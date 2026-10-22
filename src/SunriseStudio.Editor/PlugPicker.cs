using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/**
 * The row picker's window over plug and shader rows. Plug Scope widens the choice from the socket's own plugs to every
 * plug, each scope named for the socket and item, and rows read the plug's type and description under its name.
 */
public static class PlugPicker
{
    /** How widely the picker offers plugs; each scope holds the ones before it. */
    public enum PlugScope { Compatible, ItemSocket, Socket, ItemType, Kind, All }

    /** The plugs stock gear allows: by socket type, by socket type on one type line, by type line and by gear kind. */
    private sealed record GearUses(Dictionary<ushort, HashSet<ushort>> Socket,
        Dictionary<(ushort Socket, string TypeName), HashSet<ushort>> ItemSocket, Dictionary<string, HashSet<ushort>> ItemType,
        Dictionary<InvestmentCategory, HashSet<ushort>> Kind);

    private static (InvestmentCatalog? For, Task<GearUses>? Read) uses;
    private static (object? Key, Dictionary<int, uint> Scopes) scoped = (null, []);
    private static (object? Key, IReadOnlyList<BrowserAsset> Rows) plugRows = (null, []);

    /**
     * The picker for one socket of an item; ImGui.OpenPopup(id) opens it from the same ID scope.
     * @param present The socket's plugs, which the picker leaves out but for the one being replaced.
     * @param replacing The choice being replaced, which starts chosen, or -1 when adding one.
     * @param allowed The only plugs to offer, for a socket that takes nothing else; Plug Scope then hides. A socket that
     *     allows none, such as an empty slot with no type, offers every scope.
     * @return The chosen plug's item index, once.
     */
    public static ushort? Pick(StudioWorkspace workspace, InvestmentCatalog investment, string id, InvestmentItem owner,
        InvestmentSocket socket, string name, IReadOnlyList<ushort> present, int replacing,
        IReadOnlyCollection<ushort>? allowed = null)
    {
        SocketTiles.Prepare(workspace);
        if (allowed is { Count: 0 }) allowed = null;
        ushort? current = replacing >= 0 && replacing < present.Count ? present[replacing] : null;
        var scopes = allowed is null ? Scopes(workspace, investment, owner, socket) : [];
        InvestmentItem? Item(BrowserAsset row) => investment.FindItem(row.Entry.TagHash);
        bool Keep(BrowserAsset row) => Item(row) is { DisplayTag: not 0, Index: < InvestmentItemClone.MaximumIndexRows } item
            && (item.Index == current || !present.Contains((ushort)item.Index)) && allowed?.Contains((ushort)item.Index) != false;
        RowFacet[] facets = allowed is not null ? [] :
        [
            new("Plug Scope", [.. Enum.GetValues<PlugScope>().Select(scope =>
                new CreationChoice((uint)scope, Label(scope, owner, name), Hint(scope)))],
                row => Item(row) is { } item && scopes.TryGetValue(item.Index, out uint scope) ? scope : (uint)PlugScope.All,
                Default: Start(scopes), Widening: true)
        ];
        uint? tag = current is { } plug ? investment.Items[plug].Tag : null;
        string label = current is { } named ? investment.ItemName(named) : "";
        // Each row's second line is its type and the first line of its description.
        string Detail(BrowserAsset row) => Item(row) is { } item ? string.Join(" · ", new[] { item.TypeName.Trim(),
            AssetTooltip.PlugDescription(item).Split('\n', 2)[0].Trim() }.Where(part => part.Length != 0)) : row.Type;
        if (!CreationWidgets.RowPickerWindow(id, ref tag, ref label, Rows(workspace), Keep,
                HashCode.Combine(scopes, string.Join(",", present), allowed?.Count ?? -1, workspace.AssetRevision),
                workspace.IsProjectTag, row => investment.FindItem(row) is { } item ? ItemPreview.Shared?.Item(item) : null,
                facets, Detail)
            || tag is not { } chosen
            || investment.FindItem(chosen) is not { Index: < InvestmentItemClone.MaximumIndexRows } picked)
            return null;
        return checked((ushort)picked.Index);
    }

    /** The narrowest scope from Item Socket on that offers anything, so an empty slot starts on plugs its item uses. */
    private static uint Start(Dictionary<int, uint> scopes) =>
        scopes.Values.Where(scope => scope >= (uint)PlugScope.ItemSocket).DefaultIfEmpty((uint)PlugScope.All).Min();

    private static string Hint(PlugScope scope) => scope switch
    {
        PlugScope.Compatible => "Plugs this item allows in this socket",
        PlugScope.ItemSocket => "Plugs this socket takes on this kind of item",
        PlugScope.Socket => "Plugs this socket type takes on any item",
        PlugScope.ItemType => "Any plug this kind of item uses, in any socket",
        PlugScope.Kind => "Any plug every weapon or every armor piece uses",
        _ => "Every plug. Some can stop the item from loading"
    };

    /** Plug and shader rows together, kept while their category lists are. */
    private static IReadOnlyList<BrowserAsset> Rows(StudioWorkspace workspace)
    {
        var key = (workspace.CategoryRowsOf(InvestmentItemKinds.Mods), workspace.CategoryRowsOf(InvestmentItemKinds.Shaders));
        if (!Equals(plugRows.Key, key)) plugRows = (key, [.. key.Item1, .. key.Item2]);
        return plugRows.Rows;
    }

    /** The narrowest scope each plug falls in, by item index; a plug in none is only in All. */
    private static Dictionary<int, uint> Scopes(StudioWorkspace workspace, InvestmentCatalog investment, InvestmentItem owner,
        InvestmentSocket socket)
    {
        var sets = Uses(workspace, investment);
        var key = (investment, owner.Index, socket.Index, sets is null, SocketTiles.Adopt().Count);
        if (Equals(scoped.Key, key)) return scoped.Scopes;
        var scopes = new Dictionary<int, uint>();
        static IEnumerable<ushort> Used<T>(Dictionary<T, HashSet<ushort>>? table, T key) where T : notnull =>
            table?.GetValueOrDefault(key) ?? [];
        foreach (var (scope, plugs) in new (PlugScope, IEnumerable<ushort>)[]
        {
            (PlugScope.Compatible, socket.AllowedPlugs),
            (PlugScope.ItemSocket, Used(sets?.ItemSocket, (socket.Type, owner.TypeName))),
            (PlugScope.Socket, SocketTiles.Takers(workspace, investment, socket.Type).Concat(Used(sets?.Socket, socket.Type))),
            (PlugScope.ItemType, Used(sets?.ItemType, owner.TypeName)), (PlugScope.Kind, Used(sets?.Kind, owner.Category))
        })
            foreach (ushort plug in plugs) scopes.TryAdd(plug, (uint)scope);
        scoped = (key, scopes);
        return scopes;
    }

    /** Reads which plugs stock gear allows where, once per catalog on the thread pool; null until it finishes. */
    private static GearUses? Uses(StudioWorkspace workspace, InvestmentCatalog investment)
    {
        if (!ReferenceEquals(uses.For, investment) && workspace.Catalog is { } packages)
            uses = (investment, Task.Run(() =>
            {
                var sets = new GearUses([], [], [], []);
                static void Add<T>(Dictionary<T, HashSet<ushort>> table, T key, IEnumerable<ushort> plugs) where T : notnull =>
                    (table.TryGetValue(key, out var set) ? set : table[key] = []).UnionWith(plugs);
                foreach (var gear in investment.Items.Where(item => item.DisplayTag != 0
                    && item.Category is InvestmentCategory.Weapons or InvestmentCategory.Armor))
                    try
                    {
                        foreach (var socket in investment.ReadItem(gear.Tag, packages.Read(gear.Tag)).Sockets)
                        {
                            Add(sets.Socket, socket.Type, socket.AllowedPlugs);
                            Add(sets.ItemSocket, (socket.Type, gear.TypeName), socket.AllowedPlugs);
                            Add(sets.ItemType, gear.TypeName, socket.AllowedPlugs);
                            Add(sets.Kind, gear.Category, socket.AllowedPlugs);
                        }
                    }
                    catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException) { }
                return sets;
            }));
        return uses.Read is { IsCompletedSuccessfully: true } done ? done.Result : null;
    }

    /** A scope's name in the words of the socket and item, as for an Auto Rifle's Scope: Auto Rifle Scope, Any Scope. */
    private static string Label(PlugScope scope, InvestmentItem owner, string socket) => scope switch
    {
        PlugScope.Compatible => $"{owner.Name.Trim()} {socket}".Trim(),
        PlugScope.ItemSocket => $"{owner.TypeName.Trim()} {socket}".Trim(),
        PlugScope.Socket => $"Any {socket}",
        PlugScope.ItemType => $"Any {owner.TypeName.Trim()} Plug",
        PlugScope.Kind => owner.Category switch
        {
            InvestmentCategory.Weapons => "Any Weapon Plug", InvestmentCategory.Armor => "Any Armor Plug", _ => "Any Plug"
        },
        _ => "All"
    };
}
