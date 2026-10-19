using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/** A plug's share of a stat; a mod's reads as a change to the stat, any other plug's as part of its base. */
public sealed record StatSource(string Plug, int Value, bool Mod);

/**
 * The stats an item's display group shows, in the game's order, and the value a full bar stands for.
 * @param Stats Each shown stat's place in the list and whether it reads as a number with no bar.
 */
public sealed record ItemStatGroup(int Maximum, IReadOnlyDictionary<byte, (int Position, bool Numeric)> Stats)
{
    private static readonly InvestmentSources Sources = new();

    /** Null when the item's display has no stat group or the group cannot be read. */
    public static ItemStatGroup? Read(PackageCatalog catalog, InvestmentItem item)
    {
        if (item.DisplayTag == 0 || !catalog.ContainsTag(Sources.StatGroups)) return null;
        try
        {
            if (InvestmentStatGroups.ReadDisplayGroup(catalog.Read(item.DisplayTag)) is not { } index) return null;
            var (maximum, stats) = InvestmentStatGroups.ReadGroup(catalog.Read(Sources.StatGroups), index);
            return new(maximum, stats.Select((stat, position) => (stat, position))
                .DistinctBy(row => row.stat.Stat).ToDictionary(row => row.stat.Stat, row => (row.position, row.stat.Numeric)));
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException) { return null; }
    }

    /**
     * What each equipped plug adds to each stat; the game shows an item's own values plus its equipped plugs'. Plugs in the
     * game's mod socket categories read as changes to a stat, and every other plug, such as armor's stat roll, as its base.
     * @param sockets Each socket's equipped plug and socket type.
     */
    public static Dictionary<byte, List<StatSource>> PlugStats(InvestmentCatalog investment, PackageCatalog catalog,
        IEnumerable<(ushort Plug, ushort Type)> sockets)
    {
        var sources = new Dictionary<byte, List<StatSource>>();
        byte[]? types = catalog.ContainsTag(InvestmentSocketTypes.GameplayTable) ? catalog.Read(InvestmentSocketTypes.GameplayTable) : null;
        byte[]? categories = catalog.ContainsTag(InvestmentSocketTypes.CategoryTable)
            ? catalog.Read(InvestmentSocketTypes.CategoryTable) : null;
        foreach (var (plug, type) in sockets.Where(socket => socket.Plug < investment.Items.Count))
            try
            {
                var item = investment.Items[plug];
                bool mod = types is not null && categories is not null
                    && InvestmentSocketTypes.Category(types, categories, type) is { } category
                    && InvestmentSocketTypes.ModCategories.Contains(category);
                foreach (var stat in investment.ReadItem(item.Tag, catalog.Read(item.Tag)).Stats.Where(stat => stat.Value != 0))
                {
                    if (!sources.TryGetValue(stat.Index, out var list)) sources[stat.Index] = list = [];
                    list.Add(new(item.Name, stat.Value, mod));
                }
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { }
        return sources;
    }

    /** What the plugs a stat lists add, the mods' or the others'. */
    public static int Sum(IReadOnlyList<StatSource>? sources, bool mods) =>
        sources?.Where(source => source.Mod == mods).Sum(source => source.Value) ?? 0;

    /**
     * What a stat reading is made of: the item's own raw value, each plug's share, then the value the game shows when a
     * curve changes the total. Empty when the reading is the item's own value as it is.
     */
    public static string Breakdown(int own, IReadOnlyList<StatSource>? sources, int raw, int shown)
    {
        var lines = new List<string>();
        if (own != 0 || sources is not { Count: > 0 }) lines.Add($"Base {own}");
        foreach (var source in sources ?? []) lines.Add($"{source.Plug} {source.Value:+0;-0}");
        if (shown != raw) lines.Add($"Shows {shown}");
        return lines.Count > 1 ? string.Join("\n", lines) : "";
    }

    /** The stat's place in the group; stats the game does not show have none. */
    public int Position(byte stat) => Stats.TryGetValue(stat, out var row) ? row.Position : int.MaxValue;

    /** The game's order: the stats it draws as bars, then its numbers, then the stats it does not show. */
    public int Order(byte stat) =>
        Stats.TryGetValue(stat, out var row) ? (row.Numeric ? Stats.Count : 0) + row.Position : int.MaxValue;

    /** True when the game draws the stat as a bar. */
    public bool Bar(byte stat) => Stats.TryGetValue(stat, out var row) && !row.Numeric;

    /** The value that fills a bar. */
    public int Full => Maximum > 0 ? Maximum : 100;
}
