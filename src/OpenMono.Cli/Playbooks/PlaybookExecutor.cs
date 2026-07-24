using System.Text;
using System.Text.Json;
using OpenMono.Agents;
using OpenMono.Config;
using OpenMono.Hooks;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Playbooks;

public sealed class PlaybookExecutor : IDisposable
{
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly IRenderer _renderer;
    private readonly AppConfig _config;
    private readonly PermissionEngine _permissions;
    private readonly ToolDispatcher _dispatcher;
    private readonly bool _ownsDispatcher;
    private readonly SessionState _session;

    public PlaybookExecutor(
        ILlmClient llm,
        ToolRegistry tools,
        IRenderer renderer,
        AppConfig config,
        PermissionEngine permissions,
        SessionState? session = null,
        ToolDispatcher? dispatcher = null)
    {
        _llm = llm;
        _tools = tools;
        _renderer = renderer;
        _config = config;
        _permissions = permissions;

        _session = session ?? new SessionState();

        _ownsDispatcher = dispatcher is null;
        _dispatcher = dispatcher ?? new ToolDispatcher(
            tools,
            permissions,
            renderer,
            config,
            _session);
    }

    public void Dispose()
    {
        if (_ownsDispatcher)
            _dispatcher.Dispose();
    }

    public PlaybookToolPlan BuildToolPlan(PlaybookDefinition playbook)
    {
        var steps = ResolveStepOrder(playbook.Steps);
        var allToolsByName = new Dictionary<string, PlaybookPlanTool>(StringComparer.OrdinalIgnoreCase);

        var planSteps = steps.Select(s =>
        {
            var registry = BuildEffectiveToolRegistry(s, playbook);
            foreach (var tool in registry.All)
            {
                if (!allToolsByName.ContainsKey(tool.Name))
                {
                    allToolsByName[tool.Name] = new PlaybookPlanTool
                    {
                        Name = tool.Name,
                        IsReadOnly = tool.IsReadOnly,
                        Dangerous = IsDangerousTool(tool.Name),
                    };
                }
            }

            return new PlaybookPlanStep
            {
                Id = s.Id,
                Gate = s.Gate,
                Description = s.InlinePrompt is { Length: > 0 } p ? (p.Length > 120 ? p[..120] + "..." : p) : s.File,
            };
        }).ToList();

        var planTools = allToolsByName.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        return new PlaybookToolPlan
        {
            PlaybookName = playbook.Name,
            Steps = planSteps,
            Tools = planTools,
            RequiresModeSwitch = false,
        };
    }

    public async Task<string> ExecuteAsync(
        PlaybookDefinition playbook,
        Dictionary<string, object> parameters,
        PlaybookState? resumeFrom,
        string sessionId,
        CancellationToken ct)
    {

        var validationError = ParameterValidator.Validate(playbook, parameters);
        if (validationError is not null)
            return $"Parameter error: {validationError}";

        var state = resumeFrom ?? new PlaybookState
        {
            PlaybookName = playbook.Name,
            SessionId = sessionId,
            Parameters = parameters,
        };

        var plan = BuildToolPlan(playbook);
        var runId = state.SessionId;
        _permissions.PushPlaybookScope(runId, plan.Tools.Select(t => t.Name));

        string? logPath = playbook.LogOutput ? BuildLogPath(_config.DataDirectory, playbook.Name, runId) : null;
        using var log = logPath is not null ? new StreamWriter(logPath, append: true) { AutoFlush = true } : null;

        try
        {
            _renderer.WriteInfo($"Playbook: {playbook.Name} v{playbook.Version}");
            if (logPath is not null)
            {
                log!.WriteLine($"=== Playbook '{playbook.Name}' v{playbook.Version} — run {runId} — started {DateTime.UtcNow:O} ===");
                _renderer.WriteInfo($"  Logging raw output to {logPath}");
            }

            var steps = ResolveStepOrder(playbook.Steps);
            var finalOutput = new StringBuilder();
            var totalSteps = steps.Count;
            var stepNumber = 0;

            foreach (var step in steps)
            {
                stepNumber++;

                if (state.IsStepCompleted(step.Id))
                {
                    _renderer.WriteInfo($"  Step '{step.Id}' — already completed (resumed)");
                    continue;
                }

                foreach (var dep in step.Requires)
                {
                    if (!state.IsStepCompleted(dep))
                        return $"Step '{step.Id}' requires '{dep}' which is not completed.";
                }

                state.CurrentStepId = step.Id;
                var progressLabel = $"Step {stepNumber}/{totalSteps}: {step.Id}";
                _renderer.ShowToolProgress(progressLabel);
                _renderer.WriteInfo($"  Step '{step.Id}' — running...");

                var stepContent = await GetStepContentAsync(step, playbook, state, ct);
                log?.WriteLine($"\n--- [{DateTime.UtcNow:O}] Step '{step.Id}' — prompt ---\n{stepContent}");

                if (step.Gate != GateType.None && playbook.SkipPermissions)
                {
                    _renderer.WriteInfo($"  Step '{step.Id}' — gate '{step.Gate}' auto-approved (skip-permissions)");
                }
                else if (step.Gate != GateType.None)
                {
                    if (IsNonInteractiveSession())
                    {
                        var msg = $"Playbook '{playbook.Name}' aborted: gate '{step.Id}' ({step.Gate}) requires interactive confirmation.";
                        _renderer.WriteWarning($"  {msg}");
                        return msg;
                    }

                    var gateResult = await HandleGateAsync(step.Gate, step.Id, stepContent, ct);
                    if (!gateResult)
                    {
                        _renderer.WriteInfo($"  Step '{step.Id}' — skipped by user");
                        continue;
                    }
                }

                var (output, stepError) = await RunStepAsync(step, stepContent, playbook, state, progressLabel, ct);
                if (stepError is not null)
                {
                    log?.WriteLine($"--- Step '{step.Id}' — ERROR ---\n{stepError}");
                    _renderer.WriteWarning($"  Step '{step.Id}' aborted — {stepError}");
                    return $"Playbook '{playbook.Name}' aborted at step '{step.Id}'.\n{stepError}";
                }
                log?.WriteLine($"--- Step '{step.Id}' — output ---\n{output}");

                if (step.Script is not null)
                {
                    var scriptPath = Path.Combine(playbook.BasePath, step.Script);
                    if (File.Exists(scriptPath))
                    {
                        var (exit, stdout, stderr) = await Utils.ProcessRunner.RunAsync(
                            $"bash \"{scriptPath}\"", _config.WorkingDirectory, ct: ct);
                        if (exit != 0)
                        {
                            _renderer.WriteWarning($"  Step '{step.Id}' aborted — validation script failed:\n{stdout}{stderr}");
                            return $"Playbook '{playbook.Name}' aborted at step '{step.Id}'.\n{stdout}{stderr}";
                        }
                    }
                }

                state.CompleteStep(step.Id, output, step.Output);
                _renderer.WriteInfo($"  Step '{step.Id}' — done");

                await state.SaveAsync(_config.DataDirectory, ct);

                if (step == steps[^1])
                    finalOutput.Append(output);
            }

            _renderer.WriteInfo($"Playbook '{playbook.Name}' completed ({state.CompletedSteps.Count} steps)");
            log?.WriteLine($"=== Playbook '{playbook.Name}' completed {DateTime.UtcNow:O} ({state.CompletedSteps.Count} steps) ===");
            return finalOutput.Length > 0 ? finalOutput.ToString() : "Playbook completed.";
        }
        finally
        {
            _renderer.ClearToolProgress();

            try
            {
                _permissions.PopPlaybookScope(runId);
            }
            catch (InvalidOperationException ex)
            {
                _renderer.WriteWarning($"PopPlaybookScope failed — scope stack corrupted: {ex.Message}");
            }
        }
    }

    private static string BuildLogPath(string dataDirectory, string playbookName, string runId)
    {
        var dir = Path.Combine(dataDirectory, "playbook-logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{playbookName}_{runId}.log");
    }

    private async Task<string> GetStepContentAsync(
        StepDefinition step, PlaybookDefinition playbook, PlaybookState state, CancellationToken ct)
    {
        string raw;

        if (step.File is not null)
        {
            var filePath = Path.Combine(playbook.BasePath, step.File);
            raw = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, ct) : step.InlinePrompt ?? "";
        }
        else
        {
            raw = step.InlinePrompt ?? $"Execute step '{step.Id}' of the {playbook.Name} playbook.";
        }

        return await TemplateEngine.ResolveAsync(raw, state, playbook, _config.WorkingDirectory, ct);
    }

    private async Task<bool> HandleGateAsync(GateType gate, string stepId, string content, CancellationToken ct)
    {
        var preview = content.Length > 500 ? content[..500] + "..." : content;

        return gate switch
        {
            GateType.Confirm => (await _renderer.AskUserAsync(
                $"Step '{stepId}' ready. Proceed? [y/N]", ct))
                .Equals("y", StringComparison.OrdinalIgnoreCase),

            GateType.Review => (await _renderer.AskUserAsync(
                $"Step '{stepId}' preview:\n{preview}\n\nProceed? [y/N]", ct))
                .Equals("y", StringComparison.OrdinalIgnoreCase),

            GateType.Approve => (await _renderer.AskUserAsync(
                $"Step '{stepId}' requires approval:\n{preview}\n\nApprove? [y/N]", ct))
                .Equals("y", StringComparison.OrdinalIgnoreCase),

            _ => true,
        };
    }

    private async Task<(string Output, string? Error)> RunStepAsync(
        StepDefinition step, string content, PlaybookDefinition playbook, PlaybookState state, string progressLabel, CancellationToken ct)
    {

        var messages = new List<Message>
        {
            new()
            {
                Role = MessageRole.System,
                Content = playbook.RoleDescription ?? "You are a coding assistant executing a playbook step."
            },
            new() { Role = MessageRole.User, Content = content }
        };

        var effectiveTools = BuildEffectiveToolRegistry(step, playbook);

        var toolDefs = effectiveTools.BuildToolDefinitions();

        JsonElement? outputSchema = null;
        if (step.OutputSchema is not null)
        {
            var schemaPath = Path.Combine(playbook.BasePath, step.OutputSchema);
            if (File.Exists(schemaPath))
            {
                using var schemaDoc = JsonDocument.Parse(await File.ReadAllTextAsync(schemaPath, ct));
                outputSchema = schemaDoc.RootElement.Clone();

                // llama.cpp's json_schema-to-grammar converter needs a typed `properties` entry for every
                // `required` field to build a real constraint — `required` alone degrades to a near-unconstrained
                // grammar, so the model outputs whatever shape it thinks fits and validation just keeps failing.
                if (outputSchema.Value.TryGetProperty("required", out var requiredEl) &&
                    requiredEl.ValueKind == JsonValueKind.Array)
                {
                    var hasProperties = outputSchema.Value.TryGetProperty("properties", out var propsEl) &&
                        propsEl.ValueKind == JsonValueKind.Object;
                    var missingFromProperties = requiredEl.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(name => !string.IsNullOrEmpty(name) && (!hasProperties || !propsEl.TryGetProperty(name!, out _)))
                        .ToList();

                    if (missingFromProperties.Count > 0)
                    {
                        _renderer.WriteWarning(
                            $"Step '{step.Id}' output-schema '{step.OutputSchema}' lists required field(s) " +
                            $"({string.Join(", ", missingFromProperties)}) with no matching typed entry in 'properties' — " +
                            "grammar-constrained decoding will be effectively unconstrained on llama.cpp. Add a " +
                            "'properties' entry (with a type) for every required field.");
                    }
                }
            }
            else
            {
                _renderer.WriteWarning(
                    $"Step '{step.Id}' declares output-schema '{step.OutputSchema}' but the file was not found — JSON is not enforced.");
            }
        }

        var options = new LlmOptions
        {
            Model = _config.Llm.Model,
            Temperature = _config.Llm.Temperature,
            MaxTokens = _config.Llm.MaxOutputTokens,
        };

        var result = new StringBuilder();
        var lastTurnText = "";
        var maxToolLoops = 10;
        var toolLoopCount = 0;

        while (toolLoopCount < maxToolLoops)
        {
            var pendingToolCalls = new List<ToolCall>();
            var textContent = new StringBuilder();
            var receivedFirstChunk = false;
            var thinkingStarted = false;
            var thinkingCollapsed = false;
            var thinkingChars = 0;

            var indicatorShown = false;
            using var indicatorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var indicatorTask = Task.Delay(500, indicatorCts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) { _renderer.ShowWaitingIndicator(); indicatorShown = true; }
            }, TaskScheduler.Default);

            try
            {
                await foreach (var chunk in _llm.StreamChatAsync(messages, toolDefs, options, ct))
                {
                    if (!indicatorCts.IsCancellationRequested)
                    {
                        indicatorCts.Cancel();
                        if (indicatorShown) _renderer.ClearWaitingIndicator();
                    }

                    if (chunk.ThinkingDelta is not null)
                    {
                        _renderer.AppendThinking(chunk.ThinkingDelta);
                        thinkingStarted = true;
                        thinkingChars += chunk.ThinkingDelta.Length;
                        continue;
                    }

                    if (!receivedFirstChunk)
                    {
                        if (thinkingStarted && !thinkingCollapsed)
                        {
                            _renderer.CollapseThinking(thinkingChars);
                            thinkingCollapsed = true;
                        }
                        _renderer.StartAssistantResponse();
                        receivedFirstChunk = true;
                    }

                    if (chunk.TextDelta is not null)
                    {
                        textContent.Append(chunk.TextDelta);
                        _renderer.StreamText(chunk.TextDelta);
                    }

                    if (chunk.ToolCallDelta is not null)
                    {
                        var tc = chunk.ToolCallDelta;
                        if (!pendingToolCalls.Any(t => t.Id == tc.Id))
                            pendingToolCalls.Add(tc);
                    }

                    if (chunk.IsComplete) break;
                }
            }
            finally
            {
                if (!indicatorCts.IsCancellationRequested)
                    indicatorCts.Cancel();
                await indicatorTask;
                _renderer.ClearWaitingIndicator();
            }

            if (thinkingStarted && !thinkingCollapsed)
                _renderer.CollapseThinking(thinkingChars);

            _renderer.EndAssistantResponse();
            _renderer.ShowToolProgress(progressLabel);
            result.Append(textContent);
            lastTurnText = textContent.ToString();

            if (pendingToolCalls.Count == 0)
                break;

            toolLoopCount++;

            messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = textContent.ToString(),
                ToolCalls = pendingToolCalls
            });

            // HARD BLOCK: reject any tool calls outside the effective allowlist
            var disallowedCalls = pendingToolCalls
                .Where(call => !(_tools.Resolve(call.Name) is { } && effectiveTools.All.Any(t => t.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (disallowedCalls.Count > 0)
            {
                var allowedToolNames = string.Join(", ", effectiveTools.All.Select(t => t.Name).OrderBy(n => n));
                var disallowedNames = string.Join(", ", disallowedCalls.Select(c => c.Name));
                var errorMsg = $"Tool call(s) not allowed in playbook '{playbook.Name}': {disallowedNames}. " +
                               $"Allowed tools: {allowedToolNames}. Do not retry this tool.";

                // Replace disallowed calls with error results
                var resultMap = new Dictionary<string, ToolResult>();
                foreach (var call in disallowedCalls)
                {
                    resultMap[call.Id] = ToolResult.Error(errorMsg);
                }

                // Execute only the allowed calls
                var allowedCalls = pendingToolCalls.Where(c => !disallowedCalls.Any(dc => dc.Id == c.Id)).ToList();
                var allowedResults = await _dispatcher.ExecuteToolCallsAsync(allowedCalls, ct);
                var allowedIdx = 0;
                foreach (var call in allowedCalls)
                {
                    resultMap[call.Id] = allowedResults[allowedIdx++];
                }

                for (var i = 0; i < pendingToolCalls.Count; i++)
                {
                    var call = pendingToolCalls[i];
                    var toolResult = resultMap[call.Id];
                    messages.Add(new Message
                    {
                        Role = MessageRole.Tool,
                        Content = toolResult.Content,
                        ToolCallId = call.Id
                    });
                    result.AppendLine($"\n[Tool: {call.Name}]\n{toolResult.Content}");
                }
            }
            else
            {
                var toolResults = await _dispatcher.ExecuteToolCallsAsync(pendingToolCalls, ct);

                for (var i = 0; i < pendingToolCalls.Count; i++)
                {
                    var call = pendingToolCalls[i];
                    var toolResult = toolResults[i];

                    messages.Add(new Message
                    {
                        Role = MessageRole.Tool,
                        Content = toolResult.Content,
                        ToolCallId = call.Id
                    });

                    result.AppendLine($"\n[Tool: {call.Name}]\n{toolResult.Content}");
                }
            }
        }

        if (toolLoopCount >= maxToolLoops)
        {
            _renderer.WriteWarning($"Step '{step.Id}' reached maximum tool loop count ({maxToolLoops})");
        }

        if (outputSchema is { } schema)
            return await EnforceJsonOutputAsync(schema, messages, options, lastTurnText, step.Id, ct);

        return (result.ToString(), null);
    }

    /// <summary>Validates the step's final text against its output-schema; on mismatch, re-prompts the
    /// LLM for a correction — with no tools offered and the schema sent as a grammar-constrained
    /// response_format, so on llama.cpp/OpenAI-compat providers this correction is itself guaranteed
    /// to come back valid (at most one retry ever needed there). Providers that ignore response_format
    /// (e.g. Anthropic) fall back to prompt-based correction for up to 3 attempts.</summary>
    private async Task<(string Output, string? Error)> EnforceJsonOutputAsync(
        JsonElement schema,
        List<Message> messages,
        LlmOptions options,
        string candidate,
        string stepId,
        CancellationToken ct)
    {
        const int maxRetries = 3;
        var correctionOptions = options with { ResponseFormatSchema = schema };

        for (var attempt = 0; ; attempt++)
        {
            string? problem = TryExtractJson(candidate, out var json)
                ? FindMissingRequiredFields(json, schema)
                : "the response was not valid JSON";

            if (problem is null)
                return (json, null);

            if (attempt >= maxRetries)
                return (candidate, $"step '{stepId}' did not produce JSON matching its output-schema after {maxRetries} retries ({problem})");

            _renderer.WriteWarning($"  Step '{stepId}' — JSON attempt {attempt + 1} rejected: {problem}. Retrying.");

            messages.Add(new Message { Role = MessageRole.Assistant, Content = candidate });
            messages.Add(new Message
            {
                Role = MessageRole.User,
                Content = $"Your response did not satisfy the required JSON schema: {problem}. " +
                          "Return ONLY the corrected JSON — no prose, no markdown code fences, no tool calls.",
            });

            var retryText = new StringBuilder();
            await foreach (var chunk in _llm.StreamChatAsync(messages, tools: null, correctionOptions, ct))
            {
                if (chunk.TextDelta is not null)
                {
                    retryText.Append(chunk.TextDelta);
                    _renderer.StreamText(chunk.TextDelta);
                }
                if (chunk.IsComplete) break;
            }
            _renderer.EndAssistantResponse();
            candidate = retryText.ToString();
        }
    }

    /// <summary>Strips a markdown code fence if present, then narrows to the outermost {..}/[..] span.
    /// Models routinely wrap JSON in prose or fences even when asked not to.</summary>
    private static bool TryExtractJson(string text, out string json)
    {
        var trimmed = text.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && closingFence > firstNewline)
                trimmed = trimmed[(firstNewline + 1)..closingFence].Trim();
        }

        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            var start = trimmed.IndexOfAny(['{', '[']);
            if (start < 0) { json = ""; return false; }
            var closeChar = trimmed[start] == '{' ? '}' : ']';
            var end = trimmed.LastIndexOf(closeChar);
            if (end <= start) { json = ""; return false; }
            trimmed = trimmed[start..(end + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            json = trimmed;
            return true;
        }
        catch (JsonException)
        {
            json = "";
            return false;
        }
    }

    private static string? FindMissingRequiredFields(string json, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var requiredEl) ||
            requiredEl.ValueKind != JsonValueKind.Array)
            return null;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return "expected a JSON object at the top level";

        var missing = requiredEl.EnumerateArray()
            .Select(e => e.GetString())
            .Where(name => !string.IsNullOrEmpty(name) && !doc.RootElement.TryGetProperty(name!, out _))
            .ToList();

        return missing.Count > 0 ? $"missing required field(s): {string.Join(", ", missing)}" : null;
    }

    private ToolRegistry BuildEffectiveToolRegistry(StepDefinition step, PlaybookDefinition playbook)
    {
        var filtered = new ToolRegistry();

        var agentAllowedTools = step.Agent is not null && BuiltInAgents.All.TryGetValue(step.Agent, out var agentDef)
            ? agentDef.AllowedTools
            : null;

        foreach (var tool in _tools.All)
        {
            var passesPlaybook = ToolNameMatcher.IsAllowed(tool.Name, playbook.AllowedTools);
            var passesAgent = agentAllowedTools is null || ToolNameMatcher.IsAllowed(tool.Name, agentAllowedTools);

            if (passesPlaybook && passesAgent)
                filtered.Register(tool);
        }

        return filtered;
    }

    private static bool IsDangerousTool(string toolName) => toolName switch
    {
        "Bash" => true,
        "FileWrite" => true,
        "FileEdit" => true,
        "ApplyPatch" => true,
        _ => false,
    };

    private static bool IsNonInteractiveSession() =>
        Console.IsInputRedirected || Console.IsOutputRedirected;

    private static List<StepDefinition> ResolveStepOrder(StepDefinition[] steps)
    {
        var ordered = new List<StepDefinition>();
        var visited = new HashSet<string>();

        void Visit(StepDefinition step)
        {
            if (visited.Contains(step.Id)) return;
            visited.Add(step.Id);

            foreach (var dep in step.Requires)
            {
                var depStep = steps.FirstOrDefault(s => s.Id == dep);
                if (depStep is not null) Visit(depStep);
            }

            ordered.Add(step);
        }

        foreach (var step in steps) Visit(step);
        return ordered;
    }
}
