using System.Diagnostics;

namespace OpenMono.Utils;

/// <summary>
/// Best-effort user alerts, used to tell the user that an agent needs their
/// attention — e.g. it is blocked on a permission prompt — while they are
/// looking at a different window or a different agent's tab.
///
/// Delivery adapts to where the agent runs:
///  - On the host, a native OS notification (macOS / Linux / Windows).
///  - Inside the Docker container (where native notifiers can't reach the host),
///    terminal escape sequences that the host terminal emulator renders.
///
/// All calls are fire-and-forget: they never throw and never block the turn.
/// </summary>
public static class DesktopNotifier
{
    private const char Esc = '\u001b';  // ESC
    private const char Bel = '\u0007';  // BEL

    /// <summary>
    /// Notifications are on by default. Set OPENMONO_NOTIFICATIONS to
    /// 0/false/off/no to silence them (e.g. in headless/CI environments).
    /// </summary>
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("OPENMONO_NOTIFICATIONS")?.Trim().ToLowerInvariant()
            is not ("0" or "false" or "off" or "no");

    // When the agent runs inside the Docker container, native notifier tools
    // (osascript / notify-send) either don't exist or can't reach the host's
    // notification center. In that case we notify *through the terminal* instead,
    // which is bridged straight to the host terminal emulator.
    private static readonly bool InContainer =
        Environment.GetEnvironmentVariable("OPENMONO_IN_CONTAINER") == "1"
        || File.Exists("/.dockerenv");

    /// <summary>
    /// Alert the user that the agent needs their attention. Picks the delivery
    /// mechanism that can actually reach them: a native OS banner when running on
    /// the host, or terminal escape sequences (which the host terminal renders)
    /// when running inside the container.
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
    /// Notify via terminal escape sequences written to the attached TTY:
    /// OSC 9 (a real banner in iTerm2 / kitty / WezTerm / the VS Code terminal)
    /// plus BEL (bounces the dock/taskbar icon when the terminal is unfocused).
    /// Both are non-printing, so they don't disturb the TUI, and terminals that
    /// don't understand OSC 9 silently ignore it.
    /// </summary>
    public static void NotifyTerminal(string message)
    {
        if (!Enabled) return;
        try
        {
            if (Console.IsOutputRedirected) return; // no terminal attached
            // OSC 9 desktop notification: ESC ] 9 ; <message> BEL
            Console.Out.Write($"{Esc}]9;{message}{Bel}");
            // Standalone BEL for a dock/taskbar bounce on unsupported terminals.
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
