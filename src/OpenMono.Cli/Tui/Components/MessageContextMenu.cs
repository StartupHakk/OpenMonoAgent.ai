using OpenMono.Session;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public enum ContextAction
{
    Copy,
    ExpandCollapse,
    InspectJson,
    RetryTool,
    OpenInEditor
}

public sealed class MessageContextMenu
{
    public Action<ContextAction, Message>? OnAction { get; set; }

    public void Show(IApplication app, View parent, Message message, int screenX, int screenY)
    {
        var items = BuildItems(message);

        var menu = new Menu(items)
        {
            X = screenX,
            Y = screenY,
            Visible = true,
            Enabled = true
        };

        parent.Add(menu);
        menu.SetFocus();

        menu.Accepted += (_, _) =>
        {
            parent.Remove(menu);
            menu.Dispose();
        };

        menu.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                key.Handled = true;
                parent.Remove(menu);
                menu.Dispose();
            }
        };
    }

    private List<MenuItem> BuildItems(Message message)
    {
        var items = new List<MenuItem>();

        items.Add(new MenuItem("Copy", "", () =>
        {
            OnAction?.Invoke(ContextAction.Copy, message);
        }, Key.C));

        items.Add(new MenuItem("Expand/Collapse", "", () =>
        {
            OnAction?.Invoke(ContextAction.ExpandCollapse, message);
        }, Key.E));

        if (message.Role == MessageRole.Tool)
        {
            items.Add(new MenuItem("Inspect JSON", "", () =>
            {
                OnAction?.Invoke(ContextAction.InspectJson, message);
            }, Key.I));

            items.Add(new MenuItem("Retry Tool", "", () =>
            {
                OnAction?.Invoke(ContextAction.RetryTool, message);
            }, Key.R));
        }

        if (!string.IsNullOrEmpty(message.Content))
        {
            items.Add(new MenuItem("Open in Editor", "", () =>
            {
                OnAction?.Invoke(ContextAction.OpenInEditor, message);
            }, Key.O));
        }

        return items;
    }

    public static void CopyToClipboard(IApplication app, string text)
    {
        if (app.Clipboard?.TrySetClipboardData(text) == true)
            return;

        try
        {
            var process = Environment.OSVersion.Platform switch
            {
                PlatformID.Unix when File.Exists("/usr/bin/pbcopy") =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pbcopy")
                    {
                        RedirectStandardInput = true,
                        UseShellExecute = false
                    }),
                PlatformID.Unix =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xclip", "-selection clipboard")
                    {
                        RedirectStandardInput = true,
                        UseShellExecute = false
                    }),
                _ => null
            };

            if (process is not null)
            {
                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.WaitForExit(2000);
            }
        }
        catch
        {

        }
    }

    public static void OpenInEditor(string content)
    {
        try
        {
            var editor = Environment.GetEnvironmentVariable("EDITOR") ?? "vi";
            var tempFile = Path.Combine(Path.GetTempPath(), $"openmono-{Guid.NewGuid():N}.txt");
            File.WriteAllText(tempFile, content);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(editor, tempFile)
            {
                UseShellExecute = true
            });
        }
        catch
        {

        }
    }
}
