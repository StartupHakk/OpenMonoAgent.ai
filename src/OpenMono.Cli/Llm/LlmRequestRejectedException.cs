using System.Net;

namespace OpenMono.Llm;

/// <summary>HTTP 400 from the inference server, carrying its reply so the user sees why
/// (an unsupported request field, a model that cannot take tools, a malformed conversation).</summary>
public sealed class LlmRequestRejectedException : HttpRequestException
{
    public const int MaxBodyChars = 600;

    public LlmRequestRejectedException(string? providerBody)
        : base(BuildMessage(providerBody), inner: null, HttpStatusCode.BadRequest)
    {
        ProviderBody = providerBody ?? "";
    }

    public string ProviderBody { get; }

    private static string BuildMessage(string? body)
    {
        var reply = string.IsNullOrWhiteSpace(body) ? "(empty reply)" : body.Trim().ReplaceLineEndings(" ");
        if (reply.Length > MaxBodyChars)
            reply = reply[..MaxBodyChars] + "…";
        return $"Response status code does not indicate success: 400 (Bad Request). Server reply: {reply}";
    }
}
