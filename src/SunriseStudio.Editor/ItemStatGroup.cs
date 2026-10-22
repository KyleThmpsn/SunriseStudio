using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/**
 * A plug's share of a stat; it reads as a change to the stat, or as part of its base for armor's stat roll and traits.
 * A conditional share applies only while its condition holds, so no total counts it.
 */
public sealed record StatSource(string Plug, int Value, bool Change, bool Conditional);

/** A weapon's shown Magazine, null when it did not read, and the note its row carries, or none. */
public sealed record ItemMagazine(InvestmentStatCurve? Curve, string Note);

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
     * What each equipped plug adds to each stat; the game shows an item's own values plus its equipped plugs'. A plug reads
     * as a change to a stat, but for the game's armor perks socket category, armor's stat roll and traits, which is its base.
     * @param sockets Each socket's equipped plug and socket type.
     */
    public static Dictionary<byte, List<StatSource>> PlugStats(InvestmentCatalog investment, PackageCatalog catalog,
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

    /**
     * A weapon's Magazine curve, translated at its other stats, in place of its display group's curve, and the note its
     * row carries. A Magazine that does not read shows its raw value and says why. Null for an item that is not a weapon
     * or whose translator writes no capacity.
     * @param stats The item's stats with its equipped plugs', by stat index.
     */
    public static ItemMagazine? Magazine(InvestmentCatalog investment, PackageCatalog catalog, InvestmentItem item,
        IReadOnlyDictionary<byte, int> stats)
    {
        if (item.Category != InvestmentCategory.Weapons) return null;
        try
        {
            return WeaponTuningAuthoring.ReadMagazine(catalog, investment, item, stats) is { } magazine
                ? new(magazine.Curve, magazine.Unchecked ? "Magazine may be inaccurate." : "") : null;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
        { return new(null, "Magazine did not read: " + error.Message); }
    }

    /** The curve a stat's shown value reads: a weapon's translated Magazine, else its display group's curve. */
    public static InvestmentStatCurve? Curve(InvestmentItemDetail detail, byte stat, ItemMagazine? magazine) =>
        stat == WeaponTranslationEdits.MagazineStat ? magazine?.Curve : detail.DisplayCurves.GetValueOrDefault(stat);

    /** The item's own stats and what its plugs always add, by stat index. */
    public static Dictionary<byte, int> Totals(IEnumerable<InvestmentStat> own, IReadOnlyDictionary<byte, List<StatSource>> plugs)
    {
        var totals = plugs.ToDictionary(pair => pair.Key, pair => Sum(pair.Value, false) + Sum(pair.Value, true));
        foreach (var stat in own) totals[stat.Index] = totals.GetValueOrDefault(stat.Index) + stat.Value;
        return totals;
    }

    /** What the plugs a stat lists always add, the changes' or the base's. */
    public static int Sum(IReadOnlyList<StatSource>? sources, bool changes) =>
        sources?.Where(source => source.Change == changes && !source.Conditional).Sum(source => source.Value) ?? 0;

    /**
     * What a stat reading is made of: the item's own raw value, each plug's share, then the value the game shows when a
     * curve changes the total, and a note on that value. Empty when the reading is the item's own value as it is.
     */
    public static string Breakdown(int own, IReadOnlyList<StatSource>? sources, int raw, int shown, string note = "")
    {
        var lines = new List<string>();
        if (own != 0 || sources is not { Count: > 0 }) lines.Add($"Base {own}");
        foreach (var source in sources ?? [])
            lines.Add($"{source.Plug} {source.Value:+0;-0}" + (source.Conditional ? " while its condition holds" : ""));
        if (shown != raw) lines.Add($"Shows {shown}");
        if (note.Length != 0) lines.Add(note);
        return lines.Count > 1 || sources?.Any(source => source.Conditional) == true ? string.Join("\n", lines) : "";
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
