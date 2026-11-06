using System.Text.RegularExpressions;

namespace OpenMono.Llm;

/// <summary>An error the inference server reported in a response body (an SSE or JSON
/// <c>error</c> object on an otherwise successful HTTP response). The server answered, so
/// this is never a connection problem, even though it carries no HTTP status code.</summary>
public sealed class LlmProviderErrorException : HttpRequestException
{
    public const string MessagePrefix = "LLM API error: ";

    public LlmProviderErrorException(string? providerMessage)
        : base(MessagePrefix + (string.IsNullOrWhiteSpace(providerMessage) ? "Unknown API error" : providerMessage))
    {
        ProviderMessage = providerMessage ?? "";
    }

    public string ProviderMessage { get; }

    /// <summary>Foundry Local refusing structured tool calling for a model variant that does not
    /// publish tool markers, e.g. "chat_session.cc:1198 ...BuildToolCallContextForRequest structured
    /// tool calling requires model-published opening and closing tool markers". The same request
    /// fails the same way every time; only a request without tools can succeed.</summary>
    public static bool IsToolMarkerError(string? text) =>
        !string.IsNullOrEmpty(text) && ToolMarkerPattern.IsMatch(text);

    private static readonly Regex ToolMarkerPattern = new(
        @"BuildToolCallContextForRequest|opening\s+and\s+closing\s+tool\s+markers|structured\s+tool\s+calling\s+requires",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
