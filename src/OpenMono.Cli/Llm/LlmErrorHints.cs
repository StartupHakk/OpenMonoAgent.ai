using System.Net;
using OpenMono.Config;

namespace OpenMono.Llm;

/// <summary>The follow-up line under "LLM error: ..." for an HTTP failure. On Windows the
/// server is Foundry Local on the host, so the hints point there instead of llama-server.</summary>
public static class LlmErrorHints
{
    public static string For(HttpStatusCode status, LlmConfig llm) =>
        llm.IsFoundry ? Foundry(status) : LlamaServer(status);

    private static string LlamaServer(HttpStatusCode status) => status switch
    {
        HttpStatusCode.InternalServerError =>
            "llama-server returned 500. Likely causes: context too long (KV cache overflow), " +
            "out of GPU memory, or model crash.\n" +
            "Check logs: docker logs llama-server --tail 40",
        HttpStatusCode.ServiceUnavailable =>
            "llama-server is busy or still loading; wait a moment and try again.\n" +
            "Check status: curl http://localhost:7474/health",
        HttpStatusCode.TooManyRequests =>
            "llama-server rate limit hit; wait a moment and try again.",
        HttpStatusCode.BadRequest =>
            "llama-server rejected the request (400); its reply is shown above. It may be the served model, " +
            "a request field it does not support, or a malformed conversation.\n" +
            "Check logs: docker logs llama-server --tail 40\n" +
            "Try starting a new session: /new",
        _ =>
            "Check: curl http://localhost:7474/health",
    };

    private const string HostCommands =
        "On the Windows host: .\\win\\openmono.ps1 status shows the served model, " +
        ".\\win\\openmono.ps1 start loads the OpenMono model for this machine, " +
        "and foundry server logs shows Foundry's side.";

    private static string Foundry(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest =>
            "Foundry Local rejected the request (400); its reply is shown above. It may be the served model " +
            "(it must be the OpenMono model for this machine and support tool calling) or a request field " +
            "Foundry does not support.\n" + HostCommands + "\n" +
            "If the conversation itself is malformed, start a new session: /new",
        HttpStatusCode.InternalServerError =>
            "Foundry Local returned 500. Likely causes: context too long, out of GPU memory, or the model crashed.\n" +
            HostCommands,
        HttpStatusCode.ServiceUnavailable =>
            "Foundry Local is busy or still loading the model; wait a moment and try again.\n" +
            "On the Windows host: foundry server status",
        HttpStatusCode.TooManyRequests =>
            "Foundry Local is busy; wait a moment and try again.",
        _ => HostCommands,
    };
}
