using System.Text.Json;
using OpenMono.Session;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public static class ApprovalDialog
{

    public static ApprovalDecision Show(IApplication app, ToolCall call)
    {
        var dialog = new Dialog
        {
            Title = "Tool Approval Required",
            Width = Dim.Percent(80),
            Height = Dim.Auto(DimAutoStyle.Content)
        };

        var y = 0;

        var nameLabel = new Label
        {
            Text = call.Name,
            X = 1,
            Y = y,
            Width = Dim.Fill(1)
        };
        dialog.Add(nameLabel);
        y++;

        var sep = new Label
        {
            Text = new string('\u2500', 50),
            X = 1,
            Y = y,
            Width = Dim.Fill(1)
        };
        dialog.Add(sep);
        y++;

        var argsDisplay = FormatArgs(call.Name, call.Arguments);
        foreach (var line in argsDisplay.Split('\n'))
        {
            var argLabel = new Label
            {
                Text = line,
                X = 1,
                Y = y,
                Width = Dim.Fill(1)
            };
            dialog.Add(argLabel);
            y++;
        }

        y++;

        var hintLabel = new Label
        {
            Text = "[Y] Allow  [N] Deny  [A] Allow All  [!] Deny All",
            X = 1,
            Y = y,
            Width = Dim.Fill(1)
        };
        dialog.Add(hintLabel);
        y++;

        var allowBtn = new Button { Text = "_Y Allow" };
        var denyBtn = new Button { Text = "_N Deny" };
        var allowAllBtn = new Button { Text = "_A Allow All" };
        var denyAllBtn = new Button { Text = "! Deny All" };

        dialog.AddButton(allowBtn);
        dialog.AddButton(denyBtn);
        dialog.AddButton(allowAllBtn);
        dialog.AddButton(denyAllBtn);

        app.Run(dialog);

        if (dialog.Canceled)
            return ApprovalDecision.Deny;

        return dialog.Result switch
        {
            0 => ApprovalDecision.Allow,
            1 => ApprovalDecision.Deny,
            2 => ApprovalDecision.AllowAll,
            3 => ApprovalDecision.DenyAll,
            _ => ApprovalDecision.Deny
        };
    }

    private static string FormatArgs(string toolName, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var lines = new List<string>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    _ => prop.Value.GetRawText()
                };

                if (value.Length <= 60)
                {
                    lines.Add($"  {prop.Name}: {value}");
                }
                else
                {

                    lines.Add($"  {prop.Name}:");
                    foreach (var chunk in value.Split('\n').Take(8))
                    {
                        var trimmed = chunk.Length > 70 ? chunk[..67] + "..." : chunk;
                        lines.Add($"    {trimmed}");
                    }
                    if (value.Split('\n').Length > 8)
                        lines.Add($"    ... ({value.Split('\n').Length - 8} more lines)");
                }
            }

            return lines.Count > 0 ? string.Join('\n', lines) : argsJson;
        }
        catch
        {

            return argsJson.Length > 200 ? argsJson[..197] + "..." : argsJson;
        }
    }
}
