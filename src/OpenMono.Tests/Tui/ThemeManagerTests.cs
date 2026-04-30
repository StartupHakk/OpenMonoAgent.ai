using FluentAssertions;
using OpenMono.Tui.Rendering;
using Terminal.Gui.Drawing;

namespace OpenMono.Tests.Tui;

public class ThemeManagerTests
{
    private static void SkipIfNoTerminalGui()
    {
        try { _ = ThemeManager.Current; }
        catch { Skip.If(true, "Terminal.Gui module init failed in test runner"); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void DefaultTheme_IsDark()
    {
        SkipIfNoTerminalGui();
        ThemeManager.Load(null);
        ThemeManager.Current.Background.Should().Be(Color.Black);
        ThemeManager.Current.Foreground.Should().Be(Color.White);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void ResolveBuiltIn_Dark()
    {
        SkipIfNoTerminalGui();
        ThemeManager.ResolveBuiltIn("dark").Background.Should().Be(Color.Black);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void ResolveBuiltIn_Light()
    {
        SkipIfNoTerminalGui();
        var theme = ThemeManager.ResolveBuiltIn("light");
        theme.Background.Should().Be(Color.White);
        theme.Foreground.Should().Be(Color.Black);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void ResolveBuiltIn_Monokai()
    {
        SkipIfNoTerminalGui();
        var theme = ThemeManager.ResolveBuiltIn("monokai");
        theme.Background.Should().NotBe(Color.Black);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void ResolveBuiltIn_Solarized()
    {
        SkipIfNoTerminalGui();
        ThemeManager.ResolveBuiltIn("solarized").Background.Should().NotBe(Color.Black);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void ResolveBuiltIn_Unknown_FallsToDark()
    {
        SkipIfNoTerminalGui();
        ThemeManager.ResolveBuiltIn("nonexistent").Background.Should().Be(Color.Black);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Load_FromJsonFile_SelectsTheme()
    {
        SkipIfNoTerminalGui();
        var dir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tui.json"), """{"theme": "light"}""");
            ThemeManager.Load(Path.Combine(dir, "tui.json"));
            ThemeManager.Current.Background.Should().Be(Color.White);
        }
        finally { Directory.Delete(dir, true); ThemeManager.Load(null); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Load_WithCustomOverrides_AppliesColors()
    {
        SkipIfNoTerminalGui();
        var dir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tui.json"), """
            {"theme":"dark","customTheme":{"background":"#FF0000","syntax":{"keyword":"#00FF00"}}}
            """);
            ThemeManager.Load(Path.Combine(dir, "tui.json"));
            ThemeManager.Current.Background.R.Should().Be(255);
            ThemeManager.Current.SyntaxKeyword.G.Should().Be(255);
        }
        finally { Directory.Delete(dir, true); ThemeManager.Load(null); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Load_MalformedJson_FallsToDark()
    {
        SkipIfNoTerminalGui();
        var dir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tui.json"), "not json");
            ThemeManager.Load(Path.Combine(dir, "tui.json"));
            ThemeManager.Current.Background.Should().Be(Color.Black);
        }
        finally { Directory.Delete(dir, true); ThemeManager.Load(null); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Load_MissingFile_FallsToDark()
    {
        SkipIfNoTerminalGui();
        ThemeManager.Load("/nonexistent/path/tui.json");
        ThemeManager.Current.Background.Should().Be(Color.Black);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Theme_DerivedAttributes_AreConsistent()
    {
        SkipIfNoTerminalGui();
        var t = ThemeManager.Dark;
        t.Normal.Foreground.Should().Be(t.Foreground);
        t.Normal.Background.Should().Be(t.Background);
        t.Dim.Foreground.Should().Be(t.Muted);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Theme_GetSyntaxAttribute_AllTokenTypes()
    {
        SkipIfNoTerminalGui();
        var t = ThemeManager.Dark;
        foreach (var token in Enum.GetValues<TokenType>())
            t.GetSyntaxAttribute(token).Background.Should().Be(t.CodeBlockBg);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Theme_MakeRoleScheme_SetsNormalAndFocus()
    {
        SkipIfNoTerminalGui();
        var scheme = ThemeManager.Dark.MakeRoleScheme(Color.Green);
        scheme.Normal.Foreground.Should().Be(Color.Green);
        scheme.Focus.Foreground.Should().Be(Color.Green);
    }
}
