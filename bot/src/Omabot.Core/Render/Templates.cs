// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text;

namespace Omabot.Render;

/// <summary>Templates shipped in bot/templates, embedded in Omabot.Core.</summary>
public static class Templates
{
    private static readonly Assembly Asm = typeof(Templates).Assembly;

    public static string Get(string relativePath)
    {
        var suffix = "templates." + relativePath.Replace('/', '.').Replace('\\', '.');
        var name = Asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("Template not found: " + relativePath);
        using var s = Asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return Common.SecureFile.NormalizeLf(r.ReadToEnd());
    }

    public static IEnumerable<string> List()
        => Asm.GetManifestResourceNames().Where(n => n.Contains(".templates.", StringComparison.Ordinal));

    /// <summary>Replace {{key}} tokens. Unknown tokens stay as they are.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template);
        foreach (var (k, v) in values) sb.Replace("{{" + k + "}}", v);
        return sb.ToString();
    }
}
