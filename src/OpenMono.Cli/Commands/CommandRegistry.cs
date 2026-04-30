namespace OpenMono.Commands;

public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ICommand command)
    {
        _commands[command.Name] = command;
    }

    public ICommand? Resolve(string name)
    {

        var normalized = name.StartsWith('/') ? name[1..] : name;
        return _commands.GetValueOrDefault(normalized);
    }

    public IReadOnlyCollection<ICommand> All => _commands.Values;

    public bool IsCommand(string input)
    {
        if (!input.StartsWith('/')) return false;
        var name = input.Split(' ', 2)[0][1..];
        return _commands.ContainsKey(name);
    }
}
