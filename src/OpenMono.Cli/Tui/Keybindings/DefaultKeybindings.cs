using Terminal.Gui.Input;

namespace OpenMono.Tui.Keybindings;

public static class DefaultKeybindings
{
    public static IReadOnlyList<(Key Key, TuiAction Action)> All { get; } =
    [
        (Key.P.WithCtrl, TuiAction.Pause),
        (Key.S.WithCtrl, TuiAction.ToggleSidebar),
        (Key.A.WithCtrl, TuiAction.ToggleApproval),
        (Key.T.WithCtrl, TuiAction.ToggleThinking),
        (Key.F1,         TuiAction.Help),
        (Key.D.WithCtrl, TuiAction.Debug),
    ];
}
