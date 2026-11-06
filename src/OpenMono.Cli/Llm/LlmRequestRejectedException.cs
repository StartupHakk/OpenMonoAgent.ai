using System.Net;

namespace OpenMono.Llm;

/// <summary>A request the inference server refused (HTTP 400, or a Foundry Local 500), carrying
/// its reply so the user sees why (an unsupported request field, a model that cannot take tools,
/// a malformed conversation). Resending the same body fails the same way.</summary>
public sealed class LlmRequestRejectedException : HttpRequestException
{
    public const int MaxBodyChars = 600;

    public LlmRequestRejectedException(string? providerBody, HttpStatusCode status = HttpStatusCode.BadRequest)
        : base(BuildMessage(providerBody, status), inner: null, status)
    {
        ProviderBody = providerBody ?? "";
    }

    public string ProviderBody { get; }

    private static string BuildMessage(string? body, HttpStatusCode status)
    {
        var reply = string.IsNullOrWhiteSpace(body) ? "(empty reply)" : body.Trim().ReplaceLineEndings(" ");
        if (reply.Length > MaxBodyChars)
            reply = reply[..MaxBodyChars] + "…";
        var reason = status switch
        {
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.InternalServerError => "Internal Server Error",
            _ => status.ToString(),
        };
        return $"Response status code does not indicate success: {(int)status} ({reason}). Server reply: {reply}";
    }
}
