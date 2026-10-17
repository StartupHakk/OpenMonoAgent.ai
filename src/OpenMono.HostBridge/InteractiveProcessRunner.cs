using System.Diagnostics;
using System.Text;

namespace OpenMono.HostBridge;

/// <summary>
/// Byte-streaming stdout/stderr reader for foreground host commands with
/// operator stdin forwarding. Prompts (which have no trailing newline) are
/// detected on output arrival — line-buffered reads would hang forever.
///
/// Single-flight prompting: at most one operator question at a time, bounded
/// per command. Without a prompter (or when it cannot prompt) the first
/// detected prompt fails fast with guidance instead of hanging till timeout.
/// </summary>
internal static class InteractiveProcessRunner
{
    internal sealed record ReadResult(string Stdout, string Stderr, bool TimedOut, bool Cancelled, string? AbortError);

    public static async Task<ReadResult> ReadAsync(
        Process process,
        IHostExecPrompter? prompter,
        int timeoutMs,
        CancellationToken ct,
        Action<Process>? kill = null)
    {
        kill ??= Kill;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        var token = timeoutCts.Token;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var state = new PromptState(process, prompter, stdout, stderr, kill);

        var outTask = PumpStreamAsync(process.StandardOutput.BaseStream, Encoding.UTF8.GetDecoder(), stdout, state, token);
        var errTask = PumpStreamAsync(process.StandardError.BaseStream, Encoding.UTF8.GetDecoder(), stderr, state, token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            kill(process);
        }

        await Task.WhenAll(outTask, errTask);

        string outText, errText;
        lock (stdout) outText = stdout.ToString();
        lock (stderr) errText = stderr.ToString();

        try { process.StandardInput.Close(); } catch { }

        if (ct.IsCancellationRequested)
            return new ReadResult(outText, errText, TimedOut: false, Cancelled: true, AbortError: null);
        if (state.AbortError is not null)
            return new ReadResult(outText, errText, TimedOut: false, Cancelled: false, AbortError: state.AbortError);
        if (timedOut)
            return new ReadResult(outText, errText, TimedOut: true, Cancelled: false, AbortError: null);
        return new ReadResult(outText, errText, TimedOut: false, Cancelled: false, AbortError: null);
    }

    private static async Task PumpStreamAsync(
        Stream baseStream, Decoder decoder, StringBuilder sb, PromptState state, CancellationToken token)
    {
        var bytes = new byte[4096];
        var chars = new char[4096];
        while (true)
        {
            int n;
            try
            {
                n = await baseStream.ReadAsync(bytes, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            if (n == 0)
                break;
            var count = decoder.GetChars(bytes, 0, n, chars, 0);
            lock (sb)
                sb.Append(chars, 0, count);
            try
            {
                await state.CheckPromptAsync(sb, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (state.AbortError is not null)
                break;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    /// <summary>Shared prompt matching state (single-flight via semaphore).</summary>
    private sealed class PromptState
    {
        private readonly Process _process;
        private readonly IHostExecPrompter? _prompter;
        private readonly StringBuilder _stdout;
        private readonly StringBuilder _stderr;
        private readonly Action<Process> _kill;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _consumedOut;
        private int _consumedErr;
        private int _prompts;

        public string? AbortError { get; private set; }

        public PromptState(Process process, IHostExecPrompter? prompter, StringBuilder stdout, StringBuilder stderr, Action<Process> kill)
        {
            _process = process;
            _prompter = prompter;
            _stdout = stdout;
            _stderr = stderr;
            _kill = kill;
        }

        public async Task CheckPromptAsync(StringBuilder updated, CancellationToken token)
        {
            if (AbortError is not null)
                return;
            await _gate.WaitAsync(token);
            try
            {
                if (AbortError is not null)
                    return;
                string tail;
                bool isOut = ReferenceEquals(updated, _stdout);
                lock (updated)
                    tail = updated.ToString()[(isOut ? _consumedOut : _consumedErr)..];
                var kind = HostExecInteraction.ClassifyPrompt(tail);
                if (kind == PromptKind.None)
                    return;

                // Settle: echoed text scrolls by, real prompts block. Only ask
                // when the tail is unchanged after the delay and the process
                // is still alive.
                var before = tail;
                try
                {
                    await Task.Delay(HostExecInteraction.PromptSettleDelay, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                lock (updated)
                    tail = updated.ToString()[(isOut ? _consumedOut : _consumedErr)..];
                if (!tail.Equals(before, StringComparison.Ordinal) || HasExited())
                    return;

                await HandlePromptAsync(kind, HostExecInteraction.Excerpt(tail), token);
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool HasExited()
        {
            try { return _process.HasExited; }
            catch { return true; }
        }

        private async Task HandlePromptAsync(PromptKind kind, string excerpt, CancellationToken token)
        {
            _prompts++;
            if (_prompts > HostExecInteraction.MaxPromptsPerCommand)
            {
                AbortError = $"ERROR: command asked for input {_prompts} times; stopping to avoid a prompt loop. " +
                    "Re-run with non-interactive flags (e.g. -y, --non-interactive, BatchMode) or answer fewer prompts.";
                _kill(_process);
                return;
            }
            if (_prompter is null || !_prompter.CanPrompt)
            {
                AbortError = $"ERROR: command needs interactive input ('{excerpt}'); " +
                    "re-run interactively, or use a non-interactive alternative " +
                    "(ssh keys, -o BatchMode=yes, --non-interactive flags). Nothing ran to completion.";
                _kill(_process);
                return;
            }

            using var promptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            promptCts.CancelAfter(HostExecInteraction.PromptTimeout);
            try
            {
                bool answered = kind switch
                {
                    PromptKind.Secret => await AskSecretAsync(excerpt, promptCts.Token),
                    PromptKind.Confirm => await AskConfirmAsync(excerpt, promptCts.Token),
                    _ => await AskTextAsync(excerpt, promptCts.Token),
                };
                if (!answered && AbortError is null)
                    AbortError = $"ERROR: input declined or timed out for prompt ('{excerpt}'); command stopped.";
                if (AbortError is not null)
                    _kill(_process);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Turn cancelled mid-prompt: stop the process; the caller reports cancellation.
                _kill(_process);
            }
            catch (OperationCanceledException)
            {
                AbortError = $"ERROR: input timed out for prompt ('{excerpt}'); command stopped.";
                _kill(_process);
            }
        }

        private async Task<bool> AskSecretAsync(string excerpt, CancellationToken token)
        {
            var secret = await _prompter!.AskSecretAsync(excerpt, token);
            if (secret is null)
                return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(secret).Concat([(byte)'\n']).ToArray();
                await _process.StandardInput.BaseStream.WriteAsync(bytes, token);
                await _process.StandardInput.BaseStream.FlushAsync(token);
            }
            finally
            {
                Array.Clear(secret);
            }
            MarkConsumed();
            return true;
        }

        private async Task<bool> AskConfirmAsync(string excerpt, CancellationToken token)
        {
            var answer = await _prompter!.AskConfirmAsync(excerpt, token);
            if (answer is null)
                return false;
            await WriteLineAsync(answer.Value ? "y" : "n", token);
            MarkConsumed();
            return true;
        }

        private async Task<bool> AskTextAsync(string excerpt, CancellationToken token)
        {
            var answer = await _prompter!.AskTextAsync(excerpt, token);
            if (answer is null)
                return false;
            await WriteLineAsync(answer, token);
            MarkConsumed();
            return true;
        }

        private async Task WriteLineAsync(string answer, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(answer + "\n");
            await _process.StandardInput.BaseStream.WriteAsync(bytes, token);
            await _process.StandardInput.BaseStream.FlushAsync(token);
        }

        private void MarkConsumed()
        {
            lock (_stdout) _consumedOut = _stdout.Length;
            lock (_stderr) _consumedErr = _stderr.Length;
        }
    }
}
