using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/**
 * A plug's share of a stat; it reads as a change to the stat, or as part of its base for armor's stat roll and traits.
 * A conditional share applies only while its condition holds, so no total counts it.
 */
public sealed record StatSource(string Plug, int Value, bool Change, bool Conditional);

/** What an item's equipped plugs add to its stats, and where its display group puts each stat. */
public static class StatSources
{
    /** The value that fills a bar when the display group names none. */
    private const int DefaultFullBar = 100;

    /**
     * What each equipped plug adds to each stat; the game shows an item's own values plus its equipped plugs'. A plug reads
     * as a change to a stat, but for the game's armor perks socket category, armor's stat roll and traits, which is its base.
     * @param sockets Each socket's equipped plug and socket type.
     */
    public static Dictionary<byte, List<StatSource>> Read(InvestmentCatalog investment, PackageCatalog catalog,
        IEnumerable<(ushort Plug, ushort Type)> sockets)
    {
        var sources = new Dictionary<byte, List<StatSource>>();
        byte[]? types = catalog.ContainsTag(InvestmentSocketTypes.GameplayTable)
            ? catalog.Read(InvestmentSocketTypes.GameplayTable) : null;
        byte[]? categories = catalog.ContainsTag(InvestmentSocketTypes.CategoryTable)
            ? catalog.Read(InvestmentSocketTypes.CategoryTable) : null;
        foreach (var (plug, type) in sockets.Where(socket => socket.Plug < investment.Items.Count))
            try
            {
                var item = investment.Items[plug];
                bool change = types is null || categories is null
                    || InvestmentSocketTypes.Category(types, categories, type) != InvestmentSocketTypes.ArmorPerksCategory;
                foreach (var stat in investment.ReadItem(item.Tag, catalog.Read(item.Tag)).Stats.Where(stat => stat.Value != 0))
                {
                    if (!sources.TryGetValue(stat.Index, out var list)) sources[stat.Index] = list = [];
                    list.Add(new(item.Name, stat.Value, change, stat.Conditional));
                }
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { }
        return sources;
    }

    /** What the plugs a stat lists always add, the changes' or the base's. */
    public static int Sum(IReadOnlyList<StatSource>? sources, bool changes) =>
        sources?.Where(source => source.Change == changes && !source.Conditional).Sum(source => source.Value) ?? 0;

    /**
     * What a stat reading is made of: the item's own raw value, each plug's share, then the value the game shows when a
     * curve changes the total. Empty when the reading is the item's own value as it is.
     */
    public static string Breakdown(int own, IReadOnlyList<StatSource>? sources, int raw, int shown)
    {
        var lines = new List<string>();
        if (own != 0 || sources is not { Count: > 0 }) lines.Add($"Base {own}");
        foreach (var source in sources ?? [])
            lines.Add($"{source.Plug} {source.Value:+0;-0}" + (source.Conditional ? " while its condition holds" : ""));
        if (shown != raw) lines.Add($"Shows {shown}");
        return lines.Count > 1 || sources?.Any(source => source.Conditional) == true ? string.Join("\n", lines) : "";
    }

    /** The display group that lists stats, or null: a display with no stat block, or one whose block names no group. */
    public static InvestmentStatGroup? Group(InvestmentItemDetail detail) =>
        detail.StatGroup is { Stats.Count: > 0 } group ? group : null;

    /** The group's row for a stat, or null for a stat the game does not show. */
    public static InvestmentGroupStat? Row(this InvestmentStatGroup group, byte stat)
    {
        foreach (var row in group.Stats)
            if (row.Stat == stat) return row;
        return null;
    }

    /** True when the game shows the stat. */
    public static bool Shows(this InvestmentStatGroup group, byte stat) => group.Row(stat) is not null;

    /** The game's order: the stats it draws as bars, then its numbers, then the stats it does not show. */
    public static int Order(this InvestmentStatGroup group, byte stat)
    {
        for (int position = 0; position < group.Stats.Count; position++)
            if (group.Stats[position].Stat == stat) return (group.Stats[position].Numeric ? group.Stats.Count : 0) + position;
        return int.MaxValue;
    }

    /** True when the game draws the stat as a bar. */
    public static bool Bar(this InvestmentStatGroup group, byte stat) => group.Row(stat) is { Numeric: false };

    /** The stats the game draws as bars. */
    public static IEnumerable<byte> Bars(this InvestmentStatGroup group) =>
        group.Stats.Where(row => !row.Numeric).Select(row => row.Stat);

    /** The value that fills a bar. */
    public static int Full(InvestmentStatGroup? group) => group is { FullBar: > 0 } ? group.FullBar : DefaultFullBar;
}
