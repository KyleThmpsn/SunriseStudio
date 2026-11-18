using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/**
 * An asset's tooltip. Items get the game's: band, package, description, figure, stat bars and default perks;
 * other assets the band and package. Stats and perks read on a worker.
 */
public static class AssetTooltip
{
    /** Drawn larger than the editor's text, nearer the game's size. */
    public const float Zoom = 1.2f;
    // Layout in unscaled pixels. The tooltip is a fixed column, so stats and perks line up down it.
    private const float Width = 330, Padding = 8, BandHeight = 52, BandTypeGap = 7, BlockGap = 4;
    private const float StatValueWidth = 34, StatBarHeight = 10, StatColumnGap = 8, StatRowGap = -2;
    private const float StatBlockPadding = .7f;
    private const float PerkIconExtent = 22, PerkIconPadding = 3, PerkRowHeight = 30, ClipSlack = 10;
    // Name, type, figure and energy sizes relative to the body text; a mark is set at half its figure's size.
    private const float NameScale = 2.05f, TypeScale = 1.35f, FigureScale = 2.6f, EnergyScale = 1.31f, MarkScale = .5f;
    // A wrapped name steps by cap heights; a face's line height keeps room capitals never use.
    private const float NameLeading = 1.3f;
    // Stroke widths that fake the heavier cuts when the game's faces are missing.
    private const float NameWeight = 1.15f, FigureWeight = 2, EnergyWeight = .9f, BoldStrikeStep = .6f;
    // The figure row: its box is pulled up past unused ascender room, and type beside it hangs near its baseline.
    private const float FigureTopTrim = 7, FigureRuleGap = 7, FigureMarkGap = 1.5f, FigureMiddle = .5f, TextAnchor = .576f;
    private const float FigureRuleHeight = .58f, FigureRowHeight = .74f;
    // The ammunition mark's own margin is deeper below it.
    private const float AmmoMarkExtent = 30, AmmoWordGap = 1, AmmoMarkDrop = .06f;
    // A plug or perk's tooltip is narrower, its band drawn at this share of an item's.
    private const float PlugWidth = 250, PlugBand = .76f;
    private const int DefaultCeiling = 100, CachedItems = 64;
    // Stat rows that title gear: Power, which very few items set, and armor's Defense.
    private const byte PowerStat = 0, DefenseStat = 2;
    private const string TotalLabel = "Total";
    // The longest stat name the game shows, so every tooltip's bars start in one place.
    private const string LongestStat = "Rounds Per Minute";
    // The game's tooltip colours: a band per rarity tier, a near-black body, white bars and faint rules.
    private static readonly Vector4[] RarityTints =
    [
        new(.765f, .737f, .706f, 1), new(.765f, .737f, .706f, 1), new(.212f, .435f, .259f, 1),
        new(.314f, .463f, .639f, 1), new(.322f, .184f, .396f, 1), new(.808f, .682f, .2f, 1)
    ];
    private static readonly Vector4 BodyColor = new(.09f, .09f, .10f, .97f), BorderColor = new(1, 1, 1, .12f);
    private static readonly Vector4 IntrinsicFill = new(1, 1, 1, .07f);
    private static readonly Vector4 BarTrack = new(1, 1, 1, .14f), BarFill = new(.93f, .93f, .93f, 1);
    private static readonly Vector4 RuleColor = new(1, 1, 1, .10f), FigureRuleColor = new(1, 1, 1, .30f);
    private static readonly Vector4 MutedColor = new(1, 1, 1, .55f);
    // Common and Exotic bands are light, so their text is dark; muted band text keeps part of its opacity.
    private static readonly Vector4 DarkBandText = new(.08f, .08f, .09f, 1), LightBandText = new(1, 1, 1, 1);
    private const float MutedBandText = .72f, IconBackdrop = .17f;
    // Arc, Solar and Void by damage type, after Kinetic: the game's symbol face code points and their tints.
    private static readonly char[] Marks = ['\0', '\uE143', '\uE140', '\uE144'];
    private static readonly Vector4[] MarkTints =
        [default, new(.47f, .82f, .96f, 1), new(.96f, .55f, .22f, 1), new(.70f, .49f, .93f, 1)];
    // Icon rows of the game's ammunition marks in this build, which no item names; an unread row leaves the mark off.
    private static readonly int[] AmmoMarks = [0, 8380, 8379, 8381];
    private static readonly InvestmentSources Sources = new();

    /** A font drawn at a size; the weight strikes a regular face heavier when the game's faces are missing. */
    private readonly record struct Face(ImFontPtr Font, float Size, float Weight = 0);
    /** The figure row's lines: marks centre on the figure's digits, and type hangs from the anchor. */
    private readonly record struct Row(float Centre, float Anchor, float Height);
    private sealed record Stat(byte Index, string Name, int Value, bool Numeric);
    /** A default perk; the intrinsic sits on a panel. */
    private sealed record Perk(InvestmentItem Plug, bool Intrinsic);
    /** The figure an item leads with, its damage type, ammunition, armor energy and the word the row ends on. */
    private sealed record Lockup(int Figure, int Damage, int Ammo, int Energy, int EnergyType, string Label);
    /**
     * Title is the figure's stat row, or -1. Signed marks a plug, whose stats are changes; Description is its
     * perks' text, for a plug with none of its own.
     */
    private sealed record Content(IReadOnlyList<Stat> Stats, int Ceiling, int Title, bool Armor, bool Signed, Lockup? Lockup,
        IReadOnlyList<Perk> Perks, string Description = "");

    private sealed class Entry(Task<Content> work)
    {
        public readonly Task<Content> Work = work;
        public long Drawn;
    }

    private static readonly Dictionary<uint, Entry> Entries = [];
    // Reads the item catalog the first time an item's tooltip shows before it is read.
    private static readonly InvestmentTablesLoader Loader = new();
    /** Plug descriptions read from their perks, by plug tag. */
    private static readonly Dictionary<uint, Task<string>> PerkTexts = [];
    private static StudioWorkspace? workspace;
    private static InvestmentCatalog? source;
    // The source catalog's power caps and season cap, read once by the first worker that needs them.
    private static Lazy<(IReadOnlyList<int> Caps, int? Season)?>? power;
    private static InvestmentIconCache icons = InvestmentIconCache.Unprepared;
    private static GameFonts? fonts;
    private static float scale = 1;
    private static long frame;

    /** Runs between frames: drops a replaced catalog's entries and keeps the cache small. */
    public static void Prepare(StudioWorkspace current, InvestmentIconCache cache, GameFonts? faces, float uiScale)
    {
        workspace = current; icons = cache; fonts = faces; scale = uiScale * Zoom; frame++;
        if (!ReferenceEquals(source, current.LoadedInvestment))
        { Entries.Clear(); PerkTexts.Clear(); power = null; source = current.LoadedInvestment; }
        // The least recently drawn go first; a removed entry's worker finishes unread.
        var stale = Entries.OrderBy(pair => pair.Value.Drawn).Take(Entries.Count - CachedItems).Select(pair => pair.Key);
        foreach (uint tag in stale.ToList()) Entries.Remove(tag);
    }

    /** The tooltip for a browser row. */
    public static void Draw(BrowserAsset row) => Draw(row.Entry.TagHash, row.Entry.PackageName, row.Name, row.Kind);

    /** The tooltip for a tag; its package comes from the catalog. */
    /** @param note A line under the strip for an asset with no description of its own, such as a map's bubble count. */
    public static void Draw(uint tag, string name, string kind, string note = "") => Draw(tag, Package(tag), name, kind, note);

    /**
     * The tooltip for a map or mission: its title in the band, its code name under it and its kind at the far end.
     * @param note A line under the strip. @param points Lines listed under the note, each after a mark.
     */
    public static void DrawWorld(uint tag, string name, string kind, string note, IReadOnlyList<string> points)
    {
        // A titled world's name ends in its code name in brackets.
        int open = name.EndsWith(')') ? name.LastIndexOf(" (", StringComparison.Ordinal) : -1;
        if (open > 0) Draw(tag, Package(tag), name[..open], name[(open + 2)..^1], note, kind, points);
        else Draw(tag, Package(tag), name, "", note, kind, points);
    }

    // The bullet the game's own text puts before a listed line.
    private const string Bullet = "\u2022 ";

    /** Lines under a description, each after a bullet, as the game's perk text lists them. */
    private static void DrawPoints(IReadOnlyList<string> points)
    {
        foreach (string point in points) Muted(Bullet + point);
    }

    private static string Package(uint tag) =>
        workspace?.Catalog is { } catalog && catalog.TryGetEntry(tag, out var entry) ? entry.PackageName : "";

    /** @param far A word for the band's far end in place of a tier. @param points Lines listed under the description. */
    private static void Draw(uint tag, string package, string name, string kind, string note = "", string far = "",
        IReadOnlyList<string>? points = null)
    {
        // A perk program's row shows the sandbox perk it runs.
        if (workspace?.Catalog is { } packages && workspace.LoadedInvestment is { } loaded
            && PerkPrograms.PerkOf(packages, loaded, tag) is { } perk)
        { DrawPerk(loaded, perk, name, package, tag); return; }
        // The names index knows an item's type and tier before the investment catalog loads.
        InvestmentItemFacts facts = default;
        bool named = workspace?.Names?.TryGetItem(tag, out facts) == true;
        var investment = named ? Loader.RequestCatalog(workspace!) : workspace?.LoadedInvestment;
        var item = investment?.FindItem(tag);
        // A plug that is not gear is drawn in the narrower form, its stats what it changes.
        bool plug = named && facts.HasPlug && facts.Bucket > InvestmentItemKinds.LastArmorBucket;
        var entry = item is not null ? Request(investment!, item, plug) : null;
        var content = entry?.Work.IsCompletedSuccessfully == true ? entry.Work.Result : null;
        string notice = entry?.Work.IsFaulted == true
            ? "Stats and perks did not read: " + entry.Work.Exception!.GetBaseException().Message : "";
        string description = item?.Description is { Length: > 0 } own ? own
            : content?.Description is { Length: > 0 } said ? said : note;
        int title = content?.Title ?? -1;
        bool stats = content is not null && content.Stats.Count > (title < 0 ? 0 : 1), perks = content?.Perks.Count > 0;
        bool figures = content?.Lockup is not null || stats || perks || notice.Length != 0;

        Begin();
        float width = plug ? SmallWidth(package, tag) : Width * scale;
        DrawBand(item?.Name ?? name, item?.TypeName ?? (named ? facts.TypeLine : kind),
            item?.Tier ?? (named ? facts.Tier : (byte)0), item is not null && !icons.NoIcon(investment!, item.Index),
            item?.Bucket ?? facts.Bucket, item is null ? null : icons.Get(investment!, item.Index), width, plug ? PlugBand : 1,
            far);
        DrawSource(package, tag, width);
        if (description.Length != 0)
        {
            Gap();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            Muted(description);
            ImGui.PopTextWrapPos();
            if (points is not null) DrawPoints(points);
            if (figures) { Gap(); Rule(); }
        }
        if (content?.Lockup is { } lockup) { Gap(); DrawLockup(lockup); }
        if (stats) { Gap(); DrawStats(content!, width); }
        if (notice.Length != 0)
        {
            Gap();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            Muted(notice);
            ImGui.PopTextWrapPos();
        }
        for (int i = 0; i < (content?.Perks.Count ?? 0); i++)
        {
            if (i != 0) Rule();
            DrawPerkRow(content!.Perks[i], icons.Get(investment!, content.Perks[i].Plug.Index), width);
        }
        // Text and the figure, whose digits run below the row they reserve, keep a padding under them.
        bool figureLast = content?.Lockup is not null && !stats && notice.Length == 0 && !perks;
        End(description.Length != 0 && !figures || notice.Length != 0 && !perks || figureLast);
    }

    /** A sandbox perk's tooltip in plug form: its package index, then its definition hash. */
    public static void DrawPerk(int perk, string name)
    {
        var investment = workspace?.LoadedInvestment;
        DrawPerk(investment, perk, name, $"Sandbox perk {perk}", Definition(investment, perk)?.Hash ?? 0);
    }

    /** A sandbox perk's tooltip over a source strip: its index and hash, or the program row it came from. */
    private static void DrawPerk(InvestmentCatalog? investment, int perk, string name, string package, uint hash) =>
        DrawSmall(name, "Sandbox Perk", 0, int.MaxValue, investment is null || !icons.NoPerkIcon(investment, perk),
            investment is null ? null : icons.Perk(investment, perk), package, hash,
            Definition(investment, perk)?.Description ?? "");

    private static InvestmentPerkDefinition? Definition(InvestmentCatalog? investment, int perk) =>
        investment is not null && perk >= 0 && perk < investment.PerkCount ? investment.PerkDefinitions[perk] : null;

    /** @param square Leaves room for the icon; false once it has none. */
    private static void DrawSmall(string name, string type, byte tier, int bucket, bool square, GpuTexture? icon, string package,
        uint hash, string description)
    {
        Begin();
        float width = SmallWidth(package, hash);
        DrawBand(name, type, tier, square, bucket, icon, width, PlugBand);
        DrawSource(package, hash, width);
        if (description.Length != 0)
        {
            Gap();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            Muted(description);
            ImGui.PopTextWrapPos();
        }
        End(description.Length != 0);
    }

    /** A plug or perk tooltip's width in an open tooltip, fitting the package name and hash on one line. */
    private static float SmallWidth(string package, uint hash) => Math.Max(PlugWidth * scale, ImGui.CalcTextSize(package).X
        + ImGui.CalcTextSize(hash.ToString("X8", CultureInfo.InvariantCulture)).X + (Padding + ClipSlack) * scale);

    /** A plug's description, else its perks' text, read once on a worker; empty until then. */
    public static string PlugDescription(InvestmentItem plug)
    {
        if (!string.IsNullOrWhiteSpace(plug.Description) || workspace?.LoadedInvestment is not { } investment
            || workspace.Catalog is not { } catalog) return plug.Description;
        if (!PerkTexts.TryGetValue(plug.Tag, out var work))
            PerkTexts.Add(plug.Tag,
                work = Task.Run(() => PerkText(investment, investment.ReadItem(plug.Tag, catalog.Read(plug.Tag)))));
        return work.IsCompletedSuccessfully ? work.Result : "";
    }

    /** What an item's perks say, each once. */
    private static string PerkText(InvestmentCatalog investment, InvestmentItemDetail detail) => string.Join("\n\n",
        detail.Perks.Where(perk => perk.Index < investment.PerkCount)
            .Select(perk => investment.PerkDefinitions[perk.Index].Description.Trim())
            .Where(text => text.Length != 0).Distinct());

    private static void Begin()
    {
        // Square, as the game's tooltips are.
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0);
        // Padding at the sides only: the band fills the top, and the last row ends the tooltip.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Padding * scale, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, BodyColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.BeginTooltip();
        // The game's faces are baked at the zoomed size; the editor's own face is scaled up in their place.
        if (fonts is not null) ImGui.PushFont(fonts.Text);
        else ImGui.SetWindowFontScale(Zoom);
    }

    /** Closes the tooltip; text or the figure keeps a padding under it, rows and the strip end flush. */
    private static void End(bool text)
    {
        if (text) ImGui.Dummy(new(0, Padding * scale));
        if (fonts is not null) ImGui.PopFont();
        else ImGui.SetWindowFontScale(1);
        ImGui.EndTooltip();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    /** The item's entry, read on a worker the first time it is drawn. */
    private static Entry Request(InvestmentCatalog investment, InvestmentItem item, bool plug)
    {
        if (!Entries.TryGetValue(item.Tag, out var entry))
        {
            var catalog = workspace!.Catalog!;
            // The first gear tooltip reads the cap tables on its worker; later ones share them.
            var table = power ??= new(() => ReadPower(investment, catalog));
            Entries.Add(item.Tag, entry = new(Task.Run(() => Read(investment, catalog, item, plug, table))));
        }
        entry.Drawn = frame;
        return entry;
    }

    /**
     * Granted plugs' stats count toward the item's. The display group picks and orders the stats, and its curves give
     * the shown values. A plug shows its signed changes and no perks.
     */
    private static Content Read(InvestmentCatalog investment, PackageCatalog catalog, InvestmentItem item, bool plug,
        Lazy<(IReadOnlyList<int> Caps, int? Season)?> power)
    {
        var detail = investment.ReadItem(item.Tag, catalog.Read(item.Tag));
        if (plug)
        {
            var changes = detail.Stats
                .Where(stat => stat.Value != 0 && investment.StatName(stat.Index) != $"Stat {stat.Index}")
                .Select(stat => new Stat(stat.Index, investment.StatName(stat.Index), stat.Value, true)).ToList();
            // A plug often leaves its own description blank and lets its perks describe it.
            return new(changes, DefaultCeiling, -1, false, true, null, [], PerkText(investment, detail));
        }
        var plugs = detail.Sockets.Where(socket => socket.InitialPlug < investment.Items.Count)
            .Select(socket => investment.Items[socket.InitialPlug])
            .Select(granted => (Plug: granted, Detail: ReadPlug(investment, catalog, granted))).ToArray();
        var totals = Totals(detail, plugs.Select(granted => granted.Detail).OfType<InvestmentItemDetail>());
        bool armor = item.Category == InvestmentCategory.Armor;
        var group = detail.StatGroup is { Stats.Count: > 0 } listed ? listed : null;
        // Armor shows every bar of its group, a zero roll empty; a magazine with no shot is left out. No group shows raw values.
        var shown = group is null
            ? totals.OrderBy(pair => pair.Key)
                .Select(pair => new Stat(pair.Key, investment.StatName(pair.Key), pair.Value, false))
            : group.Stats.Where(row => totals.ContainsKey(row.Stat) || armor && !row.Numeric).OrderBy(row => row.Numeric)
                .Select(row => new Stat(row.Stat, investment.StatName(row.Stat),
                    row.Shown(totals.GetValueOrDefault(row.Stat)), row.Numeric))
                .Where(stat => stat.Index != WeaponTranslationEdits.MagazineStat || stat.Value > 0);
        // An unnamed stat is bookkeeping no player is shown.
        var stats = shown.Where(stat => stat.Name != $"Stat {stat.Index}").ToList();
        // Gear leads with Power, anything else with its first declared stat; a plug's stats are all changes.
        bool gear = item.Category is InvestmentCategory.Weapons or InvestmentCategory.Armor;
        int title = gear || detail.StatGroup is null || detail.Stats.Count == 0 ? -1
            : stats.FindIndex(stat => stat.Index == detail.Stats[0].Index);
        var lockup = gear ? ReadLockup(investment, catalog, item, detail, totals, power)
            : title >= 0 ? new Lockup(stats[title].Value, 0, 0, 0, 0, stats[title].Name.ToUpperInvariant()) : null;
        var perks = plugs.Where(granted => granted.Detail is { } own && Listed(investment, catalog, granted.Plug, own))
            .Select(granted => new Perk(granted.Plug,
                granted.Plug.TypeName.Contains("intrinsic", StringComparison.OrdinalIgnoreCase))).ToList();
        return new(stats, group is { FullBar: > 0 } ? group.FullBar : DefaultCeiling, title, armor, false, lockup, perks);
    }

    /** A granted plug's own stats and perks, or null when it does not read. */
    private static InvestmentItemDetail? ReadPlug(InvestmentCatalog investment, PackageCatalog catalog, InvestmentItem plug)
    {
        try { return investment.ReadItem(plug.Tag, catalog.Read(plug.Tag)); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { return null; }
    }

    /** The item's own stats plus what each granted plug always adds, by stat index. */
    private static Dictionary<byte, int> Totals(InvestmentItemDetail detail, IEnumerable<InvestmentItemDetail> plugs)
    {
        var totals = new Dictionary<byte, int>();
        void Add(InvestmentStat stat) => totals[stat.Index] = totals.GetValueOrDefault(stat.Index) + stat.Value;
        foreach (var stat in detail.Stats) Add(stat);
        foreach (var stat in plugs.SelectMany(plug => plug.Stats).Where(stat => stat.Value != 0 && !stat.Conditional)) Add(stat);
        return totals;
    }

    /** The power cap table with the season's cap, or null when the installation has no table of that class. */
    private static (IReadOnlyList<int> Caps, int? Season)? ReadPower(InvestmentCatalog investment, PackageCatalog catalog)
    {
        if (!catalog.TryGetEntry(Sources.PowerCaps, out var table) || table.Reference != InvestmentPowerCaps.TableClass)
            return null;
        var caps = InvestmentPowerCaps.Read(catalog.Read(Sources.PowerCaps));
        return (caps, InvestmentPowerCaps.SeasonCap(investment, catalog, caps));
    }

    /**
     * Gear leads with its Power, else its power cap held to the season's. Weapons add damage type and ammunition,
     * armor its energy, else Defense.
     */
    private static Lockup? ReadLockup(InvestmentCatalog investment, PackageCatalog catalog, InvestmentItem item,
        InvestmentItemDetail detail, Dictionary<byte, int> totals, Lazy<(IReadOnlyList<int> Caps, int? Season)?> table)
    {
        int set = detail.Stats.FirstOrDefault(stat => stat.Index == PowerStat)?.Value ?? 0;
        int? figure = set > 0 ? set : null;
        if (figure is null && table.Value is (var caps, var season)
            && InvestmentPowerCaps.ItemCap(catalog.Read(item.Tag), caps) is { } cap)
            figure = cap > season ? season : InvestmentPowerCaps.IsLimit(cap) ? cap : null;
        if (figure is not { } power) return null;
        if (item.Category == InvestmentCategory.Weapons)
        {
            int damage = InvestmentDamageType.Installed(investment)
                ? InvestmentDamageType.ReadWeapon(investment, catalog, item) ?? 0 : 0;
            return InvestmentAmmoType.Read(catalog, item) is { } ammo
                ? new(power, damage, ammo, 0, 0, InvestmentAmmoType.Name(ammo).ToUpperInvariant())
                : new(power, damage, 0, 0, 0, investment.StatName(PowerStat).ToUpperInvariant());
        }
        // Energy capacity is a stat of its own, named for the energy type it holds.
        foreach (var (stat, value) in totals)
        {
            string name = investment.StatName(stat);
            if (value <= 0 || !name.EndsWith("Energy Capacity", StringComparison.Ordinal)) continue;
            int type = Array.FindIndex(InvestmentDamageType.Names, element => name.StartsWith(element, StringComparison.Ordinal));
            return new(power, 0, 0, value, Math.Max(type, 0), "ENERGY");
        }
        return new(power, 0, 0, 0, 0, investment.StatName(DefenseStat).ToUpperInvariant());
    }

    /**
     * The plugs the game lists as perks: named, typed and showing something the tooltip doesn't. Empty sockets,
     * upkeep plugs, shaders and damage type markers are left out.
     */
    private static bool Listed(InvestmentCatalog investment, PackageCatalog catalog, InvestmentItem plug,
        InvestmentItemDetail detail)
    {
        if (plug.DisplayTag == 0 || plug.Name == $"Item {plug.Index}" || plug.Name == catalog.GetName(plug.Tag)) return false;
        if (plug.TypeName.Length == 0 || plug.Bucket == InvestmentItemKinds.ShaderBucket) return false;
        return detail.Stats.Any(stat => stat.Value != 0)
            || detail.Perks.Any(perk => InvestmentDamageType.Marker(investment, perk.Index) is null);
    }

    /** The rarity band: icon in the corner, name in capitals, type below, tier at the far end; a long name wraps. */
    private static void DrawBand(string name, string type, byte tier, bool item, int bucket, GpuTexture? icon, float width,
        float size = 1, string far = "")
    {
        if (tier >= RarityTints.Length) tier = 0;
        // Whole pixels, so the icon's edge never falls inside one.
        float padding = Padding * scale, square = MathF.Round(BandHeight * scale * size);
        var (left, right) = Span();
        var origin = new Vector2(left, ImGui.GetWindowPos().Y + ImGui.GetStyle().PopupBorderSize);
        float textLeft = origin.X + padding + (item ? square : 0), room = right - padding - textLeft;
        Face nameFace = Scaled(NameFace(), size), typeFace = Scaled(TypeFace(), size);
        var (nameCapTop, nameCap) = Ink(nameFace, 'H');
        var (typeCapTop, typeCap) = Ink(typeFace, 'H');
        string? tierName = far.Length != 0 ? far : tier == 0 ? null : InvestmentItemKinds.TierName(tier);
        float tierWidth = tierName is null ? 0 : Measure(typeFace, tierName).X, gap = BandTypeGap * scale * size;
        var lines = Wrap(nameFace, name.ToUpperInvariant(), room - ClipSlack * scale);
        // The first line and the type centre on their capitals; each further name line adds its leading.
        float leading = nameCap * NameLeading, added = (lines.Count - 1) * leading;
        float below = type.Length != 0 || tierName is not null ? gap + typeCap : 0;
        float height = square + added, inkTop = origin.Y + Math.Max(0, square - nameCap - below) * .5f;
        var draw = ImGui.GetWindowDrawList();
        var tint = RarityTints[tier];
        draw.AddRectFilled(origin, new(right, origin.Y + height), ImGui.GetColorU32(tint));
        if (item) DrawIcon(icon, tint, bucket, origin, square);
        for (int i = 0; i < lines.Count; i++)
            Clipped(nameFace, lines[i], new(textLeft, inkTop - nameCapTop + i * leading), room, BandText(tier, false));
        uint muted = BandText(tier, true);
        float typeTop = inkTop + added + nameCap + gap - typeCapTop;
        if (type.Length != 0) Clipped(typeFace, type, new(textLeft, typeTop), room - tierWidth - padding, muted);
        if (tierName is not null)
            draw.AddText(typeFace.Font, typeFace.Size, new(right - padding - tierWidth, typeTop), muted, tierName);
        ImGui.Dummy(new(width, height));
    }

    /**
     * The icon's layers over the band in their rarity frame, a letter until they upload. Square, since a rounded
     * edge smooths into a dark line.
     */
    private static void DrawIcon(GpuTexture? icon, Vector4 tint, int bucket, Vector2 origin, float extent)
    {
        var draw = ImGui.GetWindowDrawList();
        var corner = origin + new Vector2(extent);
        if (icon is not null) InvestmentIconCache.DrawFitted(draw, icon, origin, corner);
        else
        {
            draw.AddRectFilled(origin, corner, ImGui.GetColorU32(new Vector4(tint.X * IconBackdrop, tint.Y * IconBackdrop,
                tint.Z * IconBackdrop, 1)));
            draw.AddText(origin + new Vector2(extent * .4f, extent * .35f), ImGui.GetColorU32(ImGuiCol.TextDisabled),
                bucket <= InvestmentItemKinds.LastWeaponBucket ? "W" : bucket <= InvestmentItemKinds.LastArmorBucket ? "A" : "+");
        }
        draw.AddRect(origin, corner, ImGui.GetColorU32(tint));
    }

    /** Package and tag on a shaded strip under the band, the tag at the far end. */
    private static void DrawSource(string package, uint tag, float width)
    {
        float padding = Padding * scale, height = ImGui.GetTextLineHeight() + padding;
        var (left, right) = Span();
        var at = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(new(left, at.Y), new(right, at.Y + height), ImGui.GetColorU32(IntrinsicFill));
        string hash = tag.ToString("X8", CultureInfo.InvariantCulture);
        float hashWidth = ImGui.CalcTextSize(hash).X, top = at.Y + padding * .5f;
        uint muted = ImGui.GetColorU32(MutedColor);
        draw.AddText(new(at.X + width - hashWidth, top), muted, hash);
        if (package.Length != 0)
            Clipped(new(ImGui.GetFont(), ImGui.GetFontSize()), package, new(at.X, top), width - hashWidth - padding, muted);
        ImGui.Dummy(new(width, height));
    }

    /** The figure tinted by damage type after its mark, a rule, then energy and the closing word. */
    private static void DrawLockup(Lockup lockup)
    {
        var lifted = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(lifted with { Y = lifted.Y - FigureTopTrim * scale });
        var face = FigureFace();
        string figure = lockup.Figure.ToString(CultureInfo.InvariantCulture);
        var extent = Measure(face, figure);
        float top = ImGui.GetCursorScreenPos().Y;
        var row = new Row(top + extent.Y * FigureMiddle, top + extent.Y * TextAnchor, extent.Y * FigureRowHeight);
        if (lockup.Damage > 0 && fonts is not null)
        {
            DrawMark(lockup.Damage, face.Size * MarkScale, row.Centre, row.Height);
            ImGui.SameLine(0, FigureMarkGap * scale);
        }
        Bold(face, figure, ImGui.GetCursorScreenPos() with { Y = top }, lockup.Damage > 0
            ? ImGui.GetColorU32(MarkTints[lockup.Damage]) : ImGui.GetColorU32(ImGuiCol.Text));
        ImGui.Dummy(new(extent.X + face.Weight, row.Height));
        ImGui.SameLine(0, FigureRuleGap * scale);
        float x = ImGui.GetCursorScreenPos().X, half = extent.Y * FigureRuleHeight * .5f;
        ImGui.GetWindowDrawList().AddLine(new(x, row.Centre - half), new(x, row.Centre + half),
            ImGui.GetColorU32(FigureRuleColor));
        ImGui.Dummy(new(1, row.Height));
        ImGui.SameLine(0, FigureRuleGap * scale);
        if (lockup.Energy > 0) DrawEnergy(lockup, row);
        DrawAmmunition(lockup, row);
    }

    /** Armor's energy: its type's mark, then its capacity in that type's tint, set as the figure drawn small. */
    private static void DrawEnergy(Lockup lockup, Row row)
    {
        float size = ImGui.GetFontSize() * EnergyScale;
        var face = fonts is { } game ? new Face(game.Figure, size) : new Face(ImGui.GetFont(), size, EnergyWeight * scale);
        string capacity = lockup.Energy.ToString(CultureInfo.InvariantCulture);
        var extent = Measure(face, capacity);
        float top = row.Anchor - extent.Y * TextAnchor;
        if (lockup.EnergyType > 0 && fonts is not null)
        {
            DrawMark(lockup.EnergyType, size * MarkScale, top + extent.Y * FigureMiddle, row.Height);
            ImGui.SameLine(0, 0);
        }
        Bold(face, capacity, ImGui.GetCursorScreenPos() with { Y = top }, lockup.EnergyType > 0
            ? ImGui.GetColorU32(MarkTints[lockup.EnergyType]) : ImGui.GetColorU32(ImGuiCol.Text));
        ImGui.Dummy(new(extent.X + face.Weight, row.Height));
        ImGui.SameLine(0, FigureRuleGap * scale);
    }

    /** A weapon's ammunition mark, which carries its own colour, then the word the row ends on. */
    private static void DrawAmmunition(Lockup lockup, Row row)
    {
        if (lockup.Ammo > 0 && source is { } investment && icons.Row(investment, AmmoMarks[lockup.Ammo]) is { } mark)
        {
            float size = AmmoMarkExtent * scale;
            var at = ImGui.GetCursorScreenPos() with { Y = row.Centre - size * (.5f - AmmoMarkDrop) };
            InvestmentIconCache.DrawFitted(ImGui.GetWindowDrawList(), mark, at, at + new Vector2(size));
            ImGui.Dummy(new(size, row.Height));
            ImGui.SameLine(0, AmmoWordGap * scale);
        }
        ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() with { Y = row.Anchor - ImGui.GetTextLineHeight() * TextAnchor });
        if (lockup.Ammo > 0) ImGui.TextUnformatted(lockup.Label);
        else Muted(lockup.Label);
    }

    /** A damage type's mark from the game's symbol face, its ink centred on a line. Needs the game's faces. */
    private static void DrawMark(int type, float size, float line, float height)
    {
        // The face baked nearest the mark's size.
        var face = new Face(size > ImGui.GetFontSize() * 1.15f ? fonts!.Type : fonts!.Text, size);
        string mark = Marks[type].ToString();
        var (inkTop, inkHeight) = Ink(face, Marks[type]);
        var at = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddText(face.Font, face.Size, new(at.X, line - inkTop - inkHeight * .5f),
            ImGui.GetColorU32(MarkTints[type]), mark);
        ImGui.Dummy(new(Measure(face, mark).X, height));
    }

    /** Right-aligned names, white bars on a dark track and the values; armor signs its stats and sums the bars. */
    private static void DrawStats(Content content, float width)
    {
        float padding = Padding * scale, nameWidth = ImGui.CalcTextSize(LongestStat).X, gap = StatColumnGap * scale;
        float barWidth = width - nameWidth - StatValueWidth * scale - gap * 2;
        float line = ImGui.GetTextLineHeight(), rowHeight = line + StatRowGap * scale, barHeight = StatBarHeight * scale;
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float barLeft = origin.X + nameWidth + gap, top = origin.Y + padding * StatBlockPadding * .5f;
        uint muted = ImGui.GetColorU32(MutedColor), text = ImGui.GetColorU32(ImGuiCol.Text);
        int drawn = content.Stats.Count - (content.Title < 0 ? 0 : 1) + (content.Armor ? 1 : 0), sum = 0;
        for (int i = 0; i < content.Stats.Count; i++)
        {
            if (i == content.Title) continue;
            var stat = content.Stats[i];
            draw.AddText(new(origin.X + nameWidth - ImGui.CalcTextSize(stat.Name).X, top), muted, stat.Name);
            if (!stat.Numeric)
            {
                float barTop = top + (line - barHeight) * .5f;
                float share = Math.Clamp((float)stat.Value / Math.Max(content.Ceiling, 1), 0, 1);
                var barStart = new Vector2(barLeft, barTop);
                draw.AddRectFilled(barStart, barStart + new Vector2(barWidth, barHeight), ImGui.GetColorU32(BarTrack));
                draw.AddRectFilled(barStart, barStart + new Vector2(barWidth * share, barHeight), ImGui.GetColorU32(BarFill));
                sum += stat.Value;
            }
            // Armor and plugs sign their stats; a number starts where a bar would.
            string value = ((content.Armor || content.Signed) && stat.Value > 0 ? "+" : "")
                + stat.Value.ToString(CultureInfo.InvariantCulture);
            draw.AddText(new(stat.Numeric ? barLeft : barLeft + barWidth + gap, top), text, value);
            top += rowHeight;
        }
        if (content.Armor)
        {
            draw.AddText(new(origin.X + nameWidth - ImGui.CalcTextSize(TotalLabel).X, top), muted, TotalLabel);
            draw.AddText(new(barLeft, top), text, sum.ToString(CultureInfo.InvariantCulture));
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new(width, rowHeight * drawn + padding * StatBlockPadding));
    }

    /** One default perk: its icon, then its name. The intrinsic sits on a panel. */
    private static void DrawPerkRow(Perk perk, GpuTexture? icon, float width)
    {
        float rowHeight = PerkRowHeight * scale, size = PerkIconExtent * scale, column = size + PerkIconPadding * 2 * scale;
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        if (perk.Intrinsic)
        {
            var (left, right) = Span();
            draw.AddRectFilled(new(left, at.Y), new(right, at.Y + rowHeight), ImGui.GetColorU32(IntrinsicFill));
        }
        var centre = new Vector2(at.X + column * .5f, at.Y + rowHeight * .5f);
        var half = new Vector2(size * .5f);
        if (icon is not null) InvestmentIconCache.DrawFitted(draw, icon, centre - half, centre + half);
        float textLeft = at.X + column + Padding * scale;
        Clipped(new(ImGui.GetFont(), ImGui.GetFontSize()), perk.Plug.Name,
            new(textLeft, at.Y + (rowHeight - ImGui.GetTextLineHeight()) * .5f), Math.Max(0, width - column - Padding * scale),
            ImGui.GetColorU32(ImGuiCol.Text));
        ImGui.Dummy(new(width, rowHeight));
    }

    private static Face NameFace() => fonts is { } game ? new(game.Name, game.Name.FontSize)
        : new(ImGui.GetFont(), ImGui.GetFontSize() * NameScale, NameWeight * scale);
    private static Face TypeFace() => fonts is { } game ? new(game.Type, game.Type.FontSize)
        : new(ImGui.GetFont(), ImGui.GetFontSize() * TypeScale);
    private static Face FigureFace() => fonts is { } game ? new(game.Figure, game.Figure.FontSize)
        : new(ImGui.GetFont(), ImGui.GetFontSize() * FigureScale, FigureWeight * scale);

    private static Face Scaled(Face face, float size) => face with { Size = face.Size * size, Weight = face.Weight * size };

    private static uint BandText(byte tier, bool muted)
    {
        var color = tier <= 1 || tier == 5 ? DarkBandText : LightBandText;
        if (muted) color.W = MutedBandText;
        return ImGui.GetColorU32(color);
    }

    /** The inside edges of the frame, so a rule or a panel runs its full width past the padding. */
    private static (float Left, float Right) Span()
    {
        float border = ImGui.GetStyle().PopupBorderSize, x = ImGui.GetWindowPos().X;
        return (x + border, x + ImGui.GetWindowSize().X - border);
    }

    private static void Rule()
    {
        var (left, right) = Span();
        float y = ImGui.GetCursorScreenPos().Y;
        ImGui.GetWindowDrawList().AddLine(new(left, y), new(right, y), ImGui.GetColorU32(RuleColor));
    }

    private static void Gap() => ImGui.Dummy(new(0, BlockGap * scale));

    private static void Muted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MutedColor);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    private static Vector2 Measure(Face face, string text) => face.Font.CalcTextSizeA(face.Size, float.MaxValue, 0, text);

    /** Where a character's ink sits in a line: its distance below the line's top, and its height. */
    private static unsafe (float Top, float Height) Ink(Face face, char code)
    {
        // ImGui.NET misreads the glyph's bit fields; the native glyph packs them in one word, then AdvanceX, X0, Y0, X1, Y1.
        var glyph = (float*)face.Font.FindGlyph(code).NativePtr;
        if (glyph == null) return (0, face.Size);
        float ratio = face.Size / face.Font.FontSize;
        return (glyph[3] * ratio, (glyph[5] - glyph[3]) * ratio);
    }

    /** Words fill each line to a width; a word wider than the line stays whole for the clip to cut. */
    private static List<string> Wrap(Face face, string text, float width)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length != 0 && Measure(face, next).X > width) { lines.Add(line); line = word; }
            else line = next;
        }
        lines.Add(line);
        return lines;
    }

    /** Text cut to a width with "..." added when it does not fit. */
    private static void Clipped(Face face, string text, Vector2 at, float width, uint color)
    {
        float limit = width - ClipSlack * scale;
        string shown = text;
        while (shown.Length != 0 && Measure(face, shown).X > limit)
            shown = shown[..(shown.Length > 1 && char.IsLowSurrogate(shown[^1]) ? ^2 : ^1)];
        Bold(face, shown.Length == text.Length ? text : shown + "...", at, color);
    }

    /** Text struck again in a ring around itself, so a regular face reads heavier without leaning one way. */
    private static void Bold(Face face, string text, Vector2 at, uint color)
    {
        var draw = ImGui.GetWindowDrawList();
        if (face.Weight <= 0) { draw.AddText(face.Font, face.Size, at, color, text); return; }
        float radius = face.Weight * .5f;
        var centre = at with { X = at.X + radius };
        int strikes = Math.Max(1, (int)(radius / BoldStrikeStep));
        for (int ring = 1; ring <= strikes; ring++)
        {
            float offset = radius * ring / strikes;
            foreach (var step in new Vector2[] { new(-offset, 0), new(offset, 0), new(0, -offset), new(0, offset) })
                draw.AddText(face.Font, face.Size, centre + step, color, text);
        }
        draw.AddText(face.Font, face.Size, centre, color, text);
    }
}
