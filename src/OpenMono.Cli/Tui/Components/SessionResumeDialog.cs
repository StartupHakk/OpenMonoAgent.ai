using OpenMono.Session;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public enum SessionResumeChoice
{
    Resume,
    NewSession,
    Delete
}

public static class SessionResumeDialog
{

    public static (SessionResumeChoice Choice, string? SessionId) Show(
        IApplication app,
        SessionSummary session)
    {
        var dialog = new Dialog
        {
            Title = "Resume Session?",
            Width = Dim.Percent(70),
            Height = Dim.Auto(DimAutoStyle.Content)
        };

        var y = 0;

        dialog.Add(new Label
        {
            Text = "Previous session found:",
            X = 2,
            Y = y++,
            Width = Dim.Fill(2)
        });

        y++;

        var started = session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var elapsed = DateTime.UtcNow - session.StartedAt;
        var duration = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
            : $"{(int)elapsed.TotalMinutes} minutes";

        dialog.Add(new Label { Text = $"  Started:    {started}", X = 2, Y = y++, Width = Dim.Fill(2) });
        dialog.Add(new Label { Text = $"  Duration:   {duration}", X = 2, Y = y++, Width = Dim.Fill(2) });
        dialog.Add(new Label { Text = $"  Turns:      {session.TurnCount}", X = 2, Y = y++, Width = Dim.Fill(2) });

        var topic = session.FirstMessage;
        if (topic.Length > 50)
            topic = topic[..47] + "...";
        if (!string.IsNullOrEmpty(topic))
            dialog.Add(new Label { Text = $"  Last topic: \"{topic}\"", X = 2, Y = y++, Width = Dim.Fill(2) });

        y++;

        dialog.Add(new Label
        {
            Text = "[R] Resume  [N] New Session  [D] Delete",
            X = 2,
            Y = y++,
            Width = Dim.Fill(2)
        });

        y++;

        var resumeBtn = new Button { Text = "_R Resume" };
        var newBtn = new Button { Text = "_N New Session" };
        var deleteBtn = new Button { Text = "_D Delete" };

        dialog.AddButton(resumeBtn);
        dialog.AddButton(newBtn);
        dialog.AddButton(deleteBtn);

        app.Run(dialog);

        if (dialog.Canceled)
            return (SessionResumeChoice.NewSession, null);

        return dialog.Result switch
        {
            0 => (SessionResumeChoice.Resume, session.Id),
            1 => (SessionResumeChoice.NewSession, null),
            2 => (SessionResumeChoice.Delete, session.Id),
            _ => (SessionResumeChoice.NewSession, null)
        };
    }
}
