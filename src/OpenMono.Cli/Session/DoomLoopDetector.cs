using System.Text;
using System.Text.Json;

namespace OpenMono.Session;

internal sealed class DoomLoopDetector
{
    private readonly List<string> _signatures = [];
    private const int MaxPeriod  = 4;
    private const int MaxHistory = 12;

    public void Reset() => _signatures.Clear();

    /// <summary>Builds the canonical signature for a batch of tool calls (normalized args, stable order).</summary>
    public static string SignatureFor(List<ToolCall> calls) =>
        string.Join("|", calls.Select(c => $"{c.Name}:{Normalize(c.Arguments)}"));

    public bool Check(List<ToolCall> calls)
    {
        var sig = SignatureFor(calls);
        _signatures.Add(sig);

        if (_signatures.Count > MaxHistory)
            _signatures.RemoveAt(0);

        return HasLoop();
    }

    private bool HasLoop()
    {
        var n = _signatures.Count;
        for (var period = 1; period <= MaxPeriod; period++)
        {
            var reps   = period == 1 ? 3 : 2;
            var needed = period * reps;
            if (n < needed) continue;

            var window = _signatures.TakeLast(needed).ToList();
            var isLoop = true;
            for (var i = 0; i < needed; i++)
                if (window[i] != window[i % period]) { isLoop = false; break; }
            if (isLoop) return true;
        }
        return false;
    }

    private static string Normalize(string args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
                WriteNormalized(writer, doc.RootElement);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return args; }
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(prop.Name); WriteNormalized(writer, prop.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteNormalized(writer, item);
                writer.WriteEndArray(); break;
            default:
                element.WriteTo(writer); break;
        }
    }
}
