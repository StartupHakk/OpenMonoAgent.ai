using FluentAssertions;
using OpenMono.Tui.Keybindings;
using Terminal.Gui.Input;

namespace OpenMono.Tests.Tui;

public class KeybindingManagerTests
{
    private static bool TerminalGuiAvailable()
    {
        try { _ = Key.P; return true; } catch { return false; }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void DefaultBindings_AllActionsHaveKeys()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var mgr = new KeybindingManager();
        foreach (var action in Enum.GetValues<TuiAction>())
            mgr.GetKey(action).Should().NotBeNull($"action {action} should have a default binding");
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Resolve_CtrlP_ReturnsPause()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var mgr = new KeybindingManager();
        mgr.Resolve(Key.P.WithCtrl).Should().Be(TuiAction.Pause);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Resolve_CtrlS_ReturnsSidebar()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var mgr = new KeybindingManager();
        mgr.Resolve(Key.S.WithCtrl).Should().Be(TuiAction.ToggleSidebar);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Resolve_F1_ReturnsHelp()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var mgr = new KeybindingManager();
        mgr.Resolve(Key.F1).Should().Be(TuiAction.Help);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Resolve_UnboundKey_ReturnsNull()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var mgr = new KeybindingManager();
        mgr.Resolve(Key.Z.WithCtrl).Should().BeNull();
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void GetHint_CtrlP_ReturnsCaretP()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var mgr = new KeybindingManager();
        mgr.GetHint(TuiAction.Pause).Should().Be("^P");
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void GetHint_F1_ReturnsF1()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var mgr = new KeybindingManager();
        mgr.GetHint(TuiAction.Help).Should().Be("F1");
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void LoadOverrides_RemapsKey()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var dir = Path.Combine(Path.GetTempPath(), $"openmono-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "tui.json");

        try
        {
            File.WriteAllText(configPath, """{"Pause": "Ctrl+X"}""");
            var mgr = new KeybindingManager(configPath);
            mgr.Resolve(Key.P.WithCtrl).Should().BeNull();
            mgr.Resolve(Key.X.WithCtrl).Should().Be(TuiAction.Pause);
            mgr.GetHint(TuiAction.Pause).Should().Be("^X");
        }
        finally { Directory.Delete(dir, true); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void LoadOverrides_MalformedJson_KeepsDefaults()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var dir = Path.Combine(Path.GetTempPath(), $"openmono-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "tui.json");

        try
        {
            File.WriteAllText(configPath, "not json at all");
            var mgr = new KeybindingManager(configPath);
            mgr.Resolve(Key.P.WithCtrl).Should().Be(TuiAction.Pause);
        }
        finally { Directory.Delete(dir, true); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void LoadOverrides_UnknownAction_Ignored()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var dir = Path.Combine(Path.GetTempPath(), $"openmono-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "tui.json");

        try
        {
            File.WriteAllText(configPath, """{"FlyToMoon": "Ctrl+M"}""");
            var mgr = new KeybindingManager(configPath);
            mgr.Resolve(Key.P.WithCtrl).Should().Be(TuiAction.Pause);
        }
        finally { Directory.Delete(dir, true); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void LoadOverrides_FunctionKey()
    {
        Skip.IfNot(TerminalGuiAvailable(), "Terminal.Gui module init failed");
        var dir = Path.Combine(Path.GetTempPath(), $"openmono-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "tui.json");

        try
        {
            File.WriteAllText(configPath, """{"Help": "F2"}""");
            var mgr = new KeybindingManager(configPath);
            mgr.Resolve(Key.F1).Should().BeNull();
            mgr.Resolve(Key.F2).Should().Be(TuiAction.Help);
        }
        finally { Directory.Delete(dir, true); }
    }
}
