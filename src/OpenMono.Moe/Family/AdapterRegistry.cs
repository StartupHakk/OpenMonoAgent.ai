namespace OpenMono.Moe.Family;

public sealed class AdapterRegistry
{
    private readonly Dictionary<string, IModelFamilyAdapter> _byId = new(StringComparer.OrdinalIgnoreCase);

    public AdapterRegistry()
    {
        Register(new MimoV26FlashAdapter());
        Register(new Qwen38FlashNextAdapter());
    }

    public void Register(IModelFamilyAdapter adapter) => _byId[adapter.Id] = adapter;

    public IModelFamilyAdapter? Resolve(string id) => _byId.GetValueOrDefault(id);

    public IModelFamilyAdapter RequireOwned(string id)
    {
        var adapter = Resolve(id) ?? throw new KeyNotFoundException($"Unknown Mosaic family '{id}'.");
        if (!adapter.OwnsRuntime)
        {
            throw new NotSupportedException(
                $"{adapter.DisplayName} is owned by {adapter.RuntimeOwner}, not Mosaic.");
        }
        return adapter;
    }

    public IReadOnlyCollection<IModelFamilyAdapter> All => _byId.Values;
}
