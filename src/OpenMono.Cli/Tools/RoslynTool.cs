using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace OpenMono.Tools;

public sealed class RoslynTool : ToolBase, IDisposable
{
    public override string Name => "Roslyn";
    public override string Description =>
        "C# semantic code analysis via Roslyn compiler. " +
        "Analyzes reference projects (ref/) and the current workspace together. " +
        "Find references, callers, type hierarchy, diagnostics, and search symbols " +
        "with compiler-level accuracy. For C# projects only — use code-review-graph for other languages.";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    private readonly string? _referenceDirectory;

    public RoslynTool(string? referenceDirectory = null)
    {
        _referenceDirectory = referenceDirectory;
    }

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddEnum("action", "The analysis action to perform",
            "overview", "find-references", "callers", "diagnostics", "capture-baseline", "search", "type-hierarchy", "blast-radius", "get-symbol")
        .AddString("target", "Symbol name (e.g. 'MyClass', 'MyClass.MyMethod'), file path for overview/diagnostics/capture-baseline, or search query. Use '.' for project-wide.")
        .Require("action", "target");

    private readonly ConcurrentDictionary<string, HashSet<string>> _baselineCache = new();

    private AdhocWorkspace? _workspace;
    private ProjectId? _projectId;
    private CSharpCompilation? _compilation;
    private string? _cachedDir;
    private DateTime _loadedAt;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var action = input.GetProperty("action").GetString()!;
        var target = input.GetProperty("target").GetString()!;

        try
        {
            var hasRefFiles = _referenceDirectory is not null && HasCSharpFiles(_referenceDirectory);
            var hasWorkFiles = HasCSharpFiles(context.WorkingDirectory);

            if (!hasRefFiles && !hasWorkFiles)
                return ToolResult.Success(
                    "No C# files found in ref/ or the working directory. " +
                    "Roslyn analysis is not available for this project.");

            await LoadCompilationAsync(context.WorkingDirectory, ct);

            if (_compilation is null)
                return ToolResult.Success("Roslyn compilation not available.");

            return action switch
            {
                "overview" => GetOverview(target, context.WorkingDirectory),
                "find-references" => await FindReferencesAsync(target, ct),
                "callers" => await FindCallersAsync(target, ct),
                "diagnostics" => GetDiagnostics(target, context.WorkingDirectory),
                "capture-baseline" => CaptureBaseline(target, context.WorkingDirectory),
                "search" => SearchSymbols(target),
                "type-hierarchy" => GetTypeHierarchy(target),
                "blast-radius" => await GetBlastRadiusAsync(target, ct),
                "get-symbol" => GetSymbolInfo(target),
                _ => ToolResult.Error($"Unknown action '{action}'."),
            };
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Analysis cancelled.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Roslyn analysis error: {ex.Message}");
        }
    }

    private async Task LoadCompilationAsync(string workDir, CancellationToken ct)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_compilation is not null && _cachedDir == workDir && DateTime.UtcNow - _loadedAt < CacheTtl)
                return;

            _workspace?.Dispose();
            _workspace = new AdhocWorkspace();

            var csFiles = new List<string>();

            if (_referenceDirectory is not null && Directory.Exists(_referenceDirectory))
            {
                csFiles.AddRange(Directory.EnumerateFiles(_referenceDirectory, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !IsExcludedPath(f)));
            }

            csFiles.AddRange(Directory.EnumerateFiles(workDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcludedPath(f))
                .Where(f => _referenceDirectory is null || !Path.GetFullPath(f).StartsWith(Path.GetFullPath(_referenceDirectory))));

            csFiles = csFiles.Take(2000).ToList();

            if (csFiles.Count == 0)
                throw new InvalidOperationException("No .cs files found in ref/ or working directory.");

            _projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                _projectId,
                VersionStamp.Default,
                "Analysis",
                "Analysis",
                LanguageNames.CSharp,
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest));

            var solution = _workspace.CurrentSolution.AddProject(projectInfo);

            foreach (var file in csFiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var text = await File.ReadAllTextAsync(file, ct);
                    var docId = DocumentId.CreateNewId(_projectId);
                    solution = solution.AddDocument(docId,
                        Path.GetRelativePath(workDir, file),
                        SourceText.From(text, Encoding.UTF8),
                        filePath: file);
                }
                catch {}
            }

            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var refs = new List<MetadataReference>();
            foreach (var dll in Directory.EnumerateFiles(runtimeDir, "*.dll"))
            {
                try { refs.Add(MetadataReference.CreateFromFile(dll)); }
                catch {}
            }
            solution = solution.AddMetadataReferences(_projectId, refs);

            _workspace.TryApplyChanges(solution);

            var project = _workspace.CurrentSolution.GetProject(_projectId);
            _compilation = (CSharpCompilation?)await project!.GetCompilationAsync(ct);
            _cachedDir = workDir;
            _loadedAt = DateTime.UtcNow;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private ToolResult GetOverview(string target, string workDir)
    {
        var sb = new StringBuilder();
        var filePath = target == "." ? null : ResolveFilePath(target, workDir);

        if (filePath is not null)
        {
            var tree = _compilation!.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (tree is null)
                return ToolResult.Error($"File not found in compilation: {target}");

            var model = _compilation.GetSemanticModel(tree);
            sb.AppendLine($"# {Path.GetRelativePath(workDir, filePath)}");
            sb.AppendLine();

            foreach (var typeDecl in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeDecl) is INamedTypeSymbol symbol)
                    AppendTypeOverview(sb, symbol, "");
            }
        }
        else
        {
            var types = new List<INamedTypeSymbol>();
            CollectAllTypes(_compilation!.GlobalNamespace, types);

            var errors = _compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings = _compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Warning);

            sb.AppendLine("# Project Overview");
            sb.AppendLine();
            sb.AppendLine($"Files: {_compilation.SyntaxTrees.Count()}");
            sb.AppendLine($"Types: {types.Count}");
            sb.AppendLine($"Diagnostics: {errors} errors, {warnings} warnings");
            sb.AppendLine();

            foreach (var group in types
                .GroupBy(t => t.ContainingNamespace?.ToDisplayString() ?? "(global)")
                .OrderBy(g => g.Key))
            {
                sb.AppendLine($"## {group.Key}");
                foreach (var type in group.OrderBy(t => t.Name))
                    AppendTypeOverview(sb, type, "  ");
                sb.AppendLine();
            }
        }

        return ToolResult.Success(sb.ToString());
    }

    private async Task<ToolResult> FindReferencesAsync(string target, CancellationToken ct)
    {
        var symbols = ResolveSymbols(target);
        if (symbols.Count == 0)
            return ToolResult.Error($"Symbol '{target}' not found.");

        var sb = new StringBuilder();
        sb.AppendLine($"# References to '{target}'");
        sb.AppendLine();

        var totalRefs = 0;
        foreach (var symbol in symbols.Take(5))
        {
            var refs = await SymbolFinder.FindReferencesAsync(symbol, _workspace!.CurrentSolution, ct);
            foreach (var refGroup in refs)
            {
                foreach (var loc in refGroup.Locations)
                {
                    var span = loc.Location.GetLineSpan();
                    var relPath = RelPath(span.Path);
                    var line = span.StartLinePosition.Line + 1;

                    var sourceLine = loc.Location.SourceTree?.GetText(ct)
                        ?.Lines[span.StartLinePosition.Line].ToString().Trim() ?? "";

                    sb.AppendLine($"  {relPath}:{line} — {sourceLine}");
                    totalRefs++;
                }
            }
        }

        if (totalRefs == 0)
            sb.AppendLine("  (no references found)");

        return ToolResult.Success(sb.ToString());
    }

    private async Task<ToolResult> FindCallersAsync(string target, CancellationToken ct)
    {
        var symbols = ResolveSymbols(target);
        var methods = symbols.OfType<IMethodSymbol>().ToList();

        if (methods.Count == 0)
            return ToolResult.Error($"Method '{target}' not found. Specify a method name.");

        var sb = new StringBuilder();
        sb.AppendLine($"# Callers of '{target}'");
        sb.AppendLine();

        var found = 0;
        foreach (var method in methods.Take(3))
        {
            var callers = await SymbolFinder.FindCallersAsync(method, _workspace!.CurrentSolution, ct);
            foreach (var caller in callers.Where(c => c.IsDirect))
            {
                var loc = caller.CallingSymbol.Locations.FirstOrDefault();
                if (loc is null) continue;

                var span = loc.GetLineSpan();
                sb.AppendLine($"  {caller.CallingSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                sb.AppendLine($"    at {RelPath(span.Path)}:{span.StartLinePosition.Line + 1}");
                found++;
            }
        }

        if (found == 0)
            sb.AppendLine("  (no callers found)");

        return ToolResult.Success(sb.ToString());
    }

    private ToolResult CaptureBaseline(string target, string workDir)
    {
        var filePath = target == "." ? null : ResolveFilePath(target, workDir);

        IEnumerable<Diagnostic> diagnostics;
        if (filePath is not null)
        {
            var tree = _compilation!.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (tree is null)
                return ToolResult.Error($"File not found in compilation: {target}");
            diagnostics = _compilation.GetSemanticModel(tree).GetDiagnostics();
        }
        else
        {
            diagnostics = _compilation!.GetDiagnostics();
        }

        var keys = diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(DiagnosticKey)
            .ToHashSet();

        var storeKey = filePath ?? workDir;
        _baselineCache[storeKey] = keys;

        return ToolResult.Success(
            $"Baseline captured for '{target}': {keys.Count} existing diagnostic(s) recorded. " +
            "Call 'diagnostics' after your edits to see only new issues.");
    }

    private ToolResult GetDiagnostics(string target, string workDir)
    {
        IEnumerable<Diagnostic> diagnostics;
        var filePath = target == "." ? null : ResolveFilePath(target, workDir);

        if (filePath is not null)
        {
            var tree = _compilation!.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (tree is null)
                return ToolResult.Error($"File not found: {target}");
            diagnostics = _compilation.GetSemanticModel(tree).GetDiagnostics();
        }
        else
        {
            diagnostics = _compilation!.GetDiagnostics();
        }

        var relevant = diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .OrderByDescending(d => d.Severity)
            .Take(50)
            .ToList();

        var storeKey = filePath ?? workDir;
        var baselineNote = "";
        if (_baselineCache.TryGetValue(storeKey, out var baseline))
        {
            var before = relevant.Count;
            relevant = [.. relevant.Where(d => !baseline.Contains(DiagnosticKey(d)))];
            var filtered = before - relevant.Count;
            baselineNote = filtered > 0
                ? $" ({filtered} pre-existing suppressed)"
                : " (no pre-existing to suppress)";
        }

        if (relevant.Count == 0)
            return ToolResult.Success($"No new errors or warnings{baselineNote}.");

        var errors = relevant.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = relevant.Count(d => d.Severity == DiagnosticSeverity.Warning);

        var sb = new StringBuilder();
        sb.AppendLine($"# Diagnostics — {errors} errors, {warnings} warnings{baselineNote}");
        sb.AppendLine();

        foreach (var d in relevant)
        {
            var span = d.Location.GetLineSpan();
            var sev = d.Severity == DiagnosticSeverity.Error ? "ERROR" : "WARN";
            sb.AppendLine($"  [{sev}] {RelPath(span.Path)}:{span.StartLinePosition.Line + 1}: {d.GetMessage()}");
        }

        return ToolResult.Success(sb.ToString());
    }

    private static string DiagnosticKey(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        return $"{span.Path}:{span.StartLinePosition.Line}:{d.Id}:{d.GetMessage()}";
    }

    private ToolResult SearchSymbols(string pattern)
    {
        var results = new List<(ISymbol Symbol, string Location)>();
        CollectMatchingSearch(_compilation!.GlobalNamespace, pattern, results);

        if (results.Count == 0)
            return ToolResult.Error($"No symbols matching '{pattern}'.");

        var sb = new StringBuilder();
        sb.AppendLine($"# Symbols matching '{pattern}' ({results.Count} found)");
        sb.AppendLine();

        foreach (var (symbol, location) in results.Take(50))
        {
            var kind = symbol.Kind.ToString().ToLower();
            sb.AppendLine($"  {kind} {symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} — {location}");
        }

        return ToolResult.Success(sb.ToString());
    }

    private ToolResult GetTypeHierarchy(string target)
    {
        var symbols = ResolveSymbols(target);
        var types = symbols.OfType<INamedTypeSymbol>().ToList();

        if (types.Count == 0)
            return ToolResult.Error($"Type '{target}' not found.");

        var sb = new StringBuilder();

        foreach (var type in types.Take(3))
        {
            sb.AppendLine($"# Type hierarchy: {type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
            sb.AppendLine();

            sb.AppendLine("Base types:");
            var current = type.BaseType;
            var depth = 0;
            while (current is not null && current.SpecialType != SpecialType.System_Object)
            {
                sb.AppendLine($"  {new string(' ', depth * 2)}↑ {current.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                current = current.BaseType;
                depth++;
            }
            if (depth == 0) sb.AppendLine("  (none beyond System.Object)");

            if (type.AllInterfaces.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Implements:");
                foreach (var iface in type.AllInterfaces)
                    sb.AppendLine($"  • {iface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
            }

            var derived = new List<INamedTypeSymbol>();
            CollectDerivedTypes(_compilation!.GlobalNamespace, type, derived);
            if (derived.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Derived types:");
                foreach (var d in derived)
                    sb.AppendLine($"  ↓ {d.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} — {FormatLocation(d)}");
            }

            sb.AppendLine();
        }

        return ToolResult.Success(sb.ToString());
    }

    private async Task<ToolResult> GetBlastRadiusAsync(string target, CancellationToken ct)
    {
        var symbols = ResolveSymbols(target);
        if (symbols.Count == 0)
            return ToolResult.Error($"Symbol '{target}' not found.");

        var symbol = symbols[0];
        var sb = new StringBuilder();
        sb.AppendLine($"# Blast radius: {symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
        sb.AppendLine();

        var directDependents = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        var refs = await SymbolFinder.FindReferencesAsync(symbol, _workspace!.CurrentSolution, ct);
        foreach (var refGroup in refs)
        {
            foreach (var loc in refGroup.Locations)
            {
                var tree = loc.Location.SourceTree;
                if (tree is null) continue;

                var model = _compilation!.GetSemanticModel(tree);
                var node = tree.GetRoot(ct).FindNode(loc.Location.SourceSpan);

                var containingDecl = node.Ancestors().FirstOrDefault(n =>
                    n is MethodDeclarationSyntax or PropertyDeclarationSyntax
                    or ConstructorDeclarationSyntax or EventDeclarationSyntax);

                if (containingDecl is not null)
                {
                    var s = model.GetDeclaredSymbol(containingDecl);
                    if (s is not null && !SymbolEqualityComparer.Default.Equals(s, symbol))
                        directDependents.Add(s);
                }
            }
        }

        if (symbol is INamedTypeSymbol typeSymbol)
        {
            var derived = new List<INamedTypeSymbol>();
            CollectDerivedTypes(_compilation!.GlobalNamespace, typeSymbol, derived);
            foreach (var d in derived) directDependents.Add(d);
        }

        sb.AppendLine($"Direct dependents ({directDependents.Count}):");
        foreach (var dep in directDependents.Take(30))
            sb.AppendLine($"  • {dep.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} — {FormatLocation(dep)}");

        var transitive = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var dep in directDependents.Take(20))
        {
            ct.ThrowIfCancellationRequested();
            var depRefs = await SymbolFinder.FindReferencesAsync(dep, _workspace.CurrentSolution, ct);
            foreach (var refGroup in depRefs)
            {
                foreach (var loc in refGroup.Locations)
                {
                    var tree = loc.Location.SourceTree;
                    if (tree is null) continue;

                    var model = _compilation!.GetSemanticModel(tree);
                    var node = tree.GetRoot(ct).FindNode(loc.Location.SourceSpan);
                    var containingDecl = node.Ancestors().FirstOrDefault(n =>
                        n is MethodDeclarationSyntax or PropertyDeclarationSyntax);

                    if (containingDecl is not null)
                    {
                        var s = model.GetDeclaredSymbol(containingDecl);
                        if (s is not null
                            && !directDependents.Contains(s)
                            && !SymbolEqualityComparer.Default.Equals(s, symbol))
                            transitive.Add(s);
                    }
                }
            }
        }

        if (transitive.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Transitive dependents ({transitive.Count}):");
            foreach (var dep in transitive.Take(30))
                sb.AppendLine($"  ◦ {dep.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} — {FormatLocation(dep)}");
        }

        sb.AppendLine();
        sb.AppendLine($"Total impact: {directDependents.Count} direct + {transitive.Count} transitive = {directDependents.Count + transitive.Count} symbols");

        return ToolResult.Success(sb.ToString());
    }

    private ToolResult GetSymbolInfo(string target)
    {
        var symbols = ResolveSymbols(target);
        if (symbols.Count == 0)
            return ToolResult.Error($"Symbol '{target}' not found.");

        var sb = new StringBuilder();

        foreach (var symbol in symbols.Take(5))
        {
            sb.AppendLine($"# {symbol.ToDisplayString()}");
            sb.AppendLine();
            sb.AppendLine($"Kind: {symbol.Kind}");
            sb.AppendLine($"Accessibility: {symbol.DeclaredAccessibility}");
            sb.AppendLine($"Static: {symbol.IsStatic}");
            sb.AppendLine($"Abstract: {symbol.IsAbstract}");

            if (symbol.ContainingType is not null)
                sb.AppendLine($"Containing type: {symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
            if (symbol.ContainingNamespace is { IsGlobalNamespace: false })
                sb.AppendLine($"Namespace: {symbol.ContainingNamespace.ToDisplayString()}");

            switch (symbol)
            {
                case IMethodSymbol m:
                    sb.AppendLine($"Return type: {m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                    sb.AppendLine($"Parameters: {FormatParams(m)}");
                    sb.AppendLine($"Async: {m.IsAsync}");
                    sb.AppendLine($"Generic: {m.IsGenericMethod}");
                    if (m.OverriddenMethod is not null)
                        sb.AppendLine($"Overrides: {m.OverriddenMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                    break;

                case IPropertySymbol p:
                    sb.AppendLine($"Type: {p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                    sb.AppendLine($"Get: {p.GetMethod is not null}, Set: {p.SetMethod is not null}");
                    break;

                case IFieldSymbol f:
                    sb.AppendLine($"Type: {f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                    sb.AppendLine($"Readonly: {f.IsReadOnly}, Const: {f.IsConst}");
                    if (f.IsConst && f.ConstantValue is not null)
                        sb.AppendLine($"Value: {f.ConstantValue}");
                    break;

                case INamedTypeSymbol t:
                    sb.AppendLine($"Type kind: {t.TypeKind}");
                    if (t.BaseType is { SpecialType: not SpecialType.System_Object })
                        sb.AppendLine($"Base: {t.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                    if (t.Interfaces.Length > 0)
                        sb.AppendLine($"Interfaces: {string.Join(", ", t.Interfaces.Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))}");
                    sb.AppendLine($"Members: {t.GetMembers().Count(m => !m.IsImplicitlyDeclared)}");
                    break;
            }

            sb.AppendLine($"Location: {FormatLocation(symbol)}");
            sb.AppendLine();
        }

        return ToolResult.Success(sb.ToString());
    }

    private IReadOnlyList<ISymbol> ResolveSymbols(string name)
    {
        if (_compilation is null) return [];

        var results = new List<ISymbol>();
        var parts = name.Split('.');
        CollectMatchingSymbols(_compilation.GlobalNamespace, parts, results);
        return results.GroupBy(s => s.ToDisplayString()).Select(g => g.First()).ToList();
    }

    private static void CollectMatchingSymbols(
        INamespaceOrTypeSymbol container, string[] parts, List<ISymbol> results)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                CollectMatchingSymbols(ns, parts, results);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (MatchesName(type, parts))
                    results.Add(type);

                if (parts.Length >= 2 && type.Name == parts[^2])
                {
                    foreach (var typeMember in type.GetMembers())
                    {
                        if (typeMember.Name == parts[^1] && !typeMember.IsImplicitlyDeclared)
                            results.Add(typeMember);
                    }
                }
                else if (parts.Length == 1)
                {

                    foreach (var typeMember in type.GetMembers())
                    {
                        if (typeMember.Name == parts[0] && !typeMember.IsImplicitlyDeclared)
                            results.Add(typeMember);
                    }
                }

                CollectMatchingSymbols(type, parts, results);
            }
        }
    }

    private static bool MatchesName(ISymbol symbol, string[] parts)
    {
        if (parts.Length == 1)
            return symbol.Name.Equals(parts[0], StringComparison.Ordinal);

        if (parts.Length == 2)
            return symbol.Name == parts[1] && symbol.ContainingType?.Name == parts[0];

        return symbol.ToDisplayString().EndsWith(string.Join(".", parts), StringComparison.Ordinal);
    }

    private static void AppendTypeOverview(StringBuilder sb, INamedTypeSymbol type, string indent)
    {
        var kind = type.TypeKind switch
        {
            TypeKind.Class => "class",
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            _ => "type",
        };

        var bases = new List<string>();
        if (type.BaseType is { SpecialType: not SpecialType.System_Object })
            bases.Add(type.BaseType.Name);
        bases.AddRange(type.Interfaces.Select(i => i.Name));
        var baseStr = bases.Count > 0 ? $" : {string.Join(", ", bases)}" : "";

        var loc = type.Locations.FirstOrDefault();
        var lineInfo = loc is not null
            ? $" ({Path.GetFileName(loc.SourceTree?.FilePath ?? "")}:{loc.GetLineSpan().StartLinePosition.Line + 1})"
            : "";

        sb.AppendLine($"{indent}{kind} {type.Name}{baseStr}{lineInfo}");

        foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared).OrderBy(m => m.Kind))
        {
            var desc = member switch
            {
                IMethodSymbol m when m.MethodKind == MethodKind.Constructor =>
                    $"{indent}  ctor({FormatParams(m)})",
                IMethodSymbol m when m.MethodKind == MethodKind.Ordinary =>
                    $"{indent}  {m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {m.Name}({FormatParams(m)})",
                IPropertySymbol p =>
                    $"{indent}  {p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name} {{ {(p.GetMethod is not null ? "get; " : "")}{(p.SetMethod is not null ? "set; " : "")}}}",
                IFieldSymbol f =>
                    $"{indent}  {f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {f.Name}",
                IEventSymbol e =>
                    $"{indent}  event {e.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {e.Name}",
                _ => null,
            };

            if (desc is not null) sb.AppendLine(desc);
        }
    }

    private static string FormatParams(IMethodSymbol method) =>
        string.Join(", ", method.Parameters.Select(p =>
            $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}"));

    private string FormatLocation(ISymbol symbol)
    {
        var loc = symbol.Locations.FirstOrDefault();
        if (loc is null) return "unknown";
        var span = loc.GetLineSpan();
        return $"{RelPath(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    private string RelPath(string? path)
    {
        if (path is null) return "unknown";
        return _cachedDir is not null ? Path.GetRelativePath(_cachedDir, path) : path;
    }

    private static string? ResolveFilePath(string target, string workDir)
    {
        if (Path.IsPathRooted(target) && File.Exists(target)) return target;
        var combined = Path.Combine(workDir, target);
        return File.Exists(combined) ? Path.GetFullPath(combined) : null;
    }

    private static bool HasCSharpFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Any(f => !IsExcludedPath(f));
        }
        catch { return false; }
    }

    private static bool IsExcludedPath(string path)
    {
        var n = path.Replace('\\', '/');
        return n.Contains("/bin/") || n.Contains("/obj/")
            || n.Contains("/node_modules/") || n.Contains("/.git/");
    }

    private static void CollectAllTypes(INamespaceOrTypeSymbol container, List<INamedTypeSymbol> types)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
                CollectAllTypes(ns, types);
            else if (member is INamedTypeSymbol type && type.Locations.Any(l => l.IsInSource))
            {
                types.Add(type);
                CollectAllTypes(type, types);
            }
        }
    }

    private static void CollectDerivedTypes(
        INamespaceOrTypeSymbol container, INamedTypeSymbol baseType, List<INamedTypeSymbol> results)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                CollectDerivedTypes(ns, baseType, results);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (type.BaseType is not null && SymbolEqualityComparer.Default.Equals(type.BaseType, baseType))
                    results.Add(type);
                if (type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, baseType)))
                    results.Add(type);
                CollectDerivedTypes(type, baseType, results);
            }
        }
    }

    private void CollectMatchingSearch(
        INamespaceOrTypeSymbol container, string pattern, List<(ISymbol, string)> results)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                CollectMatchingSearch(ns, pattern, results);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (type.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                    && type.Locations.Any(l => l.IsInSource))
                    results.Add((type, FormatLocation(type)));

                foreach (var typeMember in type.GetMembers().Where(m => !m.IsImplicitlyDeclared))
                {
                    if (typeMember.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        results.Add((typeMember, FormatLocation(typeMember)));
                }

                CollectMatchingSearch(type, pattern, results);
            }
        }
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _loadLock.Dispose();
    }
}
