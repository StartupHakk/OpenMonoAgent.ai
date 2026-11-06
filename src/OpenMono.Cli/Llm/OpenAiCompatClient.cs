using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMono.Config;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Llm;

public sealed class OpenAiCompatClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private const int MaxRetries = 3;

    private static SemaphoreSlim? _requestGate;
    private static int _gateCapacity;
    private static readonly object _gateInitLock = new();
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
    ];

    public string? ApiKey { get; init; }
    public Action<string>? OnDebug { get; set; }
    public Action<string>? OnModelReported { get; set; }
    public Action<string>? OnWarning { get; set; }
    private string? _reportedModel;

    private readonly string _model;
    private readonly bool _foundry;
    // Structured `tools` in the request body. Starts from OPENMONO_TOOL_CALLING and is
    // switched off for the rest of the session when Foundry refuses it for the served
    // variant; tools are then described in the prompt and parsed from the reply text.
    private bool _toolCalling;

    public bool StructuredToolCalling => _toolCalling;

    public OpenAiCompatClient(LlmConfig config)
        : this(config, new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
    {
    }

    internal OpenAiCompatClient(LlmConfig config, HttpClient http)
    {
        _endpoint = config.Endpoint.TrimEnd('/');
        _model = config.Model;
        _foundry = config.IsFoundry;
        _toolCalling = config.ToolCalling ?? true;
        _http = http;
        EnsureRequestGate(config.MaxConcurrentRequests);
    }

    private static SemaphoreSlim EnsureRequestGate(int requested)
    {
        var capacity = Math.Max(1, requested);
        if (_requestGate is { } existing && _gateCapacity == capacity)
            return existing;
        lock (_gateInitLock)
        {
            if (_requestGate is null || _gateCapacity != capacity)
            {
                _requestGate?.Dispose();
                _requestGate = new SemaphoreSlim(capacity, capacity);
                _gateCapacity = capacity;
            }
            return _requestGate;
        }
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var gate = _requestGate!;
        await gate.WaitAsync(ct);
        try
        {
        var lastException = default(Exception);
        TimeSpan? pendingRetryAfter = null;
        var retryDelay = TimeSpan.Zero;
        var toolFallbackUsed = false;
        var skipDelay = false;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            HttpResponseMessage? response = null;
            var yieldedToCaller = false;
            string? retryableStreamError = null;
            var retryWithoutTools = false;
            ct.ThrowIfCancellationRequested();

            if (attempt > 0 && !skipDelay)
            {
                retryDelay = RetryPolicy.NextDelay(attempt, pendingRetryAfter, Random.Shared.NextDouble());
                await Task.Delay(retryDelay, ct);
            }

            var requestBody = BuildRequestBody(messages, tools, options, _model, _foundry, _toolCalling);
            var requestJson = JsonSerializer.Serialize(requestBody, JsonOptions.Default);

            if (attempt == 0)
            {
                var toolCount = tools?.ValueKind == JsonValueKind.Array ? tools.Value.GetArrayLength() : 0;
                OnDebug?.Invoke($"[LLM] POST {_endpoint}/v1/chat/completions");
                var resolvedModel = string.IsNullOrEmpty(options.Model) ? _model : options.Model;
                OnDebug?.Invoke($"[LLM] Model: {resolvedModel} | Messages: {messages.Count} | Tools: {toolCount} | MaxTokens: {options.MaxTokens}");
                Log.Debug($"LLM request: model={resolvedModel} messages={messages.Count} tools={toolCount} endpoint={_endpoint}");

            }
            else if (skipDelay)
            {
                OnDebug?.Invoke("[LLM] Retrying the turn without tools");
            }
            else
            {
                OnDebug?.Invoke($"[LLM] Retry {attempt}/{MaxRetries} after {retryDelay.TotalSeconds:F1}s");
                Log.Warn($"LLM retry {attempt}/{MaxRetries} after {retryDelay.TotalSeconds:F1}s");
            }

            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/v1/chat/completions")
            {
                Content = content,
            };

            if (ApiKey is not null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

            skipDelay = false;
            LlmRequestRejectedException? rejected = null;
            try
            {
                response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.StatusCode is System.Net.HttpStatusCode.InternalServerError
                    or System.Net.HttpStatusCode.RequestEntityTooLarge)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (ContextOverflowException.IsOverflow(body, (int)response.StatusCode))
                    {
                        Log.Warn($"[Overflow] Provider rejected request as context overflow (HTTP {(int)response.StatusCode}): {Truncate(body, 200)}");
                        response.Dispose();
                        throw new ContextOverflowException(body.Length > 300 ? body[..300] : body);
                    }
                    response.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    // Foundry answers 500 for a request it cannot serve (Echo12 saw the
                    // tool-marker error this way); the same body fails again.
                    if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                        && (_foundry || LlmProviderErrorException.IsToolMarkerError(body)))
                    {
                        Log.Warn($"LLM request failed (HTTP 500): {Truncate(body, 500)}");
                        response.Dispose();
                        response = null;
                        if (LlmProviderErrorException.IsToolMarkerError(body) && CanDropTools(tools, toolFallbackUsed))
                        {
                            DisableTools(body);
                            retryWithoutTools = true;
                        }
                        else
                        {
                            rejected = new LlmRequestRejectedException(body, System.Net.HttpStatusCode.InternalServerError);
                        }
                    }
                }

                // A 400 is the same on every attempt: keep the server's reason
                // (unsupported field, model cannot take tools, ...) and do not retry.
                if (response?.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    Log.Warn($"LLM request rejected (HTTP 400): {Truncate(body, 500)}");
                    response.Dispose();
                    response = null;
                    if (LlmProviderErrorException.IsToolMarkerError(body) && CanDropTools(tools, toolFallbackUsed))
                    {
                        DisableTools(body);
                        retryWithoutTools = true;
                    }
                    else
                    {
                        rejected = new LlmRequestRejectedException(body);
                    }
                }
                else if (response is not null && IsRetryableStatus(response.StatusCode))
                {
                    lastException = new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                        inner: null, response.StatusCode);
                    pendingRetryAfter = RetryPolicy.ParseRetryAfter(response);
                    response.Dispose();
                    response = null;
                    continue;
                }

                response?.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                pendingRetryAfter = null;
                response?.Dispose();
                response = null;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {

                lastException = ex;
                pendingRetryAfter = null;
                response?.Dispose();
                response = null;
            }

            if (rejected is not null)
                throw rejected;

            if (retryWithoutTools)
            {
                toolFallbackUsed = true;
                skipDelay = true;
                attempt--;
                continue;
            }

            if (response is null)
                continue;

            var streamStarted = System.Diagnostics.Stopwatch.StartNew();
            var chunkCount = 0;

            using (response)
            {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var toolCalls = new Dictionary<int, ToolCallAccumulator>();
            var malformedChunks = 0;
            var fullText = new StringBuilder();
            var emittedText = 0;
            var suppressText = false;
            var outputTruncated = false;
            string? sseEvent = null;

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    sseEvent = line["event:".Length..].Trim();
                    continue;
                }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var data = line.StartsWith("data: ", StringComparison.Ordinal) ? line["data: ".Length..] : line["data:".Length..];
                var errorEvent = string.Equals(sseEvent, "error", StringComparison.OrdinalIgnoreCase);
                sseEvent = null;
                if (data == "[DONE]")
                {
                    foreach (var tc in toolCalls.Values.Where(t => t.IsComplete))
                    {
                        var args = tc.Arguments.ToString();

                        // If the model hit max_tokens mid-JSON, the accumulated arguments are
                        // truncated. Storing broken JSON in history makes the next request 400,
                        // so drop the call and flag truncation instead.
                        if (outputTruncated || !MessageSanitizer.IsValidJsonObject(args))
                        {
                            outputTruncated = true;
                            OnDebug?.Invoke($"[SSE] tool_call DROPPED (truncated/invalid JSON): {tc.Name}");
                            Log.Warn($"[OMA_TOOLCALL] Dropping truncated tool call: {tc.Name} args_len={args.Length}");
                            continue;
                        }

                        OnDebug?.Invoke($"[SSE] tool_call: {tc.Name} {{ {args[..Math.Min(100, args.Length)]} }}");
                        Log.Info($"[OMA_TOOLCALL] LLM generated tool call: {tc.Name} args={args[..Math.Min(200, args.Length)]}");

                        yield return new StreamChunk
                        {
                            ToolCallDelta = new ToolCall
                            {
                                Id = tc.Id,
                                Name = tc.Name,
                                Arguments = args,
                            }
                        };
                    }

                    var elapsed = streamStarted.Elapsed;
                    OnDebug?.Invoke($"[LLM] Stream complete — {chunkCount} chunks in {elapsed.TotalSeconds:F1}s");
                    Log.Info($"[OMA_LLM] Stream complete: chunks={chunkCount} tool_calls={toolCalls.Count} elapsed={elapsed.TotalSeconds:F1}s");

                    if (toolCalls.Count == 0 && suppressText)
                    {
                        var textCalls = FoundryTextTools.Parse(fullText.ToString(emittedText, fullText.Length - emittedText), tools, _foundry);
                        if (textCalls.Count == 0)
                        {
                            // Unparseable markup: show it rather than lose the reply.
                            yield return new StreamChunk { TextDelta = fullText.ToString(emittedText, fullText.Length - emittedText) };
                        }

                        var idx = 0;
                        foreach (var (name, args) in textCalls)
                        {
                            Log.Info($"[OMA_TOOLCALL] Text tool call: {name} args={Truncate(args, 200)}");
                            yield return new StreamChunk
                            {
                                ToolCallDelta = new ToolCall
                                {
                                    Id = $"call_xml_{idx++}",
                                    Name = name,
                                    Arguments = args,
                                }
                            };
                        }
                    }
                    else if (!suppressText && emittedText < fullText.Length)
                    {
                        yield return new StreamChunk { TextDelta = fullText.ToString(emittedText, fullText.Length - emittedText) };
                    }

                    if (outputTruncated)
                        Log.Warn("[OMA_LLM] Output truncated at max_tokens — incomplete tool calls dropped");

                    yield return new StreamChunk { IsComplete = true, OutputTruncated = outputTruncated };
                    yield break;
                }

                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(data);
                }
                catch (JsonException)
                {
                    if (!errorEvent)
                    {
                        malformedChunks++;
                        if (malformedChunks > 50)
                            throw new InvalidOperationException(
                                $"Too many malformed SSE chunks ({malformedChunks}). Stream is corrupt.");
                        continue;
                    }
                }

                if (StreamErrorMessage(doc?.RootElement, data, errorEvent) is { } errorMsg)
                {
                    doc?.Dispose();

                    // Deterministic: the same body fails again. Switch to text tools once
                    // for the session and retry the turn, otherwise surface it unretried.
                    if (LlmProviderErrorException.IsToolMarkerError(errorMsg))
                    {
                        if (!yieldedToCaller && CanDropTools(tools, toolFallbackUsed))
                        {
                            DisableTools(errorMsg);
                            retryWithoutTools = true;
                            break;
                        }

                        throw new LlmProviderErrorException(errorMsg);
                    }

                    if (ContextOverflowException.IsOverflow(errorMsg))
                    {
                        Log.Warn($"[Overflow] Provider reported context overflow mid-stream: {Truncate(errorMsg, 200)}");
                        throw new ContextOverflowException(errorMsg);
                    }

                    // Foundry reports request errors this way (unsupported parameter, ...);
                    // resending the same body only repeats them.
                    if (!_foundry && !yieldedToCaller && attempt < MaxRetries)
                    {
                        retryableStreamError = errorMsg;
                        break;
                    }

                    throw new LlmProviderErrorException(errorMsg);
                }

                using (doc!)
                {
                    var root = doc!.RootElement;

                    chunkCount++;

                    if (root.TryGetProperty("model", out var modelEl) &&
                        modelEl.ValueKind == JsonValueKind.String &&
                        modelEl.GetString() is { Length: > 0 } reported &&
                        reported != _reportedModel)
                    {
                        _reportedModel = reported;
                        OnModelReported?.Invoke(reported);
                    }

                    UsageInfo? usage = null;
                    if (root.TryGetProperty("usage", out var usageEl) &&
                        usageEl.ValueKind == JsonValueKind.Object)
                    {
                        // llama.cpp appends a `timings` block to the same final chunk that carries `usage`.
                        // predicted_per_second = live generation rate; predicted_n/ms feed the rolling average.
                        int predictedN = 0; double predictedMs = 0, predictedPerSec = 0;
                        if (root.TryGetProperty("timings", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
                        {
                            if (tEl.TryGetProperty("predicted_n", out var pn)) predictedN = pn.GetInt32();
                            if (tEl.TryGetProperty("predicted_ms", out var pms)) predictedMs = pms.GetDouble();
                            if (tEl.TryGetProperty("predicted_per_second", out var pps)) predictedPerSec = pps.GetDouble();
                        }
                        usage = new UsageInfo
                        {
                            PromptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                            CompletionTokens = usageEl.TryGetProperty("completion_tokens", out var cpt) ? cpt.GetInt32() : 0,
                            CachedTokens = usageEl.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object
                                && ptd.TryGetProperty("cached_tokens", out var ctdel) ? ctdel.GetInt32() : 0,
                            PredictedTokens = predictedN,
                            PredictedMs = predictedMs,
                            PredictedPerSecond = predictedPerSec,
                        };
                        var usageMsg = $"[SSE] usage: prompt={usage.PromptTokens} completion={usage.CompletionTokens} cached={usage.CachedTokens} total={usage.TotalTokens} gen_tps={usage.PredictedPerSecond:F1}";
                        OnDebug?.Invoke(usageMsg);
                        Log.Info(usageMsg);
                    }

                    if (!root.TryGetProperty("choices", out var choices)) continue;

                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (!choice.TryGetProperty("delta", out var delta)) continue;

                        if (delta.TryGetProperty("reasoning_content", out var reasoningEl) &&
                            reasoningEl.ValueKind == JsonValueKind.String)
                        {
                            var thinking = reasoningEl.GetString();
                            if (!string.IsNullOrEmpty(thinking))
                            {
                                yieldedToCaller = true;
                                yield return new StreamChunk { ThinkingDelta = thinking };
                            }
                        }

                        if (delta.TryGetProperty("content", out var contentEl) &&
                            contentEl.ValueKind == JsonValueKind.String)
                        {
                            var text = contentEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                fullText.Append(text);
                                if (!suppressText)
                                {
                                    var pending = fullText.ToString(emittedText, fullText.Length - emittedText);
                                    var marker = FoundryTextTools.MarkerIndex(pending, _foundry);
                                    var emit = marker >= 0
                                        ? pending[..marker]
                                        : pending[..^FoundryTextTools.PartialMarkerLength(pending, _foundry)];
                                    suppressText = marker >= 0;
                                    if (emit.Length > 0)
                                    {
                                        emittedText += emit.Length;
                                        yieldedToCaller = true;
                                        yield return new StreamChunk { TextDelta = emit, Usage = usage };
                                    }
                                }
                            }
                        }

                        if (delta.TryGetProperty("tool_calls", out var toolCallsEl))
                        {
                            foreach (var tc in toolCallsEl.EnumerateArray())
                            {
                                var index = tc.GetProperty("index").GetInt32();
                                if (!toolCalls.TryGetValue(index, out var acc))
                                {
                                    acc = new ToolCallAccumulator();
                                    toolCalls[index] = acc;
                                }

                                if (tc.TryGetProperty("id", out var idEl))
                                    acc.Id = idEl.GetString() ?? $"call_{index}";

                                if (tc.TryGetProperty("function", out var fn))
                                {
                                    if (fn.TryGetProperty("name", out var nameEl))
                                        acc.Name = nameEl.GetString() ?? "";
                                    if (fn.TryGetProperty("arguments", out var argsEl))
                                        acc.Arguments.Append(argsEl.GetString() ?? "");
                                }

                                acc.IsComplete = true;
                                if (!string.IsNullOrEmpty(acc.Name))
                                    yield return new StreamChunk { ToolCallProgress = acc.Name };
                            }
                        }

                        if (choice.TryGetProperty("finish_reason", out var fr) &&
                            fr.ValueKind == JsonValueKind.String)
                        {
                            var reason = fr.GetString();
                            if (reason == "tool_calls")
                            {
                                foreach (var tc in toolCalls.Values)
                                    tc.IsComplete = true;
                            }
                            else if (reason is "length" or "max_tokens")
                            {
                                outputTruncated = true;
                                OnDebug?.Invoke($"[LLM] finish_reason={reason} — output hit the token limit");
                            }
                        }
                    }

                    if (usage is not null)
                        yield return new StreamChunk { Usage = usage };
                }
            }
            }

            if (retryWithoutTools)
            {
                toolFallbackUsed = true;
                skipDelay = true;
                attempt--;
                continue;
            }

            if (retryableStreamError is not null)
            {
                lastException = new LlmProviderErrorException(retryableStreamError);
                pendingRetryAfter = null;
                continue;
            }

            yield break;
        }

        throw lastException ?? new HttpRequestException("Failed to connect after retries");
        }
        finally
        {
            gate.Release();
        }
    }

    private bool CanDropTools(JsonElement? tools, bool alreadyDropped) =>
        _foundry && _toolCalling && !alreadyDropped && FoundryTextTools.HasTools(tools);

    private void DisableTools(string? reason)
    {
        _toolCalling = false;
        Log.Warn($"[OMA_LLM] Foundry refused structured tool calling; using text tool calls for this session: {Truncate(reason, 300)}");
        OnDebug?.Invoke("[LLM] Foundry refused structured tool calling; tools go in the prompt for this session");
        OnWarning?.Invoke($"Foundry Local refused structured tool calling for {_model} (the variant publishes no tool markers). "
            + "This session describes the tools in the prompt instead and reads tool calls from the reply text.");
    }

    // An SSE error: {"error": "..."}, {"error": {"message": ...}}, or an `event: error`
    // frame whose data is the error itself (JSON or plain text).
    internal static string? StreamErrorMessage(JsonElement? root, string data, bool errorEvent)
    {
        if (root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("error", out var errorEl))
        {
            return errorEl.ValueKind switch
            {
                JsonValueKind.String => errorEl.GetString() ?? "Unknown API error",
                JsonValueKind.Object when errorEl.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    => m.GetString() ?? "Unknown API error",
                JsonValueKind.Object => errorEl.GetRawText(),
                _ => "Unknown API error",
            };
        }

        if (!errorEvent)
            return null;
        if (root is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            return msg.GetString() ?? "Unknown API error";
        if (root is { ValueKind: JsonValueKind.String } str)
            return str.GetString() ?? "Unknown API error";
        return string.IsNullOrWhiteSpace(data) ? "Unknown API error" : data.Trim();
    }

    private static bool IsRetryableStatus(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    private static string Truncate(string? s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    internal static object BuildRequestBody(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        string configModel,
        bool foundry = false,
        bool toolCalling = true)
    {
        var model = string.IsNullOrEmpty(options.Model) ? configModel : options.Model;

        // Old sessions may hold tool-call arguments truncated mid-JSON (max_tokens cutoff),
        // which providers reject with a 400. Sanitize before serializing.
        messages = MessageSanitizer.SanitizeForRequest(messages);

        // Log message roles being sent to API
        var msgRoles = string.Join(",", messages.Select(m => m.Role.ToString()[0]));
        var systemCount = messages.Count(m => m.Role == MessageRole.System);
        Log.Info($"[OMA_LLM_REQUEST] Building request with {messages.Count} messages: {msgRoles} (system={systemCount})");

        var apiMessages = foundry && !toolCalling && FoundryTextTools.HasTools(tools)
            ? FoundryTextTools.RewriteMessages(messages, tools!.Value, ToApiMessage)
            : messages.Select(ToApiMessage).ToList();

        if (foundry)
            return BuildFoundryBody(model, apiMessages, tools, options, toolCalling);

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = apiMessages,
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxTokens,
            ["top_p"] = options.TopP,
            ["top_k"] = options.TopK,
            ["presence_penalty"] = options.PresencePenalty,
            ["min_p"] = options.MinP,
            ["repetition_penalty"] = options.RepetitionPenalty,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
        };

        if (options.EnableThinking.HasValue)
        {
            var kwargs = new Dictionary<string, object?>
            {
                ["enable_thinking"] = options.EnableThinking.Value,
            };
            if (options.ReasoningEffort is { Length: > 0 })
                kwargs["reasoning_effort"] = options.ReasoningEffort;
            if (options.PreserveThinking.HasValue)
                kwargs["preserve_thinking"] = options.PreserveThinking.Value;
            body["chat_template_kwargs"] = kwargs;
        }

        // Strata (Qwen3.8-Flash-Next, see docs/STRATA.md) honors a top-level
        // "reasoning_effort" (none|low|medium|high) instead of
        // chat_template_kwargs. llama.cpp ignores unknown top-level fields, so
        // sending both keeps one client working on either backend.
        if (options.EnableThinking.HasValue || options.ReasoningEffort is { Length: > 0 })
        {
            var effort = !string.IsNullOrEmpty(options.ReasoningEffort)
                ? options.ReasoningEffort
                : options.EnableThinking == true ? "medium" : "none";
            // Strata has no xhigh level — clamp to its documented max.
            if (string.Equals(effort, "xhigh", StringComparison.OrdinalIgnoreCase))
                effort = "high";
            body["reasoning_effort"] = effort;
        }

        if (tools.HasValue && tools.Value.ValueKind == JsonValueKind.Array &&
            tools.Value.GetArrayLength() > 0)
        {
            body["tools"] = tools.Value;
            body["tool_choice"] = "auto";
        }

        if (options.ResponseFormatSchema is { } schema)
        {
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "playbook_step_output", strict = true, schema },
            };
        }

        return body;
    }

    private static object ToApiMessage(Message m) => m.Role switch
    {
        MessageRole.System => new { role = "system", content = m.Content },
        MessageRole.User when m.ContentParts is { Count: > 0 } =>
            new { role = "user", content = (object)m.ContentParts.Select<ContentPart, object>(p => p switch {
                TextPart t  => (object)new { type = "text", text = t.Text },
                ImagePart i => new { type = "image_url", image_url = new { url = i.Url } },
                _           => new { type = "text", text = "" }
            }).ToList() },
        MessageRole.User => new { role = "user", content = m.Content },
        MessageRole.Assistant when m.ToolCalls is { Count: > 0 } =>
            new
            {
                role = "assistant",
                content = m.Content ?? (object)"",
                tool_calls = m.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Name, arguments = tc.Arguments }
                })
            },
        MessageRole.Assistant => new { role = "assistant", content = m.Content },
        MessageRole.Tool => (object)new
        {
            role = "tool",
            tool_call_id = m.ToolCallId,
            content = m.Content,
        },
        _ => new { role = "user", content = m.Content },
    };

    // Foundry Local's OpenAI-compatible API answers 400 to fields it does not
    // know (top_k, min_p, repetition_penalty, chat_template_kwargs,
    // reasoning_effort, stream_options, ...), and to tools when the model
    // cannot call them. Send standard OpenAI fields only.
    private static Dictionary<string, object?> BuildFoundryBody(
        string model,
        List<object> apiMessages,
        JsonElement? tools,
        LlmOptions options,
        bool toolCalling)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = apiMessages,
            ["temperature"] = options.Temperature,
            ["top_p"] = options.TopP,
            ["max_tokens"] = options.MaxTokens,
            ["stream"] = true,
        };

        if (toolCalling && tools.HasValue && tools.Value.ValueKind == JsonValueKind.Array &&
            tools.Value.GetArrayLength() > 0)
        {
            body["tools"] = tools.Value;
            body["tool_choice"] = "auto";
        }

        return body;
    }

    public void Dispose() => _http.Dispose();

    private sealed class ToolCallAccumulator
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
        public bool IsComplete { get; set; }
    }
}
