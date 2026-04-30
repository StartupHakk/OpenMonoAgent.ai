using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenMono.Config;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Llm;

public sealed class AnthropicClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiKey;

    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(16)
    ];

    public Action<string>? OnDebug { get; set; }

    public AnthropicClient(ProviderConfig config)
    {
        _endpoint = (config.Endpoint ?? "https://api.anthropic.com").TrimEnd('/');
        _apiKey = config.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var requestBody = BuildRequestBody(messages, tools, options);
        HttpResponseMessage? response = null;

        var toolCount = tools?.ValueKind == JsonValueKind.Array ? tools.Value.GetArrayLength() : 0;
        OnDebug?.Invoke($"[LLM] POST {_endpoint}/v1/messages");
        OnDebug?.Invoke($"[LLM] Model: {options.Model} | Messages: {messages.Count} | Tools: {toolCount} | MaxTokens: {options.MaxTokens}");
        Log.Debug($"Anthropic request: model={options.Model} messages={messages.Count} tools={toolCount}");

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                OnDebug?.Invoke($"[LLM] Retry {attempt}/{MaxRetries} after {delay.TotalSeconds}s");
                Log.Warn($"Anthropic retry {attempt}/{MaxRetries}");
                await Task.Delay(delay, ct);
            }

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions.Default),
                Encoding.UTF8, "application/json");

            try
            {
                response = await _http.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/v1/messages") { Content = content },
                    HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 529)
                {
                    response.Dispose(); response = null; continue;
                }
                response.EnsureSuccessStatusCode();
                break;
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                response?.Dispose(); response = null;
            }
        }

        if (response is null) throw new HttpRequestException("Anthropic API unavailable after retries");

        var streamStarted = System.Diagnostics.Stopwatch.StartNew();

        using (response)
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var currentToolId = "";
            var currentToolName = "";
            var toolArgsBuffer = new StringBuilder();
            var inToolUse = false;

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..];

                JsonDocument? doc;
                try { doc = JsonDocument.Parse(data); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                    switch (type)
                    {
                        case "content_block_start":
                            if (root.TryGetProperty("content_block", out var block) &&
                                block.TryGetProperty("type", out var blockType) &&
                                blockType.GetString() == "tool_use")
                            {
                                inToolUse = true;
                                currentToolId = block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                                currentToolName = block.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                                toolArgsBuffer.Clear();
                            }
                            break;

                        case "content_block_delta":
                            if (root.TryGetProperty("delta", out var delta))
                            {
                                var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;

                                if (deltaType == "text_delta" &&
                                    delta.TryGetProperty("text", out var text))
                                {
                                    yield return new StreamChunk { TextDelta = text.GetString() };
                                }
                                else if (deltaType == "input_json_delta" &&
                                         delta.TryGetProperty("partial_json", out var pj))
                                {
                                    toolArgsBuffer.Append(pj.GetString());
                                }
                            }
                            break;

                        case "content_block_stop":
                            if (inToolUse)
                            {
                                var argsPreview = toolArgsBuffer.ToString();
                                OnDebug?.Invoke($"[SSE] tool_call: {currentToolName} {{ {argsPreview[..Math.Min(100, argsPreview.Length)]} }}");
                                Log.Debug($"SSE tool_call: {currentToolName} args={argsPreview[..Math.Min(200, argsPreview.Length)]}");

                                yield return new StreamChunk
                                {
                                    ToolCallDelta = new ToolCall
                                    {
                                        Id = currentToolId,
                                        Name = currentToolName,
                                        Arguments = argsPreview,
                                    }
                                };
                                inToolUse = false;
                            }
                            break;

                        case "message_delta":
                            if (root.TryGetProperty("usage", out var usage))
                            {
                                var completionTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                                OnDebug?.Invoke($"[SSE] usage: completion={completionTokens}");
                                Log.Debug($"SSE usage: completion={completionTokens}");

                                yield return new StreamChunk
                                {
                                    Usage = new UsageInfo
                                    {
                                        CompletionTokens = completionTokens,
                                    }
                                };
                            }
                            break;

                        case "message_stop":
                            var elapsed = streamStarted.Elapsed;
                            OnDebug?.Invoke($"[LLM] Stream complete — {elapsed.TotalSeconds:F1}s");
                            Log.Debug($"Anthropic stream complete: elapsed={elapsed.TotalSeconds:F1}s");
                            yield return new StreamChunk { IsComplete = true };
                            yield break;

                        case "error":
                            var errMsg = root.TryGetProperty("error", out var err)
                                ? (err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error")
                                : "Unknown error";
                            throw new HttpRequestException($"Anthropic API error: {errMsg}");
                    }
                }
            }
        }
    }

    private static object BuildRequestBody(
        IReadOnlyList<Message> messages, JsonElement? tools, LlmOptions options)
    {

        var system = messages.FirstOrDefault(m => m.Role == MessageRole.System)?.Content ?? "";

        var apiMessages = messages
            .Where(m => m.Role != MessageRole.System)
            .Select<Message, object>(m => m.Role switch
            {
                MessageRole.User => new { role = "user", content = m.Content },
                MessageRole.Assistant when m.ToolCalls is { Count: > 0 } => new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        m.Content is not null
                            ? new { type = "text", text = m.Content }
                            : null!,
                    }
                    .Where(x => x is not null)
                    .Concat(m.ToolCalls.Select(tc => (object)new
                    {
                        type = "tool_use",
                        id = tc.Id,
                        name = tc.Name,
                        input = JsonSerializer.Deserialize<object>(tc.Arguments) ?? new { },
                    }))
                    .ToArray()
                },
                MessageRole.Assistant => new { role = "assistant", content = m.Content },
                MessageRole.Tool => (object)new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "tool_result", tool_use_id = m.ToolCallId, content = m.Content }
                    }
                },
                _ => new { role = "user", content = m.Content },
            }).ToList();

        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["system"] = system,
            ["messages"] = apiMessages,
            ["max_tokens"] = options.MaxTokens,
            ["stream"] = true,
        };

        if (tools.HasValue && tools.Value.ValueKind == JsonValueKind.Array && tools.Value.GetArrayLength() > 0)
        {

            var anthropicTools = new List<object>();
            foreach (var tool in tools.Value.EnumerateArray())
            {
                if (tool.TryGetProperty("function", out var fn))
                {
                    anthropicTools.Add(new
                    {
                        name = fn.GetProperty("name").GetString(),
                        description = fn.TryGetProperty("description", out var d) ? d.GetString() : "",
                        input_schema = fn.TryGetProperty("parameters", out var p)
                            ? JsonSerializer.Deserialize<object>(p.GetRawText()) : new { type = "object" },
                    });
                }
            }
            body["tools"] = anthropicTools;
        }

        return body;
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class AnthropicProvider : IProvider
{
    public string Name => "anthropic";
    public string[] SupportedModels => ["claude-sonnet-4-20250514", "claude-haiku-4-5-20251001", "claude-opus-4-20250515"];

    public ILlmClient CreateClient(ProviderConfig config) => new AnthropicClient(config);

    public bool ValidateConfig(ProviderConfig config, out string? error)
    {
        var key = config.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(key)) { error = "ANTHROPIC_API_KEY required."; return false; }
        error = null;
        return true;
    }
}
