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
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
    ];

    private static readonly Regex QwenFunctionRegex = new(
        @"<function=(\w+)>(.*?)</function>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex QwenParamRegex = new(
        @"<parameter=(\w+)>\s*(.*?)\s*</parameter>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public string? ApiKey { get; init; }
    public Action<string>? OnDebug { get; set; }

    private readonly string _model;

    public OpenAiCompatClient(LlmConfig config)
    {
        _endpoint = config.Endpoint.TrimEnd('/');
        _model = config.Model;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        var lastException = default(Exception);

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                await Task.Delay(delay, ct);
            }

            var requestBody = BuildRequestBody(messages, tools, options, _model);
            var requestJson = JsonSerializer.Serialize(requestBody, JsonOptions.Default);

            if (attempt == 0)
            {
                var toolCount = tools?.ValueKind == JsonValueKind.Array ? tools.Value.GetArrayLength() : 0;
                OnDebug?.Invoke($"[LLM] POST {_endpoint}/v1/chat/completions");
                var resolvedModel = string.IsNullOrEmpty(options.Model) ? _model : options.Model;
                OnDebug?.Invoke($"[LLM] Model: {resolvedModel} | Messages: {messages.Count} | Tools: {toolCount} | MaxTokens: {options.MaxTokens}");
                Log.Debug($"LLM request: model={resolvedModel} messages={messages.Count} tools={toolCount} endpoint={_endpoint}");
            }
            else
            {
                var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                OnDebug?.Invoke($"[LLM] Retry {attempt}/{MaxRetries} after {delay.TotalSeconds}s");
                Log.Warn($"LLM retry {attempt}/{MaxRetries} after {delay.TotalSeconds}s");
            }

            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/v1/chat/completions")
            {
                Content = content,
            };

            if (ApiKey is not null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

            try
            {
                response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (IsRetryableStatus(response.StatusCode))
                {
                    lastException = new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                        inner: null, response.StatusCode);
                    response.Dispose();
                    response = null;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                break;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                response?.Dispose();
                response = null;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {

                lastException = ex;
                response?.Dispose();
                response = null;
            }
        }

        if (response is null)
            throw lastException ?? new HttpRequestException("Failed to connect after retries");

        var streamStarted = System.Diagnostics.Stopwatch.StartNew();
        var chunkCount = 0;

        using (response)
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var toolCalls = new Dictionary<int, ToolCallAccumulator>();
            var malformedChunks = 0;
            var fullText = new StringBuilder();
            var suppressText = false;

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]")
                {
                    foreach (var tc in toolCalls.Values.Where(t => t.IsComplete))
                    {
                        OnDebug?.Invoke($"[SSE] tool_call: {tc.Name} {{ {tc.Arguments.ToString()[..Math.Min(100, tc.Arguments.Length)]} }}");
                        Log.Debug($"SSE tool_call: {tc.Name} args={tc.Arguments.ToString()[..Math.Min(200, tc.Arguments.Length)]}");

                        yield return new StreamChunk
                        {
                            ToolCallDelta = new ToolCall
                            {
                                Id = tc.Id,
                                Name = tc.Name,
                                Arguments = tc.Arguments.ToString(),
                            }
                        };
                    }

                    var elapsed = streamStarted.Elapsed;
                    OnDebug?.Invoke($"[LLM] Stream complete — {chunkCount} chunks in {elapsed.TotalSeconds:F1}s");
                    Log.Debug($"LLM stream complete: chunks={chunkCount} elapsed={elapsed.TotalSeconds:F1}s");

                    if (toolCalls.Count == 0 && suppressText)
                    {
                        var idx = 0;
                        foreach (Match m in QwenFunctionRegex.Matches(fullText.ToString()))
                        {
                            var name = m.Groups[1].Value;
                            var body = m.Groups[2].Value;
                            var args = new Dictionary<string, string>();
                            foreach (Match p in QwenParamRegex.Matches(body))
                                args[p.Groups[1].Value] = p.Groups[2].Value.Trim();

                            yield return new StreamChunk
                            {
                                ToolCallDelta = new ToolCall
                                {
                                    Id = $"call_xml_{idx++}",
                                    Name = name,
                                    Arguments = JsonSerializer.Serialize(args),
                                }
                            };
                        }
                    }

                    yield return new StreamChunk { IsComplete = true };
                    yield break;
                }

                JsonDocument? doc;
                try
                {
                    doc = JsonDocument.Parse(data);
                }
                catch (JsonException)
                {
                    malformedChunks++;
                    if (malformedChunks > 50)
                        throw new InvalidOperationException(
                            $"Too many malformed SSE chunks ({malformedChunks}). Stream is corrupt.");
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        var errorMsg = errorEl.TryGetProperty("message", out var msgEl)
                            ? msgEl.GetString() : "Unknown API error";
                        throw new HttpRequestException($"LLM API error: {errorMsg}");
                    }

                    chunkCount++;

                    UsageInfo? usage = null;
                    if (root.TryGetProperty("usage", out var usageEl) &&
                        usageEl.ValueKind == JsonValueKind.Object)
                    {
                        usage = new UsageInfo
                        {
                            PromptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                            CompletionTokens = usageEl.TryGetProperty("completion_tokens", out var cpt) ? cpt.GetInt32() : 0,
                        };
                        OnDebug?.Invoke($"[SSE] usage: prompt={usage.PromptTokens} completion={usage.CompletionTokens} total={usage.TotalTokens}");
                        Log.Debug($"SSE usage: prompt={usage.PromptTokens} completion={usage.CompletionTokens}");
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
                                yield return new StreamChunk { ThinkingDelta = thinking };
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
                                    if (fullText.ToString().Contains("<function="))
                                        suppressText = true;
                                    else
                                        yield return new StreamChunk { TextDelta = text, Usage = usage };
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
                            }
                        }

                        if (choice.TryGetProperty("finish_reason", out var fr) &&
                            fr.ValueKind == JsonValueKind.String)
                        {
                            if (fr.GetString() == "tool_calls")
                            {
                                foreach (var tc in toolCalls.Values)
                                    tc.IsComplete = true;
                            }
                        }
                    }

                    if (usage is not null)
                        yield return new StreamChunk { Usage = usage };
                }
            }
        }
    }

    private static bool IsRetryableStatus(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    private static object BuildRequestBody(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        string configModel)
    {
        var model = string.IsNullOrEmpty(options.Model) ? configModel : options.Model;
        var apiMessages = messages.Select<Message, object>(m => m.Role switch
        {
            MessageRole.System => new { role = "system", content = m.Content },
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
        }).ToList();

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
            body["chat_template_kwargs"] = new { enable_thinking = options.EnableThinking.Value };

        if (tools.HasValue && tools.Value.ValueKind == JsonValueKind.Array &&
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
