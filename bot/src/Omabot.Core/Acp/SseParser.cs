// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Omabot.Acp;

public sealed record SseEvent(string Name, string Data);

/// <summary>
/// Incremental parser for `event: NAME\ndata: JSON\n\n` frames. Handles split
/// chunks, CRLF, multi-line data and comment lines. A frame with no event
/// line is named "message" as in the SSE spec.
/// </summary>
public sealed class SseParser
{
    private readonly StringBuilder _line = new();
    private string? _event;
    private readonly StringBuilder _data = new();
    private bool _hasData;
    private bool _lastWasCr;

    public IEnumerable<SseEvent> Feed(string chunk)
    {
        var output = new List<SseEvent>();
        foreach (var ch in chunk)
        {
            if (ch == '\n' && _lastWasCr) { _lastWasCr = false; continue; }
            _lastWasCr = ch == '\r';
            if (ch is '\n' or '\r')
            {
                var line = _line.ToString();
                _line.Clear();
                if (line.Length == 0)
                {
                    if (_hasData || _event is not null)
                        output.Add(new SseEvent(_event ?? "message", _data.ToString()));
                    _event = null;
                    _data.Clear();
                    _hasData = false;
                    continue;
                }
                if (line[0] == ':') continue;
                var colon = line.IndexOf(':');
                var field = colon < 0 ? line : line[..colon];
                var value = colon < 0 ? "" : line[(colon + 1)..];
                if (value.StartsWith(' ')) value = value[1..];
                if (field == "event") _event = value;
                else if (field == "data")
                {
                    if (_hasData) _data.Append('\n');
                    _data.Append(value);
                    _hasData = true;
                }
                continue;
            }
            _line.Append(ch);
        }
        return output;
    }

    /// <summary>Call when the stream ends; returns a final frame that had no blank line after it.</summary>
    public SseEvent? Flush()
    {
        if (_line.Length > 0) Feed("\n");
        if (!_hasData && _event is null) return null;
        var e = new SseEvent(_event ?? "message", _data.ToString());
        _event = null;
        _data.Clear();
        _hasData = false;
        return e;
    }
}
