using System.Text.RegularExpressions;

namespace OpenMono.Rendering;

public static partial class AnsiMarkdown
{

    private const string R  = "\x1b[0m";
    private const string B  = "\x1b[1m";
    private const string DM = "\x1b[2m";
    private const string IT = "\x1b[3m";
    private const string UL = "\x1b[4m";
    private const string Fw = "\x1b[37m";
    private const string Fc = "\x1b[36m";
    private const string Fk = "\x1b[90m";
    private const string Fy = "\x1b[33m";
    private const string BgCode = "\x1b[40m";

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRe();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldRe();

    [GeneratedRegex(@"(?<!\*)\*([^*]+)\*(?!\*)")]
    private static partial Regex ItalicRe();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiRe();

    public static List<string> Render(string text, int width)
    {
        var lines = new List<string>();
        var rawLines = text.Split('\n');
        var inCodeBlock = false;
        var codeLang = "";

        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i];

            if (line.TrimStart().StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeLang = line.TrimStart()[3..].Trim();
                    var label = codeLang.Length > 0 ? $" {Fk}{codeLang}{R}" : "";
                    lines.Add($"{BgCode}{Fk}╭───{label}{R}");
                }
                else
                {
                    inCodeBlock = false;
                    codeLang = "";
                    lines.Add($"{BgCode}{Fk}╰───{R}");
                }
                continue;
            }

            if (inCodeBlock)
            {

                foreach (var wrapped in Wrap(line, width))
                    lines.Add($"{BgCode}{Fc}{wrapped}{R}");
                continue;
            }

            if (line.StartsWith("### "))
            {
                foreach (var wrapped in Wrap(line[4..], width))
                    lines.Add($"{B}{Fw}{wrapped}{R}");
                continue;
            }
            if (line.StartsWith("## "))
            {
                foreach (var wrapped in Wrap(line[3..], width))
                    lines.Add($"{B}{Fw}{wrapped}{R}");
                continue;
            }
            if (line.StartsWith("# "))
            {
                foreach (var wrapped in Wrap(line[2..], width))
                    lines.Add($"{B}{UL}{Fw}{wrapped}{R}");
                continue;
            }

            if (line.TrimStart().StartsWith("- "))
            {
                var indent = line.Length - line.TrimStart().Length;
                var content = line.TrimStart()[2..];
                var formatted = ApplyInline(content);
                var wrappedLines = Wrap(formatted, Math.Max(1, width - indent - 2));
                for (var j = 0; j < wrappedLines.Count; j++)
                {
                    var bullet = j == 0 ? $"{Fy}•{R} " : "  ";
                    lines.Add($"{new string(' ', indent)}{bullet}{wrappedLines[j]}");
                }
                continue;
            }

            if (line.TrimStart().Length > 0 &&
                char.IsDigit(line.TrimStart()[0]) &&
                line.TrimStart().Contains(". "))
            {
                var indent = line.Length - line.TrimStart().Length;
                var dotIdx = line.TrimStart().IndexOf(". ");
                var num = line.TrimStart()[..dotIdx];
                var content = line.TrimStart()[(dotIdx + 2)..];
                var formatted = ApplyInline(content);
                var numStr = $"{num}. ";
                var wrappedLines = Wrap(formatted, Math.Max(1, width - indent - numStr.Length));
                for (var j = 0; j < wrappedLines.Count; j++)
                {
                    var prefix = j == 0 ? $"{Fk}{numStr}{R}" : new string(' ', numStr.Length);
                    lines.Add($"{new string(' ', indent)}{prefix}{wrappedLines[j]}");
                }
                continue;
            }

            foreach (var wrapped in Wrap(ApplyInline(line), width))
                lines.Add(wrapped);
        }

        if (inCodeBlock)
            lines.Add($"{BgCode}{Fk}╰───{R}");

        return lines;
    }

    private static List<string> Wrap(string text, int width)
    {
        if (width <= 0) return [text];
        if (VisLen(text) <= width) return [text];

        var result = new List<string>();
        var words = text.Split(' ');
        var currentLine = new System.Text.StringBuilder();
        var currentLen = 0;

        foreach (var word in words)
        {
            var wordVisLen = VisLen(word);
            if (currentLen + (currentLen > 0 ? 1 : 0) + wordVisLen <= width)
            {
                if (currentLen > 0)
                {
                    currentLine.Append(' ');
                    currentLen++;
                }
                currentLine.Append(word);
                currentLen += wordVisLen;
            }
            else
            {
                if (currentLine.Length > 0)
                {
                    result.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLen = 0;
                }

                if (wordVisLen > width)
                {

                    var remainingWord = word;
                    while (VisLen(remainingWord) > width)
                    {
                        var chunk = TakeVisual(remainingWord, width, out var rest);
                        result.Add(chunk);
                        remainingWord = rest;
                    }
                    currentLine.Append(remainingWord);
                    currentLen = VisLen(remainingWord);
                }
                else
                {
                    currentLine.Append(word);
                    currentLen = wordVisLen;
                }
            }
        }

        if (currentLine.Length > 0)
            result.Add(currentLine.ToString());

        return result;
    }

    public static int VisLen(string ansi) => StripAnsi(ansi).Length;

    public static string StripAnsi(string ansi) => AnsiRe().Replace(ansi, "");

    private static string TakeVisual(string text, int width, out string rest)
    {
        var vis = 0;
        var i = 0;
        for (; i < text.Length && vis < width; i++)
        {
            if (text[i] == '\x1b')
            {

                var end = text.IndexOf('m', i);
                if (end != -1) { i = end; continue; }
            }
            vis++;
        }
        rest = text[i..];
        return text[..i];
    }

    private static string ApplyInline(string text)
    {

        text = InlineCodeRe().Replace(text, $"{BgCode}{Fc}$1{R}");

        text = BoldRe().Replace(text, $"{B}{Fw}$1{R}");

        text = ItalicRe().Replace(text, $"{IT}$1{R}");

        return text;
    }
}
