using System.Text.Json;
using Terminal.Gui.Drawing;

namespace OpenMono.Tui.Rendering;

public static class ThemeManager
{
    private static Theme _current = null!;

    static ThemeManager()
    {
        _current = Dark;
    }

    public static Theme Current
    {
        get => _current;
        private set => _current = value ?? Dark;
    }

    public static void Load(string? configPath)
    {
        if (configPath is null || !File.Exists(configPath))
        {
            Current = Dark;
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var themeName = "dark";
            if (root.TryGetProperty("theme", out var themeEl) && themeEl.ValueKind == JsonValueKind.String)
                themeName = themeEl.GetString() ?? "dark";

            var baseTheme = ResolveBuiltIn(themeName);

            if (root.TryGetProperty("customTheme", out var custom) && custom.ValueKind == JsonValueKind.Object)
                Current = ApplyOverrides(baseTheme, custom);
            else
                Current = baseTheme;
        }
        catch
        {
            Current = Dark;
        }
    }

    public static Theme ResolveBuiltIn(string name) => name.ToLowerInvariant() switch
    {
        "light" => Light,
        "monokai" => Monokai,
        "solarized" => Solarized,
        _ => Dark
    };

    public static readonly Theme Dark = new()
    {
        Background = Color.Black,
        Foreground = Color.White,
        Accent = new Color(163, 255, 102),
        Muted = Color.Gray,
        Error = Color.BrightRed,
        Warning = Color.Yellow,
        Success = Color.Green,
        HeaderBg = Color.Black,
        HeaderFg = Color.White,
        StatusBarBg = Color.Black,
        StatusBarFg = Color.White,
        SidebarBg = Color.Black,
        SidebarFg = Color.White,
        UserBorder = Color.Green,
        AssistantBorder = new Color(163, 255, 102),
        ToolBorder = Color.Gray,
        SystemBorder = Color.Yellow,
        SyntaxKeyword = new Color(163, 255, 102),
        SyntaxString = Color.Green,
        SyntaxNumber = new Color(209, 154, 102, 255),
        SyntaxComment = Color.Gray,
        SyntaxType = Color.BrightCyan,
        SyntaxFunction = Color.BrightYellow,
        SyntaxOperator = Color.White,
        MdHeading = Color.BrightCyan,
        MdLink = new Color(163, 255, 102),
        MdInlineCodeFg = Color.BrightYellow,
        MdInlineCodeBg = Color.DarkGray,
        MdQuote = Color.Gray,
        MdBullet = Color.BrightCyan,
        CodeBlockBg = Color.Black,
    };

    public static readonly Theme Light = new()
    {
        Background = Color.White,
        Foreground = Color.Black,
        Accent = new Color(163, 255, 102),
        Muted = Color.DarkGray,
        Error = Color.Red,
        Warning = new Color(180, 130, 0, 255),
        Success = new Color(0, 128, 0, 255),
        HeaderBg = new Color(240, 240, 240, 255),
        HeaderFg = Color.Black,
        StatusBarBg = new Color(240, 240, 240, 255),
        StatusBarFg = Color.Black,
        SidebarBg = new Color(245, 245, 245, 255),
        SidebarFg = Color.Black,
        UserBorder = new Color(0, 128, 0, 255),
        AssistantBorder = new Color(163, 255, 102),
        ToolBorder = Color.DarkGray,
        SystemBorder = new Color(180, 130, 0, 255),
        SyntaxKeyword = new Color(163, 255, 102),
        SyntaxString = new Color(163, 21, 21, 255),
        SyntaxNumber = new Color(9, 134, 88, 255),
        SyntaxComment = new Color(0, 128, 0, 255),
        SyntaxType = new Color(38, 127, 153, 255),
        SyntaxFunction = new Color(121, 94, 38, 255),
        SyntaxOperator = Color.Black,
        MdHeading = new Color(163, 255, 102),
        MdLink = new Color(163, 255, 102),
        MdInlineCodeFg = new Color(163, 21, 21, 255),
        MdInlineCodeBg = new Color(230, 230, 230, 255),
        MdQuote = Color.DarkGray,
        MdBullet = new Color(163, 255, 102),
        CodeBlockBg = new Color(245, 245, 245, 255),
    };

    public static readonly Theme Monokai = new()
    {
        Background = new Color(39, 40, 34, 255),
        Foreground = new Color(248, 248, 242, 255),
        Accent = new Color(102, 217, 239, 255),
        Muted = new Color(117, 113, 94, 255),
        Error = new Color(249, 38, 114, 255),
        Warning = new Color(230, 219, 116, 255),
        Success = new Color(166, 226, 46, 255),
        HeaderBg = new Color(39, 40, 34, 255),
        HeaderFg = new Color(248, 248, 242, 255),
        StatusBarBg = new Color(49, 50, 44, 255),
        StatusBarFg = new Color(248, 248, 242, 255),
        SidebarBg = new Color(39, 40, 34, 255),
        SidebarFg = new Color(248, 248, 242, 255),
        UserBorder = new Color(166, 226, 46, 255),
        AssistantBorder = new Color(102, 217, 239, 255),
        ToolBorder = new Color(117, 113, 94, 255),
        SystemBorder = new Color(230, 219, 116, 255),
        SyntaxKeyword = new Color(249, 38, 114, 255),
        SyntaxString = new Color(230, 219, 116, 255),
        SyntaxNumber = new Color(174, 129, 255, 255),
        SyntaxComment = new Color(117, 113, 94, 255),
        SyntaxType = new Color(102, 217, 239, 255),
        SyntaxFunction = new Color(166, 226, 46, 255),
        SyntaxOperator = new Color(249, 38, 114, 255),
        MdHeading = new Color(102, 217, 239, 255),
        MdLink = new Color(102, 217, 239, 255),
        MdInlineCodeFg = new Color(230, 219, 116, 255),
        MdInlineCodeBg = new Color(49, 50, 44, 255),
        MdQuote = new Color(117, 113, 94, 255),
        MdBullet = new Color(166, 226, 46, 255),
        CodeBlockBg = new Color(39, 40, 34, 255),
    };

    public static readonly Theme Solarized = new()
    {
        Background = new Color(0, 43, 54, 255),
        Foreground = new Color(131, 148, 150, 255),
        Accent = new Color(38, 139, 210, 255),
        Muted = new Color(88, 110, 117, 255),
        Error = new Color(220, 50, 47, 255),
        Warning = new Color(181, 137, 0, 255),
        Success = new Color(133, 153, 0, 255),
        HeaderBg = new Color(0, 43, 54, 255),
        HeaderFg = new Color(147, 161, 161, 255),
        StatusBarBg = new Color(7, 54, 66, 255),
        StatusBarFg = new Color(147, 161, 161, 255),
        SidebarBg = new Color(0, 43, 54, 255),
        SidebarFg = new Color(131, 148, 150, 255),
        UserBorder = new Color(133, 153, 0, 255),
        AssistantBorder = new Color(38, 139, 210, 255),
        ToolBorder = new Color(88, 110, 117, 255),
        SystemBorder = new Color(181, 137, 0, 255),
        SyntaxKeyword = new Color(133, 153, 0, 255),
        SyntaxString = new Color(42, 161, 152, 255),
        SyntaxNumber = new Color(211, 54, 130, 255),
        SyntaxComment = new Color(88, 110, 117, 255),
        SyntaxType = new Color(181, 137, 0, 255),
        SyntaxFunction = new Color(38, 139, 210, 255),
        SyntaxOperator = new Color(131, 148, 150, 255),
        MdHeading = new Color(181, 137, 0, 255),
        MdLink = new Color(38, 139, 210, 255),
        MdInlineCodeFg = new Color(42, 161, 152, 255),
        MdInlineCodeBg = new Color(7, 54, 66, 255),
        MdQuote = new Color(88, 110, 117, 255),
        MdBullet = new Color(42, 161, 152, 255),
        CodeBlockBg = new Color(0, 43, 54, 255),
    };

    private static Theme ApplyOverrides(Theme baseTheme, JsonElement custom)
    {

        var bg = TryParseColor(custom, "background") ?? baseTheme.Background;
        var fg = TryParseColor(custom, "foreground") ?? baseTheme.Foreground;
        var accent = TryParseColor(custom, "accent") ?? baseTheme.Accent;
        var muted = TryParseColor(custom, "muted") ?? baseTheme.Muted;
        var error = TryParseColor(custom, "error") ?? baseTheme.Error;
        var warning = TryParseColor(custom, "warning") ?? baseTheme.Warning;
        var success = TryParseColor(custom, "success") ?? baseTheme.Success;

        Color? synKeyword = null, synString = null, synNumber = null;
        Color? synComment = null, synType = null, synFunction = null, synOperator = null;

        if (custom.TryGetProperty("syntax", out var syntax) && syntax.ValueKind == JsonValueKind.Object)
        {
            synKeyword = TryParseColor(syntax, "keyword");
            synString = TryParseColor(syntax, "string");
            synNumber = TryParseColor(syntax, "number");
            synComment = TryParseColor(syntax, "comment");
            synType = TryParseColor(syntax, "type");
            synFunction = TryParseColor(syntax, "function");
            synOperator = TryParseColor(syntax, "operator");
        }

        Color? headerBg = null, headerFg = null, statusBg = null, statusFg = null;
        Color? sidebarBg = null, sidebarFg = null;

        if (custom.TryGetProperty("ui", out var ui) && ui.ValueKind == JsonValueKind.Object)
        {
            headerBg = TryParseColor(ui, "headerBg");
            headerFg = TryParseColor(ui, "headerFg");
            statusBg = TryParseColor(ui, "statusBarBg");
            statusFg = TryParseColor(ui, "statusBarFg");
            sidebarBg = TryParseColor(ui, "sidebarBg");
            sidebarFg = TryParseColor(ui, "sidebarFg");
        }

        Color? userBorder = null, assistantBorder = null, toolBorder = null, systemBorder = null;

        if (custom.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Object)
        {
            userBorder = TryParseColor(roles, "user");
            assistantBorder = TryParseColor(roles, "assistant");
            toolBorder = TryParseColor(roles, "tool");
            systemBorder = TryParseColor(roles, "system");
        }

        return new Theme
        {
            Background = bg,
            Foreground = fg,
            Accent = accent,
            Muted = muted,
            Error = error,
            Warning = warning,
            Success = success,
            HeaderBg = headerBg ?? baseTheme.HeaderBg,
            HeaderFg = headerFg ?? baseTheme.HeaderFg,
            StatusBarBg = statusBg ?? baseTheme.StatusBarBg,
            StatusBarFg = statusFg ?? baseTheme.StatusBarFg,
            SidebarBg = sidebarBg ?? baseTheme.SidebarBg,
            SidebarFg = sidebarFg ?? baseTheme.SidebarFg,
            UserBorder = userBorder ?? baseTheme.UserBorder,
            AssistantBorder = assistantBorder ?? baseTheme.AssistantBorder,
            ToolBorder = toolBorder ?? baseTheme.ToolBorder,
            SystemBorder = systemBorder ?? baseTheme.SystemBorder,
            SyntaxKeyword = synKeyword ?? baseTheme.SyntaxKeyword,
            SyntaxString = synString ?? baseTheme.SyntaxString,
            SyntaxNumber = synNumber ?? baseTheme.SyntaxNumber,
            SyntaxComment = synComment ?? baseTheme.SyntaxComment,
            SyntaxType = synType ?? baseTheme.SyntaxType,
            SyntaxFunction = synFunction ?? baseTheme.SyntaxFunction,
            SyntaxOperator = synOperator ?? baseTheme.SyntaxOperator,
            MdHeading = baseTheme.MdHeading,
            MdLink = baseTheme.MdLink,
            MdInlineCodeFg = baseTheme.MdInlineCodeFg,
            MdInlineCodeBg = baseTheme.MdInlineCodeBg,
            MdQuote = baseTheme.MdQuote,
            MdBullet = baseTheme.MdBullet,
            CodeBlockBg = bg,
        };
    }

    private static Color? TryParseColor(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return null;

        if (el.ValueKind != JsonValueKind.String)
            return null;

        var value = el.GetString();
        if (value is null)
            return null;

        return ParseHexColor(value);
    }

    private static Color? ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');

        if (hex.Length == 6 &&
            int.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return new Color(r, g, b, 255);
        }

        return null;
    }
}
