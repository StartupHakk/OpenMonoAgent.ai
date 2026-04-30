using OpenMono.Commands;
using OpenMono.Tui.Keybindings;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public static class HelpOverlay
{
    public static void Show(IApplication app, KeybindingManager keybindings, CommandRegistry? commands)
    {
        var dialog = new Dialog
        {
            Title = "OpenMono Help",
            Width = Dim.Percent(85),
            Height = Dim.Percent(80),
        };

        var y = 0;

        AddSectionHeader(dialog, "NAVIGATION", ref y);
        AddRow(dialog, "Page Up/Down", "Scroll history", ref y);
        AddRow(dialog, "Home/End", "Jump to start/end", ref y);
        AddRow(dialog, "\u2191/\u2193", "Navigate", ref y);
        y++;

        AddSectionHeader(dialog, "CONTROL", ref y);
        AddRow(dialog, keybindings.GetHint(TuiAction.Pause), "Pause/Resume streaming", ref y);
        AddRow(dialog, keybindings.GetHint(TuiAction.ToggleApproval), "Toggle approval mode", ref y);
        AddRow(dialog, keybindings.GetHint(TuiAction.ToggleSidebar), "Toggle tool sidebar", ref y);
        AddRow(dialog, keybindings.GetHint(TuiAction.ToggleThinking), "Toggle thinking panel", ref y);
        AddRow(dialog, keybindings.GetHint(TuiAction.Debug), "Toggle debug mode", ref y);
        AddRow(dialog, keybindings.GetHint(TuiAction.Help), "Show/hide this help", ref y);
        AddRow(dialog, "Ctrl+C", "Cancel / Exit", ref y);
        y++;

        AddSectionHeader(dialog, "COMMANDS", ref y);

        if (commands is not null)
        {
            foreach (var cmd in commands.All.OrderBy(c => c.Name))
            {
                var name = $"/{cmd.Name.TrimStart('/')}";
                AddRow(dialog, name, cmd.Description, ref y);
            }
        }

        AddRow(dialog, "/clear", "Clear conversation context", ref y);
        AddRow(dialog, "/quit", "Exit OpenMono", ref y);
        y++;

        dialog.Add(new Label
        {
            Text = "Press any key to close",
            X = Pos.Center(),
            Y = y,
            Width = Dim.Auto()
        });

        dialog.KeyDown += (_, key) =>
        {
            key.Handled = true;
            app.RequestStop(dialog);
        };

        app.Run(dialog);
    }

    private static void AddSectionHeader(Dialog dialog, string title, ref int y)
    {
        dialog.Add(new Label
        {
            Text = title,
            X = 2,
            Y = y,
            Width = Dim.Auto()
        });
        y++;
        dialog.Add(new Label
        {
            Text = new string('\u2500', title.Length + 2),
            X = 2,
            Y = y,
            Width = Dim.Auto()
        });
        y++;
    }

    private static void AddRow(Dialog dialog, string key, string description, ref int y)
    {
        dialog.Add(new Label
        {
            Text = key,
            X = 4,
            Y = y,
            Width = 16
        });
        dialog.Add(new Label
        {
            Text = description,
            X = 22,
            Y = y,
            Width = Dim.Fill(2)
        });
        y++;
    }
}
