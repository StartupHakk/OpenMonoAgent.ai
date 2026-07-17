using System.Diagnostics;

namespace OpenMono.Utils;

/// <summary>

/// </summary>
public static class DesktopNotifier
{
    private const char Bel = '\u0007';  // BEL

    /// <summary>
    /// </summary>
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("OPENMONO_NOTIFICATIONS")?.Trim().ToLowerInvariant()
            is not ("0" or "false" or "off" or "no");


    private static readonly bool InContainer =
        Environment.GetEnvironmentVariable("OPENMONO_IN_CONTAINER") == "1"
        || File.Exists("/.dockerenv");

    /// <summary>
    /// </summary>
    public static void Alert(string title, string message)
    {
        if (!Enabled) return;
        if (InContainer)
            NotifyTerminal($"{title} — {message}");
        else
            Notify(title, message);
    }

    /// <summary>
    /// Raise a native desktop notification. Fire-and-forget: returns immediately
    /// and swallows any failure (missing tooling, sandboxing, etc.).
    /// </summary>
    public static void Notify(string title, string message)
    {
        if (!Enabled) return;
        // Never let a UI subprocess stall the agent turn.
        _ = Task.Run(() => Send(title, message));
    }

    /// <summary>

    /// </summary>
    public static void NotifyTerminal(string message)
    {
        if (!Enabled) return;
        try
        {
            if (Console.IsOutputRedirected) return; // no terminal attached
            Console.Out.Write(Bel);
            Console.Out.Flush();
        }
        catch { }
    }

    private static void Send(string title, string message)
    {
        try
        {
            var psi = BuildStartInfo(title, message);
            if (psi is null) return;
            var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Debug($"[notify] failed to raise desktop notification: {ex.Message}");
        }
    }

    private static ProcessStartInfo? BuildStartInfo(string title, string message)
    {
        if (OperatingSystem.IsMacOS())
        {
            var script = $"display notification {AppleScriptString(message)} " +
                         $"with title {AppleScriptString(title)}";
            var psi = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            return psi;
        }

        if (OperatingSystem.IsLinux())
        {
            var bin = new[] { "/usr/bin/notify-send", "/bin/notify-send" }
                .FirstOrDefault(File.Exists);
            if (bin is null) return null;
            var psi = new ProcessStartInfo(bin)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);
            return psi;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows 10+ toast via the built-in Windows Runtime API — no extra
            // PowerShell modules required.
            var script =
                "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] > $null;" +
                "$t=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
                "$x=$t.GetElementsByTagName('text');" +
                $"$x.Item(0).AppendChild($t.CreateTextNode({PowerShellString(title)})) > $null;" +
                $"$x.Item(1).AppendChild($t.CreateTextNode({PowerShellString(message)})) > $null;" +
                "$toast=[Windows.UI.Notifications.ToastNotification]::new($t);" +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('OpenMono').Show($toast);";
            var psi = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            return psi;
        }

        return null;
    }

    // Quote a value for embedding inside an AppleScript string literal.
    private static string AppleScriptString(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Quote a value for embedding inside a PowerShell single-quoted string.
    private static string PowerShellString(string value)
        => "'" + value.Replace("'", "''") + "'";
}
