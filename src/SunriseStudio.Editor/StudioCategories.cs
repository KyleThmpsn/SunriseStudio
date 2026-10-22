using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** One browser category with its own viewer; Name is the category part of a browser kind. */
public sealed record StudioCategory(
    string Name,
    string Label,
    string Group,
    StudioIcon Icon,
    string Tooltip,
    string[] Sections,
    bool ModelPane = false,
    Func<uint, AssetOpenContext?>? OpenContext = null);

/** Kind is "Name / Type" or "Name"; Tags join the search text; Tier >= 0 colours the row. */
public readonly record struct StudioCategoryMatch(string Kind, string Tags = "", int Tier = -1);

/** The browser categories with a viewer. Each hook below is implemented once, in its own category file. */
public static partial class StudioCategories
{
    public const string ItemsGroup = "Items", WorldGroup = "World", ContentGroup = "Content";
    public const string PreviewPageId = "sunrise.preview";
    // Pages that draw the model view themselves; the model pane hides while one is shown.
    public static readonly IReadOnlySet<string> OwnViewPages = new HashSet<string>(StringComparer.Ordinal) { "sunrise.scene" };
    private static readonly string[] Groups = [ItemsGroup, WorldGroup, ContentGroup];
    // Both are replaced whole, never changed in place, so worker threads read them without a lock.
    private static StudioCategory[] all = [];
    private static Dictionary<string, StudioCategory> byName = new(StringComparer.Ordinal);
    private static Func<PackageEntry, StudioCategoryMatch?>[] testRules = [];
    // Icon art from the game's own menus, drawn by the home view in place of a category's Icon. No installed table maps a
    // category to its art, so each is chosen by hand; one that does not read falls back to the vector Icon.
    internal static readonly IReadOnlyDictionary<string, uint> IconTags = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        [InvestmentItemKinds.Weapons] = 0x8132E4A0, [InvestmentItemKinds.Armor] = 0x80BA206F,
        [InvestmentItemKinds.Ghosts] = 0x81321605, [InvestmentItemKinds.Vehicles] = 0x813215FF,
        [InvestmentItemKinds.Ships] = 0x81321601, [InvestmentItemKinds.Subclasses] = 0x813277B1,
        [InvestmentItemKinds.Shaders] = 0x81321697, [InvestmentItemKinds.Emotes] = 0x813216A1,
        [InvestmentItemKinds.ClanBanners] = 0x80BC6090, [InvestmentItemKinds.Mods] = 0x80B46AC5,
        [InvestmentItemKinds.Engrams] = 0x80BC6093, [InvestmentItemKinds.Consumables] = 0x81326120,
        [InvestmentItemKinds.Quests] = 0x80BC659A, [VendorsName] = 0x8132086B, [PerksName] = 0x81611CEB,
        [GuardiansName] = 0x80B47ED7, [AudioName] = 0x81320819
    };

    /** Items, then World, then Content; registration order within a group. */
    public static IReadOnlyList<StudioCategory> All => all;

    static StudioCategories()
    {
        var categories = new List<StudioCategory>();
        AddItemCategories(categories);
        AddGuardianCategories(categories);
        AddEntityCategories(categories);
        AddContentCategories(categories);
        Publish(categories);
    }

    private static void Publish(IEnumerable<StudioCategory> categories)
    {
        var sorted = categories.OrderBy(category => Array.IndexOf(Groups, category.Group) is >= 0 and var at ? at : Groups.Length)
            .ToArray();
        var names = new Dictionary<string, StudioCategory>(StringComparer.Ordinal);
        foreach (var category in sorted)
            if (!names.TryAdd(category.Name, category))
                throw new InvalidOperationException($"Two browser categories are named {category.Name}.");
        byName = names;
        all = sorted;
    }

    public static StudioCategory? Find(string name) => byName.GetValueOrDefault(name);

    public static StudioCategory? ForKind(string kind) =>
        byName.Count == 0 ? null : Find(AssetCategories.SplitKind(kind).Category);

    /** Null keeps the row a normal asset. Worker-safe: cached lookups only, no package reads. */
    public static StudioCategoryMatch? Classify(PackageEntry entry, StudioNameIndex names)
    {
        if (byName.Count == 0) return null;
        StudioCategoryMatch? match = null;
        ClassifyItem(entry, names, ref match);
        if (match is null) ClassifyGuardian(entry, names, ref match);
        if (match is null) ClassifyEntity(entry, names, ref match);
        if (match is null) ClassifyContent(entry, names, ref match);
        foreach (var rule in testRules) match ??= rule(entry);
        return match is { } found && ForKind(found.Kind) is not null ? found : null;
    }

    /** Adds a category with its rule until disposed. Tests only; call it while no worker runs. */
    internal static IDisposable AddForTest(StudioCategory category, Func<PackageEntry, StudioCategoryMatch?> classify)
    {
        Publish([.. all, category]);
        testRules = [.. testRules, classify];
        return new Removal(() =>
        {
            Publish(all.Where(item => !ReferenceEquals(item, category)));
            testRules = [.. testRules.Where(rule => rule != classify)];
        });
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? pending = remove;
        public void Dispose() { pending?.Invoke(); pending = null; }
    }

    public static AssetOpenContext? DefaultContext(string kind, uint tag) => ForKind(kind)?.OpenContext?.Invoke(tag);

    /** Sort key in a category list: rows its viewer shows first, then rows with no real name. Worker only. */
    internal static int RowRank(BrowserAsset row, StudioNameIndex names) =>
        (ListOnlyKinds.Contains(row.Kind) ? 2 : 0)
        + (names.Lookup(row.Entry) is { Confidence: StudioNameConfidence.Fallback } ? 1 : 0);

    /**
     * Tabs of a category document: its Sections in order, then every other page that opens it.
     * @param paneShown True when the model pane draws the preview, so it is not a tab.
     */
    public static IReadOnlyList<IStudioPage> Tabs(StudioCategory category, IEnumerable<IStudioPage> pages,
        StudioDocument document, bool paneShown) =>
        [.. pages.Where(page => page.CanOpen(document) && !(paneShown && page.Id == PreviewPageId))
            .OrderBy(page => Array.IndexOf(category.Sections, page.Id) is >= 0 and var at ? at : int.MaxValue)];

    static partial void AddItemCategories(List<StudioCategory> categories);
    static partial void AddGuardianCategories(List<StudioCategory> categories);
    static partial void AddEntityCategories(List<StudioCategory> categories);
    static partial void AddContentCategories(List<StudioCategory> categories);
    static partial void ClassifyItem(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match);
    static partial void ClassifyGuardian(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match);
    static partial void ClassifyEntity(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match);
    static partial void ClassifyContent(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match);
}
