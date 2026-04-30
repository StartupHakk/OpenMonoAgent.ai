using System.Text;
using System.Text.RegularExpressions;

namespace OpenMono.Utils;

public static partial class InputSanitizer
{
    private const int MaxIterations = 10;

    [GeneratedRegex(@"\p{Cf}", RegexOptions.Compiled)]
    private static partial Regex FormatCharsRegex();

    [GeneratedRegex(@"\p{Co}", RegexOptions.Compiled)]
    private static partial Regex PrivateUseRegex();

    [GeneratedRegex(
        @"[\u200B-\u200F" +
        @"\u202A-\u202E" +
        @"\u2066-\u2069" +
        @"\uFEFF" +
        @"\uE000-\uF8FF]",
        RegexOptions.Compiled)]
    private static partial Regex ExplicitRangesRegex();

    public static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var current = input;
        var previous = string.Empty;
        var iterations = 0;

        while (current != previous && iterations < MaxIterations)
        {
            previous = current;

            current = current.Normalize(NormalizationForm.FormKC);

            current = FormatCharsRegex().Replace(current, "");
            current = PrivateUseRegex().Replace(current, "");

            current = ExplicitRangesRegex().Replace(current, "");

            iterations++;
        }

        return current;
    }

    public static string SanitizeUserInput(string input) => Sanitize(input.Trim());

    public static string SanitizeToolOutput(string content) => Sanitize(content);
}
