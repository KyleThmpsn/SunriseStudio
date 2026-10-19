using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** An inventory icon as the game draws it: its layers stacked over one square, the rarity plate first. */
public sealed class ItemIcon(IReadOnlyList<GpuTexture> layers) : IDisposable
{
    /** Uploads every layer; a layer that fails releases the ones before it. Runs between frames. */
    public static ItemIcon Upload(Dx11Renderer renderer, InvestmentIcon icon)
    {
        var uploaded = new List<GpuTexture>();
        try { foreach (var layer in icon.Layers) uploaded.Add(AssetViewport.CreatePreviewTexture(renderer, layer)); }
        catch { foreach (var texture in uploaded) texture.Dispose(); throw; }
        return new(uploaded);
    }

    /** The layers centred in the box at the icon's own shape, so a bitmap that is not square keeps its proportions. */
    public void Draw(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        if (layers.Count == 0) return;
        var box = max - min;
        float aspect = layers[0].Width / (float)Math.Max(1, layers[0].Height);
        var size = box.X > box.Y * aspect ? new Vector2(box.Y * aspect, box.Y) : new Vector2(box.X, box.X / aspect);
        var at = min + (box - size) * .5f;
        foreach (var layer in layers) draw.AddImage(layer.ImGuiId, at, at + size);
    }

    public void Dispose()
    {
        foreach (var layer in layers) layer.Dispose();
    }
}

/**
 * Item and perk icons, read on the thread pool and uploaded between frames, and the open weapon's model in a
 * preview viewport of its own; the weapon workflow and the Investment page draw them.
 */
public sealed class ItemPreview : IDisposable
{
    /** The window's previews; null until it has a renderer. */
    public static ItemPreview? Shared { get; set; }

    private readonly Dx11Renderer renderer;
    private readonly AssetViewport preview;
    /** What an icon key's index counts: items, sandbox perks, rows of the icon table or icon asset tags. */
    private enum Source { Item, Perk, Row, Asset }
    private readonly Dictionary<(Source Source, int Index), ItemIcon?> icons = [];
    private readonly ConcurrentDictionary<(Source Source, int Index), InvestmentIcon?> read = [];
    private readonly HashSet<(Source Source, int Index)> requested = [];
    // Icons kept uploaded; the least recently drawn beyond this are released and read again when next drawn.
    private const int CachedIcons = 512;
    private readonly Dictionary<(Source Source, int Index), long> drawn = [];
    private long frame;
    private InvestmentCatalog? investment;
    private SceneDocument? scene;
    private double seconds;

    public ItemPreview(Dx11Renderer renderer)
    {
        this.renderer = renderer;
        preview = new AssetViewport(renderer) { ShowCursor = false, ShowGrid = false };
    }

    /**
     * Uploads the icons read since the last frame and releases the least recently drawn past the cache size; a new
     * investment catalog drops every icon. Runs between frames.
     */
    public void Prepare(InvestmentCatalog? catalog)
    {
        frame++;
        if (!ReferenceEquals(catalog, investment))
        {
            DisposeIcons();
            investment = catalog;
        }
        foreach (var key in read.Keys.ToArray())
        {
            if (!read.TryRemove(key, out var image)) continue;
            try { icons[key] = image is null ? null : ItemIcon.Upload(renderer, image); }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
            { icons[key] = null; }
        }
        if (icons.Count <= CachedIcons) return;
        var stale = icons.Keys.OrderBy(icon => drawn.GetValueOrDefault(icon)).Take(icons.Count - CachedIcons);
        foreach (var key in stale.ToList())
        {
            icons[key]?.Dispose();
            icons.Remove(key); requested.Remove(key); drawn.Remove(key);
        }
    }

    /** The item's inventory icon, or null while it reads or when it has none. */
    public ItemIcon? Item(InvestmentItem item) => Icon((Source.Item, item.Index), catalog => catalog.ReadIcon(item));

    /** True once the item's icon has read and it has none, so a layout can leave its square out. */
    public bool NoIcon(InvestmentItem item) => icons.TryGetValue((Source.Item, item.Index), out var icon) && icon is null;

    /** True once the sandbox perk's icon has read and it has none. */
    public bool NoPerkIcon(int perk) => icons.TryGetValue((Source.Perk, perk), out var icon) && icon is null;

    /** The sandbox perk's icon, or null while it reads or when it has none. */
    public ItemIcon? Perk(int perk) => Icon((Source.Perk, perk), catalog => catalog.ReadPerkIcon(perk));

    /** An icon table row no item names, such as an ammunition mark; null while it reads or when it is empty. */
    public ItemIcon? Row(int row) => Icon((Source.Row, row), catalog => catalog.ReadIcon(row));

    /** An icon container or icon bitmap asset; null while it reads or for another asset. */
    public ItemIcon? Asset(uint tag) => Icon((Source.Asset, unchecked((int)tag)), catalog => catalog.ReadIconAsset(tag));

    private ItemIcon? Icon((Source Source, int Index) key, Func<InvestmentCatalog, InvestmentIcon?> load)
    {
        drawn[key] = frame;
        if (icons.TryGetValue(key, out var icon)) return icon;
        if (investment is not { } catalog || !requested.Add(key)) return null;
        Task.Run(() =>
        {
            InvestmentIcon? image = null;
            try { image = load(catalog); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
                or KeyNotFoundException or OverflowException) { }
            read[key] = image;
        });
        return null;
    }

    /** Draws an icon at a square size, or reserves the square while it reads. */
    public static void Draw(ItemIcon? icon, float size)
    {
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new(size, size));
        icon?.Draw(ImGui.GetWindowDrawList(), at, at + new Vector2(size));
    }

    // The card's icon before scaling.
    private const float CardIcon = 52;

    /**
     * An item as a card across the width: its icon, its name with its hash, its type line, and an action at the
     * bottom right. A right click copies the hash.
     * @return True when the action was clicked.
     */
    public bool Card(InvestmentItem item, string action, float scale)
    {
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X, icon = CardIcon * scale, height = icon + 2 * scale;
        // A step lighter than the panel, so the card reads as one thing.
        var panel = style.Colors[(int)ImGuiCol.WindowBg];
        draw.AddRectFilled(at, at + new Vector2(width, height),
            ImGui.GetColorU32(new Vector4(panel.X + 14 / 255f, panel.Y + 14 / 255f, panel.Z + 14 / 255f, 1)));
        Item(item)?.Draw(draw, at + new Vector2(0, scale), at + new Vector2(icon, icon + scale));
        float left = icon + style.ItemSpacing.X, title = ImGui.GetFontSize() * 1.1f;
        string hash = item.Hash.ToString("X8", CultureInfo.InvariantCulture);
        float hashWidth = ImGui.CalcTextSize(hash).X, right = width - style.FramePadding.X;
        draw.AddText(at + new Vector2(right - hashWidth, 4 * scale), ImGui.GetColorU32(ImGuiCol.TextDisabled), hash);
        draw.PushClipRect(at, at + new Vector2(right - hashWidth - style.ItemSpacing.X, height), true);
        draw.AddText(ImGui.GetFont(), title, at + new Vector2(left, 4 * scale), ImGui.GetColorU32(ImGuiCol.Text), item.Name);
        draw.PopClipRect();
        draw.AddText(at + new Vector2(left, 4 * scale + title + 2 * scale), ImGui.GetColorU32(ImGuiCol.Text), item.TypeName);
        float button = ImGui.CalcTextSize(action).X + style.FramePadding.X * 2;
        ImGui.InvisibleButton("##card", new(Math.Max(1, width - button - style.ItemSpacing.X), height));
        if (ImGui.BeginPopupContextItem("##card-menu"))
        {
            if (ImGui.MenuItem("Copy Hash")) ImGui.SetClipboardText(hash);
            ImGui.EndPopup();
        }
        ImGui.SetCursorScreenPos(at + new Vector2(width - button, height - ImGui.GetFrameHeight() - scale));
        bool clicked = ImGui.Button(action);
        ImGui.SetCursorScreenPos(at + new Vector2(0, height + style.ItemSpacing.Y));
        ImGui.Dummy(new(width, 0));
        return clicked;
    }

    /**
     * An icon and a name inside a frame-high control at a screen position, as a plug button shows its plug.
     * @param draw The list of the window that holds the control, which a combo's open popup does not change.
     */
    public static void Label(ImDrawListPtr draw, Vector2 at, float width, ItemIcon? icon, string text)
    {
        var style = ImGui.GetStyle();
        float frame = ImGui.GetFrameHeight(), inset = 2 * frame / 24, size = frame - inset * 2;
        icon?.Draw(draw, at + new Vector2(inset), at + new Vector2(inset + size));
        float left = inset + size + style.FramePadding.X;
        draw.PushClipRect(at, at + new Vector2(width, frame), true);
        draw.AddText(at + new Vector2(left, style.FramePadding.Y), ImGui.GetColorU32(ImGuiCol.Text), text);
        draw.PopClipRect();
    }

    /**
     * The open weapon's model, turning on drag and zooming on the wheel; false when the open document has no
     * model to show, so the caller can stand its icon in.
     */
    public bool DrawModel(StudioWorkspace workspace, Vector2 size)
    {
        if (workspace.ActiveScene is not { } shown || size.X < 1 || size.Y < 1) return false;
        if (!ReferenceEquals(shown, scene))
        {
            preview.Clear();
            preview.Catalog = workspace.Catalog;
            preview.SetScene(shown);
            preview.Frame();
            scene = shown;
        }
        seconds += ImGui.GetIO().DeltaTime;
        var image = preview.RenderImage(Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y), seconds);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##model-preview", size);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0))
            preview.Camera.Orbit(ImGui.GetIO().MouseDelta.X, ImGui.GetIO().MouseDelta.Y);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0) preview.Camera.Zoom(ImGui.GetIO().MouseWheel);
        var draw = ImGui.GetWindowDrawList();
        float rounding = ImGui.GetStyle().FrameRounding;
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(.055f, .065f, .08f, 1)), rounding);
        draw.AddImageRounded(image.ImGuiId, origin, origin + size, Vector2.Zero, Vector2.One, uint.MaxValue, rounding);
        return true;
    }

    private void DisposeIcons()
    {
        foreach (var icon in icons.Values) icon?.Dispose();
        icons.Clear(); requested.Clear(); read.Clear(); drawn.Clear();
    }

    public void Dispose()
    {
        DisposeIcons();
        preview.Dispose();
    }
}
