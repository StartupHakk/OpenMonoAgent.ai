using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public class HeaderBar : View
{
    private readonly Label _modelLabel;
    private readonly Label _endpointLabel;
    private readonly Label _turnLabel;
    private readonly Label _durationLabel;
    private readonly DateTime _sessionStart;

    private const string Separator = " \u2502 ";

    public HeaderBar(IApplication app, string model, string endpoint)
    {
        Height = 1;
        Width = Dim.Fill();
        CanFocus = false;

        _sessionStart = DateTime.UtcNow;

        var logo = new Label
        {
            Text = "OpenMono.ai",
            X = 0,
            Y = 0,
            Width = Dim.Auto()
        };

        var sep1 = new Label { Text = Separator, X = Pos.Right(logo), Y = 0, Width = Dim.Auto() };

        _modelLabel = new Label
        {
            Text = model,
            X = Pos.Right(sep1),
            Y = 0,
            Width = Dim.Auto()
        };

        var sep2 = new Label { Text = Separator, X = Pos.Right(_modelLabel), Y = 0, Width = Dim.Auto() };

        _endpointLabel = new Label
        {
            Text = TruncateEndpoint(endpoint, 30),
            X = Pos.Right(sep2),
            Y = 0,
            Width = Dim.Auto()
        };

        var sep3 = new Label { Text = Separator, X = Pos.Right(_endpointLabel), Y = 0, Width = Dim.Auto() };

        _turnLabel = new Label
        {
            Text = "Turn #0",
            X = Pos.Right(sep3),
            Y = 0,
            Width = Dim.Auto()
        };

        var sep4 = new Label { Text = Separator, X = Pos.Right(_turnLabel), Y = 0, Width = Dim.Auto() };

        _durationLabel = new Label
        {
            Text = "0m 0s",
            X = Pos.Right(sep4),
            Y = 0,
            Width = Dim.Auto()
        };

        Add(logo, sep1, _modelLabel, sep2, _endpointLabel, sep3, _turnLabel, sep4, _durationLabel);

        app.AddTimeout(TimeSpan.FromSeconds(1), () =>
        {
            var elapsed = DateTime.UtcNow - _sessionStart;
            _durationLabel.Text = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
            return true;
        });

        FrameChanged += (_, _) =>
        {
            var width = Frame.Size.Width;
            _endpointLabel.Visible = width >= 100;
            sep2.Visible = width >= 100;
            sep3.Visible = width >= 100;
        };
    }

    public void UpdateTurnCount(int turn)
    {
        _turnLabel.Text = $"Turn #{turn}";
    }

    private static string TruncateEndpoint(string endpoint, int maxLen)
    {
        if (endpoint.Length <= maxLen)
            return endpoint;
        return string.Concat(endpoint.AsSpan(0, maxLen - 1), "\u2026");
    }
}
