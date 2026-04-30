using System.Text.RegularExpressions;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace OpenMono.Tui.Rendering;

public enum TokenType
{
    Plain,
    Keyword,
    String,
    Number,
    Comment,
    Type,
    Function,
    Operator
}

public readonly record struct ColoredSpan(int Start, int Length, TokenType Token);

public static class SyntaxHighlighter
{

    public static TgAttribute GetAttribute(TokenType token) =>
        ThemeManager.Current.GetSyntaxAttribute(token);

    public static List<ColoredSpan> Highlight(string code, string language)
    {
        var lang = NormalizeLanguage(language);
        var patterns = GetPatterns(lang);

        if (patterns is null)
            return [new ColoredSpan(0, code.Length, TokenType.Plain)];

        return Tokenize(code, patterns);
    }

    public static string? DetectLanguage(string fenceLine)
    {
        var trimmed = fenceLine.TrimStart('`').Trim();
        return trimmed.Length > 0 ? NormalizeLanguage(trimmed) : null;
    }

    private static List<ColoredSpan> Tokenize(string code, List<(Regex Pattern, TokenType Token)> patterns)
    {

        var spans = new List<ColoredSpan>();
        var covered = new bool[code.Length];

        foreach (var (pattern, tokenType) in patterns)
        {
            foreach (Match match in pattern.Matches(code))
            {

                var group = match.Groups.Count > 1 && match.Groups[1].Success
                    ? match.Groups[1]
                    : match.Groups[0];

                var start = group.Index;
                var length = group.Length;

                var overlap = false;
                for (var i = start; i < start + length && i < covered.Length; i++)
                {
                    if (covered[i]) { overlap = true; break; }
                }
                if (overlap) continue;

                for (var i = start; i < start + length && i < covered.Length; i++)
                    covered[i] = true;

                spans.Add(new ColoredSpan(start, length, tokenType));
            }
        }

        var pos = 0;
        var result = new List<ColoredSpan>();
        var sorted = spans.OrderBy(s => s.Start).ToList();

        foreach (var span in sorted)
        {
            if (span.Start > pos)
                result.Add(new ColoredSpan(pos, span.Start - pos, TokenType.Plain));
            result.Add(span);
            pos = span.Start + span.Length;
        }

        if (pos < code.Length)
            result.Add(new ColoredSpan(pos, code.Length - pos, TokenType.Plain));

        return result;
    }

    private static string NormalizeLanguage(string lang) => lang.ToLowerInvariant() switch
    {
        "cs" or "csharp" or "c#" => "csharp",
        "ts" or "typescript" => "typescript",
        "js" or "javascript" => "javascript",
        "py" or "python" => "python",
        "go" or "golang" => "go",
        "rs" or "rust" => "rust",
        "sql" => "sql",
        "json" => "json",
        "yaml" or "yml" => "yaml",
        "bash" or "sh" or "shell" or "zsh" => "bash",
        _ => lang.ToLowerInvariant()
    };

    private static List<(Regex, TokenType)>? GetPatterns(string lang) => lang switch
    {
        "csharp"     => CSharpPatterns,
        "typescript" => TypeScriptPatterns,
        "javascript" => JavaScriptPatterns,
        "python"     => PythonPatterns,
        "go"         => GoPatterns,
        "rust"       => RustPatterns,
        "sql"        => SqlPatterns,
        "json"       => JsonPatterns,
        "yaml"       => YamlPatterns,
        "bash"       => BashPatterns,
        _            => null
    };

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.Multiline;

    private static readonly List<(Regex, TokenType)> CSharpPatterns =
    [

        (new Regex(@"//[^\n]*", Opts), TokenType.Comment),
        (new Regex(@"/\*[\s\S]*?\*/", Opts), TokenType.Comment),

        (new Regex(@"@""(?:""""|[^""])*""", Opts), TokenType.String),
        (new Regex(@"\$""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"'(?:[^'\\]|\\.)'", Opts), TokenType.String),

        (new Regex(@"\b(?:abstract|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|private|protected|public|readonly|record|ref|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|when|where|while|yield|required|init|get|set|value|global|not|and|or|with)\b", Opts), TokenType.Keyword),

        (new Regex(@"\b[A-Z][a-zA-Z0-9]*(?:<[^>]+>)?(?=\s+\w)", Opts), TokenType.Type),

        (new Regex(@"\b([a-zA-Z_]\w*)\s*(?=\()", Opts), TokenType.Function),

        (new Regex(@"\b(?:0x[0-9a-fA-F_]+|0b[01_]+|\d[\d_]*\.?\d*(?:[eE][+-]?\d+)?[fFdDmMlLuU]*)\b", Opts), TokenType.Number),

        (new Regex(@"[+\-*/%=!<>&|^~?:]+|=>", Opts), TokenType.Operator),
    ];

    private static readonly List<(Regex, TokenType)> TypeScriptPatterns =
    [
        (new Regex(@"//[^\n]*", Opts), TokenType.Comment),
        (new Regex(@"/\*[\s\S]*?\*/", Opts), TokenType.Comment),
        (new Regex(@"`(?:[^`\\]|\\.)*`", Opts), TokenType.String),
        (new Regex(@"""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"'(?:[^'\\]|\\.)*'", Opts), TokenType.String),
        (new Regex(@"\b(?:abstract|any|as|async|await|bigint|boolean|break|case|catch|class|const|constructor|continue|debugger|declare|default|delete|do|else|enum|export|extends|false|finally|for|from|function|get|if|implements|import|in|infer|instanceof|interface|is|keyof|let|module|namespace|never|new|null|number|object|of|package|private|protected|public|readonly|require|return|satisfies|set|static|string|super|switch|symbol|this|throw|true|try|type|typeof|undefined|unique|unknown|var|void|while|with|yield)\b", Opts), TokenType.Keyword),
        (new Regex(@"\b[A-Z][a-zA-Z0-9]*(?:<[^>]+>)?", Opts), TokenType.Type),
        (new Regex(@"\b([a-zA-Z_$]\w*)\s*(?=\()", Opts), TokenType.Function),
        (new Regex(@"\b(?:0x[0-9a-fA-F]+|0o[0-7]+|0b[01]+|\d+\.?\d*(?:[eE][+-]?\d+)?n?)\b", Opts), TokenType.Number),
        (new Regex(@"[+\-*/%=!<>&|^~?:]+|=>", Opts), TokenType.Operator),
    ];

    private static readonly List<(Regex, TokenType)> JavaScriptPatterns = TypeScriptPatterns;

    private static readonly List<(Regex, TokenType)> PythonPatterns =
    [
        (new Regex(@"#[^\n]*", Opts), TokenType.Comment),
        (new Regex("\"\"\"[\\s\\S]*?\"\"\"", Opts), TokenType.String),
        (new Regex(@"'''[\s\S]*?'''", Opts), TokenType.String),
        (new Regex(@"f""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"f'(?:[^'\\]|\\.)*'", Opts), TokenType.String),
        (new Regex(@"""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"'(?:[^'\\]|\\.)*'", Opts), TokenType.String),
        (new Regex(@"\b(?:False|None|True|and|as|assert|async|await|break|class|continue|def|del|elif|else|except|finally|for|from|global|if|import|in|is|lambda|nonlocal|not|or|pass|raise|return|try|while|with|yield)\b", Opts), TokenType.Keyword),
        (new Regex(@"\b(?:int|float|str|bool|list|dict|tuple|set|bytes|type|object|Exception)\b", Opts), TokenType.Type),
        (new Regex(@"\b([a-zA-Z_]\w*)\s*(?=\()", Opts), TokenType.Function),
        (new Regex(@"(?<=def\s+)([a-zA-Z_]\w*)", Opts), TokenType.Function),
        (new Regex(@"\b(?:0x[0-9a-fA-F]+|0o[0-7]+|0b[01]+|\d+\.?\d*(?:[eE][+-]?\d+)?j?)\b", Opts), TokenType.Number),
        (new Regex(@"[+\-*/%=!<>&|^~@:]+|->", Opts), TokenType.Operator),
    ];

    private static readonly List<(Regex, TokenType)> GoPatterns =
    [
        (new Regex(@"//[^\n]*", Opts), TokenType.Comment),
        (new Regex(@"/\*[\s\S]*?\*/", Opts), TokenType.Comment),
        (new Regex(@"`[^`]*`", Opts), TokenType.String),
        (new Regex(@"""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"'(?:[^'\\]|\\.)*'", Opts), TokenType.String),
        (new Regex(@"\b(?:break|case|chan|const|continue|default|defer|else|fallthrough|for|func|go|goto|if|import|interface|map|package|range|return|select|struct|switch|type|var)\b", Opts), TokenType.Keyword),
        (new Regex(@"\b(?:bool|byte|complex64|complex128|error|float32|float64|int|int8|int16|int32|int64|rune|string|uint|uint8|uint16|uint32|uint64|uintptr|any)\b", Opts), TokenType.Type),
        (new Regex(@"\b([a-zA-Z_]\w*)\s*(?=\()", Opts), TokenType.Function),
        (new Regex(@"\b(?:0x[0-9a-fA-F]+|0o[0-7]+|0b[01]+|\d+\.?\d*(?:[eE][+-]?\d+)?)\b", Opts), TokenType.Number),
        (new Regex(@"[+\-*/%=!<>&|^~:]+|<-|:=", Opts), TokenType.Operator),
    ];

    private static readonly List<(Regex, TokenType)> RustPatterns =
    [
        (new Regex(@"//[^\n]*", Opts), TokenType.Comment),
        (new Regex(@"/\*[\s\S]*?\*/", Opts), TokenType.Comment),
        (new Regex(@"""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"'(?:[^'\\]|\\.)'", Opts), TokenType.String),
        (new Regex(@"\b(?:as|async|await|break|const|continue|crate|dyn|else|enum|extern|false|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|Self|static|struct|super|trait|true|type|unsafe|use|where|while|yield)\b", Opts), TokenType.Keyword),
        (new Regex(@"\b(?:bool|char|f32|f64|i8|i16|i32|i64|i128|isize|str|u8|u16|u32|u64|u128|usize|String|Vec|Option|Result|Box|Rc|Arc)\b", Opts), TokenType.Type),
        (new Regex(@"\b([a-zA-Z_]\w*)\s*(?=\()", Opts), TokenType.Function),
        (new Regex(@"\b(?:0x[0-9a-fA-F_]+|0o[0-7_]+|0b[01_]+|\d[\d_]*\.?\d*(?:[eE][+-]?\d+)?)\b", Opts), TokenType.Number),
        (new Regex(@"[+\-*/%=!<>&|^~?:]+|=>|->", Opts), TokenType.Operator),
    ];

    private static readonly List<(Regex, TokenType)> SqlPatterns =
    [
        (new Regex(@"--[^\n]*", Opts), TokenType.Comment),
        (new Regex(@"/\*[\s\S]*?\*/", Opts), TokenType.Comment),
        (new Regex(@"'(?:[^'\\]|\\.)*'", Opts), TokenType.String),
        (new Regex(@"\b(?:SELECT|FROM|WHERE|AND|OR|NOT|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|INDEX|JOIN|INNER|LEFT|RIGHT|OUTER|FULL|CROSS|ON|AS|IN|EXISTS|BETWEEN|LIKE|IS|NULL|ORDER|BY|GROUP|HAVING|LIMIT|OFFSET|UNION|ALL|DISTINCT|CASE|WHEN|THEN|ELSE|END|BEGIN|COMMIT|ROLLBACK|GRANT|REVOKE|WITH|PRIMARY|KEY|FOREIGN|REFERENCES|UNIQUE|CHECK|DEFAULT|CONSTRAINT|CASCADE)\b", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase), TokenType.Keyword),
        (new Regex(@"\b(?:INT|INTEGER|BIGINT|SMALLINT|TINYINT|FLOAT|DOUBLE|DECIMAL|NUMERIC|VARCHAR|CHAR|TEXT|BLOB|DATE|DATETIME|TIMESTAMP|BOOLEAN|SERIAL|UUID)\b", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase), TokenType.Type),
        (new Regex(@"\b([a-zA-Z_]\w*)\s*(?=\()", Opts), TokenType.Function),
        (new Regex(@"\b\d+\.?\d*\b", Opts), TokenType.Number),
        (new Regex(@"[=<>!]+|[+\-*/%]", Opts), TokenType.Operator),
    ];

    private static readonly List<(Regex, TokenType)> JsonPatterns =
    [
        (new Regex(@"""(?:[^""\\]|\\.)*""\s*(?=:)", Opts), TokenType.Keyword),
        (new Regex(@"""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"\b(?:true|false|null)\b", Opts), TokenType.Keyword),
        (new Regex(@"-?\b\d+\.?\d*(?:[eE][+-]?\d+)?\b", Opts), TokenType.Number),
    ];

    private static readonly List<(Regex, TokenType)> YamlPatterns =
    [
        (new Regex(@"#[^\n]*", Opts), TokenType.Comment),
        (new Regex(@"""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"'(?:[^'\\]|\\.)*'", Opts), TokenType.String),
        (new Regex(@"^[\w.-]+(?=\s*:)", Opts), TokenType.Keyword),
        (new Regex(@"\b(?:true|false|null|yes|no|on|off)\b", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase), TokenType.Keyword),
        (new Regex(@"-?\b\d+\.?\d*\b", Opts), TokenType.Number),
    ];

    private static readonly List<(Regex, TokenType)> BashPatterns =
    [
        (new Regex(@"#[^\n]*", Opts), TokenType.Comment),
        (new Regex(@"""(?:[^""\\]|\\.)*""", Opts), TokenType.String),
        (new Regex(@"'[^']*'", Opts), TokenType.String),
        (new Regex(@"\$\{[^}]*\}|\$\w+", Opts), TokenType.Type),
        (new Regex(@"\b(?:if|then|else|elif|fi|for|while|do|done|case|esac|in|function|return|exit|local|export|source|alias|unalias|set|unset|readonly|declare|typeset|shift|trap|eval|exec|break|continue|select|until|coproc)\b", Opts), TokenType.Keyword),
        (new Regex(@"\b([a-zA-Z_]\w*)\s*(?=\()", Opts), TokenType.Function),
        (new Regex(@"\b\d+\b", Opts), TokenType.Number),
        (new Regex(@"[|&;><]+|&&|\|\||>>|<<", Opts), TokenType.Operator),
    ];
}
