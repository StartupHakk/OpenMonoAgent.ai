using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Decisions;

public sealed record JobHandoff
{
    public required string Goal { get; init; }
    public required string CompletedWork { get; init; }
    public required string Choice { get; init; }
    public required double Confidence { get; init; }
    public required string Destination { get; init; }
    public required string Status { get; init; }
    public required string Model { get; init; }
    public required double AutoThreshold { get; init; }
    public required double ReviewThreshold { get; init; }
    public required string Timestamp { get; init; }
    public required string Uuid { get; init; }
    public required string ContentHash { get; init; }
    public bool Capped { get; init; }

    public static string ComputeHash(string goal, string completedWork, string destination)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(goal, "\0", completedWork, "\0", destination)));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public async Task<string> SaveAsync(string queueBaseDirectory, CancellationToken ct)
    {
        var dir = Path.Combine(queueBaseDirectory, ".openmono", "decision-queue", Destination);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Uuid + ".json");
        var json = JsonSerializer.Serialize(this, JsonOptions.Indented);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    public static JobHandoff? FindByHash(string queueBaseDirectory, string destination, string hash)
    {
        var dir = Path.Combine(queueBaseDirectory, ".openmono", "decision-queue", destination);
        if (!Directory.Exists(dir))
            return null;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json").Take(200))
        {
            try
            {
                var json = File.ReadAllText(path);
                var handoff = JsonSerializer.Deserialize<JobHandoff>(json, JsonOptions.Default);
                if (handoff is not null && handoff.ContentHash == hash)
                    return handoff;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    public static JobHandoff? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<JobHandoff>(json, JsonOptions.Default);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static JobHandoff? TryClaim(string path)
    {
        var handoff = Load(path);
        if (handoff is null || handoff.Status != "queued")
            return handoff;
        return StoreStatus(path, handoff with { Status = "claimed" });
    }

    public static JobHandoff? MarkDone(string path)
    {
        var handoff = Load(path);
        if (handoff is null)
            return null;
        return StoreStatus(path, handoff with { Status = "done" });
    }

    public static IReadOnlyList<JobHandoff> List(string queueBaseDirectory, string destination, int max = 200)
    {
        var dir = Path.Combine(queueBaseDirectory, ".openmono", "decision-queue", destination);
        if (!Directory.Exists(dir))
            return [];
        var found = new List<JobHandoff>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            var handoff = Load(path);
            if (handoff is not null)
                found.Add(handoff);
        }
        found.Sort((left, right) => string.CompareOrdinal(right.Timestamp, left.Timestamp));
        return found.Count <= max ? found : found.GetRange(0, max);
    }

    private static JobHandoff? StoreStatus(string path, JobHandoff handoff)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(handoff, JsonOptions.Indented));
            File.Move(temp, path, overwrite: true);
            return handoff;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Load(path);
        }
    }
}
