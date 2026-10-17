using System.Text.Json;
using OpenMono.HostBridge;
using OpenMono.Permissions;
using OpenMono.Session;

namespace OpenMono.Tools;

public sealed class AskUserTool : ToolBase
{
    public override string Name => "AskUser";
    public override string Description => "Ask the user a question and wait for their response. Use when you need clarification or a decision — for example, when you cannot determine the project's language/framework/stack from the request or the working directory, ask which stack to use before running build/test/run commands. Pass 'options' to offer specific choices.";
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("question", "The question to ask the user")
        .AddArray("options", "Optional list of choices for the user to pick from", new { type = "string" })
        .Require("question");

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var question = input.GetProperty("question").GetString()!;

        // Host mode (sandbox paired with a host daemon): a HOST_EXEC envelope
        // never reaches the human as a raw question. On the interactive
        // surface it becomes a consent card right here and an approved command
        // runs on bare metal through the daemon. On the ACP surface the
        // envelope flows to the client as a normal question — the client UI IS
        // the consent surface — and AcpTurnRunner executes the approved
        // command when the answer arrives (the pause unwinds this call).
        if (HostExecRequest.LooksLikeHostExec(question))
        {
            var gateway = HostGatewayRegistry.Get(context.Session.Id);
            if (gateway is not null && context.Interaction is null)
                return await HandleHostExecInlineAsync(question, gateway, context, ct);
        }

        var hasOptions = input.TryGetProperty("options", out var opts)
            && opts.ValueKind == JsonValueKind.Array;

        var options = hasOptions
            ? opts.EnumerateArray().Select(o => o.GetString() ?? "").Where(o => o.Length > 0).ToList()
            : [];

        if (options.Count > 0 && context.AskUserWithOptions is not null)
        {
            var choice = await context.AskUserWithOptions(question, options, ct);
            return ToolResult.Success(choice);
        }

        var prompt = question;
        if (options.Count > 0)
            prompt += "\n" + string.Join('\n', options.Select((o, i) => $"  [{i + 1}] {o}"));

        var response = await context.AskUser(prompt, ct);
        return ToolResult.Success(response);
    }

    private static async Task<ToolResult> HandleHostExecInlineAsync(
        string question,
        HostGateway gateway,
        ToolContext context,
        CancellationToken ct)
    {
        if (!HostExecRequest.TryParse(question, out var request) || request is null)
            return ToolResult.Success(HostExecRequest.MalformedEnvelopeAnswer(question));

        var answer = await gateway.RunAsync(request, async (cmd, isRoot, innerCt) =>
        {
            var label = isRoot ? "HOST EXECUTION (as root)" : "HOST EXECUTION";
            var prompt = $"Run on the host as {gateway.RunAs}: {cmd}";
            var options = isRoot
                ? new[] { "allow once", "allow all", "deny", "deny all" }
                : new[] { "allow once", "allow all (this session)", "allow all (30 minutes)", "allow all (today)", "deny", "deny all (stop host mode)" };
            var reply = context.AskUserWithOptions is not null
                ? await context.AskUserWithOptions($"⚠ {label}\n{prompt}", options, innerCt)
                : await context.AskUser($"⚠ {label}\n{prompt}\nReply: allow once / allow all / allow all (30 minutes) / allow all (today) / deny / deny all", innerCt);
            return HostConsent.Map(reply);
        }, ct);

        return ToolResult.Success(answer);
    }
}
