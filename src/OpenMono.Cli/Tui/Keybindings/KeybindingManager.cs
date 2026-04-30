using System.Text.Json;
using Terminal.Gui.Input;

namespace OpenMono.Tui.Keybindings;

public enum TuiAction
{
    Pause,
    ToggleSidebar,
    ToggleApproval,
    ToggleThinking,
    Help,
    Debug
}

public sealed class KeybindingManager
{
    private readonly Dictionary<Key, TuiAction> _bindings = [];
    private readonly Dictionary<TuiAction, Key> _reverse = [];

    public KeybindingManager(string? configPath = null)
    {

        foreach (var (key, action) in DefaultKeybindings.All)
        {
            _bindings[key] = action;
            _reverse[action] = key;
        }

        if (configPath is not null)
            LoadOverrides(configPath);
    }

    public TuiAction? Resolve(Key key)
    {
        return _bindings.TryGetValue(key, out var action) ? action : null;
    }

    public Key? GetKey(TuiAction action)
    {
        return _reverse.TryGetValue(action, out var key) ? key : null;
    }

    public string GetHint(TuiAction action)
    {
        if (!_reverse.TryGetValue(action, out var key))
            return "???";

        return FormatKeyHint(key);
    }

    public IReadOnlyDictionary<Key, TuiAction> All => _bindings;

    private void LoadOverrides(string configPath)
    {
        if (!File.Exists(configPath))
            return;

        try
        {
            var json = File.ReadAllText(configPath);
            var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (overrides is null)
                return;

            foreach (var (actionStr, keyStr) in overrides)
            {
                if (!Enum.TryParse<TuiAction>(actionStr, ignoreCase: true, out var action))
                    continue;

                var key = ParseKeyString(keyStr);
                if (key is null)
                    continue;

                if (_reverse.TryGetValue(action, out var oldKey))
                    _bindings.Remove(oldKey);

                _bindings[key] = action;
                _reverse[action] = key;
            }
        }
        catch
        {

        }
    }

    private static Key? ParseKeyString(string keyStr)
    {

        var parts = keyStr.Split('+', StringSplitOptions.TrimEntries);
        var ctrl = false;
        var shift = false;
        var alt = false;
        Key? baseKey = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default:
                    baseKey = part.ToUpperInvariant() switch
                    {
                        "A" => Key.A, "B" => Key.B, "C" => Key.C, "D" => Key.D,
                        "E" => Key.E, "F" => Key.F, "G" => Key.G, "H" => Key.H,
                        "I" => Key.I, "J" => Key.J, "K" => Key.K, "L" => Key.L,
                        "M" => Key.M, "N" => Key.N, "O" => Key.O, "P" => Key.P,
                        "Q" => Key.Q, "R" => Key.R, "S" => Key.S, "T" => Key.T,
                        "U" => Key.U, "V" => Key.V, "W" => Key.W, "X" => Key.X,
                        "Y" => Key.Y, "Z" => Key.Z,
                        "F1" => Key.F1, "F2" => Key.F2, "F3" => Key.F3, "F4" => Key.F4,
                        "F5" => Key.F5, "F6" => Key.F6, "F7" => Key.F7, "F8" => Key.F8,
                        "F9" => Key.F9, "F10" => Key.F10, "F11" => Key.F11, "F12" => Key.F12,
                        "ESC" or "ESCAPE" => Key.Esc,
                        "ENTER" or "RETURN" => Key.Enter,
                        "TAB" => Key.Tab,
                        "SPACE" => Key.Space,
                        _ => null
                    };
                    break;
            }
        }

        if (baseKey is null)
            return null;

        var result = baseKey;
        if (ctrl) result = result.WithCtrl;
        if (shift) result = result.WithShift;
        if (alt) result = result.WithAlt;

        return result;
    }

    internal static string FormatKeyHint(Key key)
    {
        var parts = new List<string>();
        if (key.IsCtrl) parts.Add("^");
        if (key.IsShift) parts.Add("Shift+");
        if (key.IsAlt) parts.Add("Alt+");

        var baseKey = key.NoCtrl.NoShift.NoAlt;
        var name = baseKey.ToString().ToUpperInvariant();

        if (parts.Count > 0 && parts[0] == "^")
        {

            return $"^{name}";
        }

        parts.Add(name);
        return string.Join("", parts);
    }
}
