using OpenMono.Config;
using OpenMono.Session;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui;

public interface ITuiEventSink
{
    void OnStreamStart();
    void OnTokenReceived(string token, int totalCompletionTokens);
    void OnStreamEnd();
    void OnToolStarted(string toolId, string toolName, string args);
    void OnToolCompleted(string toolId, bool success, string? error);
    void OnMessageAdded(Message message);
    void UpdateMetrics(int promptTokens, int completionTokens, double tokensPerSec);
}

public static class TuiApplication
{
    public static bool IsSupported()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") is not null)
            return false;

        if (File.Exists("/.dockerenv"))
            return false;

        return true;
    }

    public static void Run(AppConfig config, SessionState session, Action<ITuiEventSink> onReady)
    {
        using var app = Application.Create();
        app.Init();

        var header = new FrameView
        {
            Title = "OpenMono.ai",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };

        var main = new FrameView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(Dim.Absolute(1))
        };

        var statusBar = new StatusBar();
        statusBar.Add(
            new Shortcut
            {
                Title = "Quit",
                Text = "Ctrl+Q"
            }
        );

        var window = new Window
        {
            Title = "OpenMono.ai",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        window.Add(header, main, statusBar);

        var sink = new TuiEventSink(app, window);
        onReady(sink);

        app.Run(window);
    }

    private sealed class TuiEventSink : ITuiEventSink
    {
        private readonly IApplication _app;
        private readonly Window _window;

        public TuiEventSink(IApplication app, Window window)
        {
            _app = app;
            _window = window;
        }

        public void OnStreamStart() { }

        public void OnTokenReceived(string token, int totalCompletionTokens) { }

        public void OnStreamEnd() { }

        public void OnToolStarted(string toolId, string toolName, string args) { }

        public void OnToolCompleted(string toolId, bool success, string? error) { }

        public void OnMessageAdded(Message message) { }

        public void UpdateMetrics(int promptTokens, int completionTokens, double tokensPerSec) { }
    }
}
