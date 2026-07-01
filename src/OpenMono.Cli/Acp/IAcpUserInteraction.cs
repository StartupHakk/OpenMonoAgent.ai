namespace OpenMono.Acp;








public interface IAcpUserInteraction
{




    Task<bool> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct);

    Task<bool> RequestToggleModeAsync(string reason, CancellationToken ct);



    Task<string?> RequestUserInputAsync(string question, CancellationToken ct);
}
