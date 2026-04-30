using OpenMono.Commands;

namespace OpenMono.Rendering;

public sealed class CommandAwareInput
{
    private readonly CommandRegistry _commands;

    private readonly List<(string Name, string Description)> _allCommands;

    private DateTime _lastCtrlCTime = DateTime.MinValue;

    public CommandAwareInput(CommandRegistry commands)
    {
        _commands = commands;

        _allCommands = new List<(string, string)>();
        foreach (var cmd in commands.All.OrderBy(c => c.Name))
        {
            var name = cmd.Name.TrimStart('/');
            _allCommands.Add(($"/{name}", cmd.Description));
        }
        _allCommands.Add(("/quit", "Exit OpenMono"));
    }

    public string Read()
    {

        Console.Write("\x1b[1;32m◇\x1b[0m ");

        var buffer = new List<char>();
        var selectedIndex = -1;
        var suggestions = new List<(string Name, string Description)>();
        var suggestionsVisible = false;
        var hintShown = false;

        var prevTreatCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (hintShown)
            {
                ClearCtrlCBanner(ref hintShown);
            }

            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                var now = DateTime.UtcNow;
                var isDouble = (now - _lastCtrlCTime).TotalSeconds <= 1.5;

                ClearSuggestions(suggestionsVisible ? suggestions.Count : 0);
                suggestionsVisible = false;
                suggestions.Clear();
                selectedIndex = -1;

                if (isDouble)
                {
                    ClearInputLine(buffer.Count);
                    Console.WriteLine();
                    throw new OperationCanceledException();
                }

                if (buffer.Count == 0)
                {

                    _lastCtrlCTime = now;
                    ShowCtrlCBanner();
                    Console.WriteLine();
                    return "/clear";
                }

                _lastCtrlCTime = now;
                ClearInputLine(buffer.Count);
                buffer.Clear();
                ShowCtrlCBanner(ref hintShown);
                continue;
            }

            if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                if (buffer.Count > 0)
                {
                    ClearSuggestions(suggestionsVisible ? suggestions.Count : 0);
                    ClearInputLine(buffer.Count);
                    buffer.Clear();
                    suggestionsVisible = false;
                    suggestions.Clear();
                    selectedIndex = -1;
                }
                continue;
            }

            if (key.Key == ConsoleKey.W && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                if (buffer.Count > 0)
                {
                    var toRemove = 0;
                    while (buffer.Count - toRemove > 0 && buffer[buffer.Count - 1 - toRemove] == ' ')
                        toRemove++;
                    while (buffer.Count - toRemove > 0 && buffer[buffer.Count - 1 - toRemove] != ' ')
                        toRemove++;

                    for (var j = 0; j < toRemove; j++)
                        Console.Write("\b \b");
                    buffer.RemoveRange(buffer.Count - toRemove, toRemove);

                    if (buffer.Count > 0 && buffer[0] == '/')
                        (suggestions, suggestionsVisible, selectedIndex) =
                            RefreshSuggestions(buffer, suggestionsVisible ? suggestions.Count : 0);
                    else
                    {
                        ClearSuggestions(suggestionsVisible ? suggestions.Count : 0);
                        suggestionsVisible = false;
                        selectedIndex = -1;
                        suggestions.Clear();
                    }
                }
                continue;
            }

            if (key.Key == ConsoleKey.Enter)
            {

                if (suggestionsVisible && selectedIndex >= 0 && selectedIndex < suggestions.Count)
                {
                    ClearSuggestions(suggestions.Count);
                    ClearInputLine(buffer.Count);
                    var selected = suggestions[selectedIndex].Name;
                    Console.Write($"\x1b[1;32m◇\x1b[0m {selected}");
                    Console.WriteLine();
                    return selected;
                }

                ClearSuggestions(suggestionsVisible ? suggestions.Count : 0);
                Console.WriteLine();
                return new string(buffer.ToArray());
            }

            if (key.Key == ConsoleKey.Escape)
            {
                if (suggestionsVisible)
                {
                    ClearSuggestions(suggestions.Count);
                    suggestionsVisible = false;
                    selectedIndex = -1;
                    suggestions.Clear();

                    ClearInputLine(buffer.Count);
                    buffer.Clear();
                    continue;
                }

                ClearInputLine(buffer.Count);
                buffer.Clear();
                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                if (suggestionsVisible && suggestions.Count > 0)
                {

                    var idx = selectedIndex >= 0 ? selectedIndex : 0;
                    var completed = suggestions[idx].Name;
                    ClearSuggestions(suggestions.Count);
                    ClearInputLine(buffer.Count);
                    buffer.Clear();
                    buffer.AddRange(completed);
                    Console.Write(new string(buffer.ToArray()));

                    (suggestions, suggestionsVisible, selectedIndex) =
                        RefreshSuggestions(buffer, suggestions.Count);
                }
                continue;
            }

            if (key.Key == ConsoleKey.UpArrow && suggestionsVisible && suggestions.Count > 0)
            {
                var oldCount = suggestions.Count;
                selectedIndex = selectedIndex <= 0 ? suggestions.Count - 1 : selectedIndex - 1;
                RenderSuggestions(suggestions, selectedIndex, oldCount);
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow && suggestionsVisible && suggestions.Count > 0)
            {
                var oldCount = suggestions.Count;
                selectedIndex = selectedIndex >= suggestions.Count - 1 ? 0 : selectedIndex + 1;
                RenderSuggestions(suggestions, selectedIndex, oldCount);
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);

                    Console.Write("\b \b");

                    if (buffer.Count > 0 && buffer[0] == '/')
                    {
                        (suggestions, suggestionsVisible, selectedIndex) =
                            RefreshSuggestions(buffer, suggestionsVisible ? suggestions.Count : 0);
                    }
                    else
                    {
                        ClearSuggestions(suggestionsVisible ? suggestions.Count : 0);
                        suggestionsVisible = false;
                        selectedIndex = -1;
                        suggestions.Clear();
                    }
                }
                continue;
            }

            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                buffer.Add(key.KeyChar);
                Console.Write(key.KeyChar);

                if (buffer[0] == '/')
                {
                    (suggestions, suggestionsVisible, selectedIndex) =
                        RefreshSuggestions(buffer, suggestionsVisible ? suggestions.Count : 0);
                }
            }
        }

        }
        finally
        {
            Console.TreatControlCAsInput = prevTreatCtrlC;
        }
    }

    private (List<(string Name, string Description)> suggestions, bool visible, int selectedIndex)
        RefreshSuggestions(List<char> buffer, int previousCount)
    {
        var typed = new string(buffer.ToArray());
        var filtered = _allCommands
            .Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            ClearSuggestions(previousCount);
            return (filtered, false, -1);
        }

        RenderSuggestions(filtered, selectedIndex: -1, previousLineCount: previousCount);
        return (filtered, true, -1);
    }

    private void RenderSuggestions(
        List<(string Name, string Description)> suggestions,
        int selectedIndex,
        int previousLineCount)
    {

        var (cursorLeft, cursorTop) = (Console.CursorLeft, Console.CursorTop);

        for (var i = 0; i < previousLineCount; i++)
        {
            var row = cursorTop + 1 + i;
            if (row < Console.BufferHeight)
            {
                Console.SetCursorPosition(0, row);
                Console.Write(new string(' ', Console.WindowWidth - 1));
            }
        }

        var maxShow = Math.Min(suggestions.Count, 10);
        for (var i = 0; i < maxShow; i++)
        {
            var row = cursorTop + 1 + i;
            if (row >= Console.BufferHeight) break;

            Console.SetCursorPosition(0, row);

            var (name, desc) = suggestions[i];
            if (i == selectedIndex)
            {

                Console.Write($"  \x1b[1;32m▸ {name,-14}{desc}\x1b[0m");
            }
            else
            {

                Console.Write($"    \x1b[2m{name,-14}{desc}\x1b[0m");
            }

            Console.Write("\x1b[K");
        }

        for (var i = maxShow; i < previousLineCount; i++)
        {
            var row = cursorTop + 1 + i;
            if (row < Console.BufferHeight)
            {
                Console.SetCursorPosition(0, row);
                Console.Write("\x1b[K");
            }
        }

        Console.SetCursorPosition(cursorLeft, cursorTop);
    }

    private void ClearSuggestions(int lineCount)
    {
        if (lineCount <= 0) return;

        var (cursorLeft, cursorTop) = (Console.CursorLeft, Console.CursorTop);

        for (var i = 0; i < lineCount; i++)
        {
            var row = cursorTop + 1 + i;
            if (row < Console.BufferHeight)
            {
                Console.SetCursorPosition(0, row);
                Console.Write("\x1b[K");
            }
        }

        Console.SetCursorPosition(cursorLeft, cursorTop);
    }

    private void ClearInputLine(int charCount)
    {

        for (var i = 0; i < charCount; i++)
            Console.Write("\b \b");
    }

    private void ShowCtrlCBanner(ref bool hintShown)
    {
        var (cx, cy) = (Console.CursorLeft, Console.CursorTop);
        if (cy + 1 < Console.BufferHeight)
        {
            Console.SetCursorPosition(0, cy + 1);
            Console.Write("\x1b[43;30m  ^C  Press Ctrl+C one more time to exit  \x1b[0m\x1b[K");
            Console.SetCursorPosition(cx, cy);
            hintShown = true;
        }
    }

    private static void ShowCtrlCBanner()
    {
        var (cx, cy) = (Console.CursorLeft, Console.CursorTop);
        if (cy + 1 < Console.BufferHeight)
        {
            Console.SetCursorPosition(0, cy + 1);
            Console.Write("\x1b[43;30m  ^C  Press Ctrl+C one more time to exit  \x1b[0m\x1b[K");
            Console.SetCursorPosition(cx, cy);
        }
    }

    private static void ClearCtrlCBanner(ref bool hintShown)
    {
        var (cx, cy) = (Console.CursorLeft, Console.CursorTop);
        if (cy + 1 < Console.BufferHeight)
        {
            Console.SetCursorPosition(0, cy + 1);
            Console.Write("\x1b[K");
            Console.SetCursorPosition(cx, cy);
        }
        hintShown = false;
    }
}
