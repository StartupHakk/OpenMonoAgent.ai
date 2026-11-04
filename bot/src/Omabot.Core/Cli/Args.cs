// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Cli;

public sealed class ArgsException(string message) : Exception(message);

/// <summary>Tiny argument reader: flags and options anywhere, positionals in order.</summary>
public sealed class Args(string[] argv)
{
    private readonly List<string> _rest = [.. argv];

    public bool Flag(string name) => _rest.Remove(name);

    public string? Option(string name)
    {
        var i = _rest.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= _rest.Count) throw new ArgsException($"{name} needs a value.");
        var v = _rest[i + 1];
        _rest.RemoveRange(i, 2);
        return v;
    }

    public string Next(string usage) => NextOrNull() ?? throw new ArgsException("Usage: oma-bot " + usage);

    public string? NextOrNull()
    {
        var i = _rest.FindIndex(s => !s.StartsWith("--", StringComparison.Ordinal));
        if (i < 0) return null;
        var v = _rest[i];
        _rest.RemoveAt(i);
        return v;
    }

    public void NoMore()
    {
        if (_rest.Count > 0) throw new ArgsException($"Unexpected argument \"{_rest[0]}\".");
    }
}
