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
    // A texel at or below this alpha, out of 255, belongs to the transparent margin a small icon leaves out; block
    // compression leaves faint texels there.
    private const byte MarginAlpha = 8;
    // The part of the layers Draw shows, in texture coordinates.
    private (Vector2 Min, Vector2 Max) visible = (Vector2.Zero, Vector2.One);

    /**
     * An icon's layers made ready on a worker for drawing far below their size: each layer's half-size levels down to
     * one texel, so it stays smooth, and the box its pixels fill, so the transparent margin is left out.
     */
    public sealed record Small(IReadOnlyList<(int Width, int Height, IReadOnlyList<byte[]> Levels)> Layers,
        Vector2 Min, Vector2 Max);

    /** The icon's small form; null when a layer does not decode, so the icon uploads as it is. Runs on a worker. */
    public static Small? Shrink(InvestmentIcon icon)
    {
        var images = new List<RgbaImage>();
        foreach (var layer in icon.Layers)
        {
            try { images.Add(TexturePixels.Decode(layer)); }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException or ArgumentException)
            { return null; }
        }
        if (images.Count == 0) return null;
        var layers = images.Select(image =>
        {
            var levels = new List<byte[]> { image.Pixels };
            for (int w = image.Width, h = image.Height; w > 1 || h > 1; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
                levels.Add(StudioBrand.HalveRgba(levels[^1], w, h));
            return (image.Width, image.Height, (IReadOnlyList<byte[]>)levels);
        }).ToArray();
        Vector2 min = Vector2.One, max = Vector2.Zero;
        foreach (var image in images)
        {
            var texel = new Vector2(1f / image.Width, 1f / image.Height);
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    if (image.Pixels[(y * image.Width + x) * 4 + 3] > MarginAlpha)
                    {
                        min = Vector2.Min(min, new Vector2(x, y) * texel);
                        max = Vector2.Max(max, new Vector2(x + 1, y + 1) * texel);
                    }
        }
        return max.X > min.X ? new(layers, min, max) : new(layers, Vector2.Zero, Vector2.One);
    }

    /**
     * Uploads every layer, from its levels when the icon has a small form; a layer that fails releases the ones before
     * it. Runs between frames.
     */
    public static ItemIcon Upload(Dx11Renderer renderer, InvestmentIcon icon, Small? small = null)
    {
        var uploaded = new List<GpuTexture>();
        try
        {
            if (small is null)
                foreach (var layer in icon.Layers) uploaded.Add(AssetViewport.CreatePreviewTexture(renderer, layer));
            else
                foreach (var (width, height, levels) in small.Layers)
                    uploaded.Add(renderer.CreateMippedTexture(width, height, levels));
        }
        catch { foreach (var texture in uploaded) texture.Dispose(); throw; }
        return small is null ? new(uploaded) : new(uploaded) { visible = (small.Min, small.Max) };
    }

    /** The visible layers centred in the box at their own shape, so a bitmap that is not square keeps its proportions. */
    public void Draw(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        if (layers.Count == 0) return;
        var box = max - min;
        var shown = (visible.Max - visible.Min) * new Vector2(layers[0].Width, layers[0].Height);
        float aspect = shown.X / Math.Max(1, shown.Y);
        var size = box.X > box.Y * aspect ? new Vector2(box.Y * aspect, box.Y) : new Vector2(box.X, box.X / aspect);
        var at = min + (box - size) * .5f;
        foreach (var layer in layers) draw.AddImage(layer.ImGuiId, at, at + size, visible.Min, visible.Max);
    }

    public void Dispose()
    {
        foreach (var layer in layers) layer.Dispose();
    }
}

/** Item, perk and icon asset icons, read on the thread pool and uploaded between frames; the browser and tooltips draw them. */
public sealed class ItemPreview : IDisposable
{
    /** The window's previews; null until it has a renderer. */
    public static ItemPreview? Shared { get; set; }

    private readonly Dx11Renderer renderer;
    /** What an icon key's index counts: items, sandbox perks, rows of the icon table or icon asset tags. */
    private enum Source { Item, Perk, Row, Asset }
    private readonly Dictionary<(Source Source, int Index), ItemIcon?> icons = [];
    // A read finishes under the catalog generation it started in; one from an older catalog is dropped.
    private readonly ConcurrentDictionary<(Source Source, int Index, int Generation), (InvestmentIcon? Icon, ItemIcon.Small? Small)>
        read = [];
    private int generation;
    private readonly HashSet<(Source Source, int Index)> requested = [];
    // Icons kept uploaded; the least recently drawn beyond this are released and read again when next drawn.
    private const int CachedIcons = 512;
    private readonly Dictionary<(Source Source, int Index), long> drawn = [];
    private long frame;
    private InvestmentCatalog? investment;

    public ItemPreview(Dx11Renderer renderer) => this.renderer = renderer;

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
        foreach (var entry in read.Keys.ToArray())
        {
            if (!read.TryRemove(entry, out var image) || entry.Generation != generation) continue;
            var key = (entry.Source, entry.Index);
            try { icons[key] = image.Icon is null ? null : ItemIcon.Upload(renderer, image.Icon, image.Small); }
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

    /** An icon container or icon bitmap asset, smoothed and trimmed for small sizes; null while it reads or for another asset. */
    public ItemIcon? Asset(uint tag) => Icon((Source.Asset, unchecked((int)tag)), catalog => catalog.ReadIconAsset(tag));

    private ItemIcon? Icon((Source Source, int Index) key, Func<InvestmentCatalog, InvestmentIcon?> load)
    {
        drawn[key] = frame;
        if (icons.TryGetValue(key, out var icon)) return icon;
        if (investment is not { } catalog || !requested.Add(key)) return null;
        int owner = generation;
        Task.Run(() =>
        {
            InvestmentIcon? image = null;
            try { image = load(catalog); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
                or KeyNotFoundException or OverflowException) { }
            // Icon assets are drawn far below their size, in category rows and the browser.
            read[(key.Source, key.Index, owner)] =
                (image, image is not null && key.Source == Source.Asset ? ItemIcon.Shrink(image) : null);
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

    private void DisposeIcons()
    {
        foreach (var icon in icons.Values) icon?.Dispose();
        icons.Clear(); requested.Clear(); read.Clear(); drawn.Clear();
        generation++;
    }

    public void Dispose() => DisposeIcons();
}
