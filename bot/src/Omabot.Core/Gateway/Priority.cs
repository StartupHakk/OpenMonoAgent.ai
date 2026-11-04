// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Gateway;

/// <summary>Gateway priorities, highest first (plan 7.9).</summary>
public enum Priority
{
    Interactive = 0,
    Assignment = 1,
    Routine = 2,
    Background = 3,
}

public static class Priorities
{
    public const int Count = 4;

    public static string Name(Priority p) => p switch
    {
        Priority.Interactive => "interactive",
        Priority.Assignment => "assignment",
        Priority.Routine => "routine",
        _ => "background",
    };

    public static Priority Parse(string? s) => s switch
    {
        "interactive" => Priority.Interactive,
        "assignment" => Priority.Assignment,
        "routine" => Priority.Routine,
        "background" => Priority.Background,
        _ => throw new ArgumentException($"Unknown priority \"{s}\"."),
    };
}
