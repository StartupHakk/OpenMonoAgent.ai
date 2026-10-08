using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenMono.Windows.AgentHost;

namespace OpenMono.Windows.Desktop.Views;

public sealed partial class ChatPage : Page
{
    public sealed record Row(string Header, string Body);

    private readonly ObservableCollection<Row> _rows = [];
    private AgentHostFactory.AgentSession? _session;
    private CancellationTokenSource? _turnCts;
    private string _streaming = string.Empty;

    public ChatPage()
    {
        InitializeComponent();
        Messages.ItemsSource = _rows;
        _ = EnsureSessionAsync();
    }

    private async Task EnsureSessionAsync()
    {
        if (_session is not null)
        {
            return;
        }

        try
        {
            var state = App.State;
            var registry = state.EnsureRegistry();
            var tier = state.SelectedTier ?? registry.ForTier(0);
            _session = AgentHostFactory.CreateSession(
                state.Supervisor,
                tier.Alias,
                tier.EffectiveCtx(state.Supervisor.VisionEnabled),
                state.Workspace,
                ChoosePermissionAsync,
                AskUserAsync);
            _session.Output.TextStreamed += OnStreamed;
            _session.Output.AssistantEnded += _ => OnTurnEnded();
            _session.Output.Message += m => DispatcherQueue.TryEnqueue(() =>
                _rows.Add(new Row(m.Role.ToString(), m.Text)));
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _rows.Add(new Row("System", $"Agent is not ready yet: {ex.Message}. Start inference on the Server page first."));
        }
    }

    private void OnStreamed(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _streaming += text;
            if (_rows.Count > 0 && _rows[^1].Header == "stream")
            {
                _rows[^1] = new Row("stream", _streaming);
            }
            else
            {
                _rows.Add(new Row("stream", _streaming));
            }
        });
    }

    private void OnTurnEnded()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _streaming = string.Empty;
            Status.Text = "Ready.";
        });
    }

    private async Task<PermissionChoice> ChoosePermissionAsync(string tool, string summary, CancellationToken ct)
    {
        PermissionChoice choice = PermissionChoice.Deny;
        var dialog = new ContentDialog
        {
            Title = $"Allow {tool}?",
            Content = summary.Length > 800 ? summary[..800] : summary,
            PrimaryButtonText = "Allow once",
            SecondaryButtonText = "Allow for session",
            CloseButtonText = "Deny",
            XamlRoot = XamlRoot,
        };
        await DispatcherQueue.EnqueueAsync(async () =>
        {
            var result = await dialog.ShowAsync();
            choice = result switch
            {
                ContentDialogResult.Primary => PermissionChoice.AllowOnce,
                ContentDialogResult.Secondary => PermissionChoice.AllowForSession,
                _ => PermissionChoice.Deny,
            };
        });

        return choice;
    }

    private Task<string> AskUserAsync(string question, IReadOnlyList<string>? options, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>();
        DispatcherQueue.TryEnqueue(() =>
        {
            var box = new TextBox { PlaceholderText = question };
            var dialog = new ContentDialog
            {
                Title = "Agent question",
                Content = box,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
            };
            _ = dialog.ShowAsync().AsTask().ContinueWith(t =>
                tcs.TrySetResult(box.Text ?? string.Empty), TaskScheduler.Default);
        });

        return tcs.Task;
    }

    private void Input_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        sender.ItemsSource = SlashCommands.IsSlashCommand(sender.Text)
            ? SlashCommands.Filter(sender.Text).Select(e => $"{e.Command}  {e.Description}").ToList()
            : null;
    }

    private void Input_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        _ = SendAsync(sender.Text);

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync(Input.Text);

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _turnCts?.Cancel();
        }
        catch
        {
        }
    }

    private async Task SendAsync(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await EnsureSessionAsync();
        if (_session is null)
        {
            return;
        }

        _rows.Add(new Row("You", text));
        Input.Text = string.Empty;
        Status.Text = "Working.";
        _turnCts = new CancellationTokenSource();
        try
        {
            await _session.RunTurnAsync(text, _turnCts.Token);
        }
        catch (OperationCanceledException)
        {
            _rows.Add(new Row("System", "Turn stopped."));
        }
        catch (Exception ex)
        {
            _rows.Add(new Row("System", $"Turn failed: {ex.Message}"));
        }
    }
}

/// <summary>
/// Small helper to await work on the UI thread.
/// </summary>
internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Func<Task> work)
    {
        var tcs = new TaskCompletionSource();
        queue.TryEnqueue(async () =>
        {
            try
            {
                await work();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }
}
