using System.Diagnostics;

namespace OpenMono.Utils;

public static class ProcessWatchdog
{

    public static void ScheduleHardKill(int delayMs = 500)
    {
        var t = new Thread(() =>
        {
            try { Thread.Sleep(delayMs); } catch { }
            try { Process.GetCurrentProcess().Kill(); } catch { }
        })
        { IsBackground = true, Name = "HardKillWatchdog" };
        t.Start();
    }
}
