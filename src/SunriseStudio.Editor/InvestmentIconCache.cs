using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/**
 * Item, perk and icon asset icons for grids, lists and tooltips, read on a worker as they are drawn and uploaded a few per
 * frame (see IconStream). An icon's layers are stacked into one texture as the game draws them: the rarity plate, the
 * artwork, the season watermark and the foreground. StudioForm owns the one cache and prepares it with the last catalog
 * read. An icon that cannot be read shows as the empty frame. The item page reports its own icon errors.
 */
public sealed class InvestmentIconCache : IDisposable
{
    /** A cache that is never prepared, for a page drawn without the window's cache. It draws the empty frame for every item. */
    internal static readonly InvestmentIconCache Unprepared = new();

    /** The page context's cache, or Unprepared when the page is drawn without one. */
    internal static InvestmentIconCache Of(StudioPageContext context) => context.Icons ?? Unprepared;

    // A texel at or below this alpha, out of 255, is part of the transparent margin an icon asset's trim leaves out; block
    // compression leaves faint texels there.
    private const byte MarginAlpha = 8;
    // Items by index, sandbox perks by index, icon table rows no item names, and icon assets by tag.
    private readonly IconStream<IconImage, GpuTexture> items = new(), perks = new(), rows = new(), assets = new();
    // Icon asset tags by the stream key each was given. A stream reads only keys from 0 up, and most tags are negative as an int.
    private readonly List<uint> assetTags = [];
    private readonly Dictionary<uint, int> assetKeys = [];
    private InvestmentCatalog? catalog;

    /** An icon's stacked layers with their half-size levels, made on the worker. */
    private sealed record IconImage(int Width, int Height, IReadOnlyList<byte[]> Levels);

    /** Requests the item's icon, unless the item belongs to another catalog. @return Its texture once uploaded, else null. */
    public GpuTexture? Get(InvestmentCatalog source, int item) =>
        ReferenceEquals(source, catalog) && item >= 0 && item < source.Items.Count ? items.Get(item) : null;

    /** Requests the sandbox perk's icon. @return Its texture once uploaded, else null. */
    public GpuTexture? Perk(InvestmentCatalog source, int perk) =>
        ReferenceEquals(source, catalog) && perk >= 0 && perk < source.PerkCount ? perks.Get(perk) : null;

    /** Requests an icon table row no item names, such as an ammunition mark. @return Its texture once uploaded, else null. */
    public GpuTexture? Row(InvestmentCatalog source, int row) =>
        ReferenceEquals(source, catalog) && row >= 0 ? rows.Get(row) : null;

    /**
     * Requests an icon container or icon bitmap asset, trimmed to the part its pixels fill, since it is drawn far below its
     * size. @return Its texture once uploaded, else null.
     */
    public GpuTexture? Asset(InvestmentCatalog source, uint tag)
    {
        if (!ReferenceEquals(source, catalog)) return null;
        int key;
        lock (assetTags)
            if (!assetKeys.TryGetValue(tag, out key)) { assetKeys[tag] = key = assetTags.Count; assetTags.Add(tag); }
        return assets.Get(key);
    }

    /** True once the item's icon has read and it has none, so a layout can leave its square out. */
    public bool NoIcon(InvestmentCatalog source, int item) => ReferenceEquals(source, catalog) && items.Missing(item);

    /** True once the sandbox perk's icon has read and it has none. */
    public bool NoPerkIcon(InvestmentCatalog source, int perk) => ReferenceEquals(source, catalog) && perks.Missing(perk);

    /**
     * Draws the item's icon as a square ImGui item of side pixels. A frame stands in while it loads or when it has none, and an
     * index that names no item, such as -1, leaves the square empty.
     */
    public void Draw(InvestmentCatalog source, int item, float side)
    {
        Draw(ImGui.GetWindowDrawList(), source, item, ImGui.GetCursorScreenPos(), side);
        ImGui.Dummy(new(side));
    }

    /** Draws the item's icon, or the frame, at start in the given draw list without adding an ImGui item. */
    public void Draw(ImDrawListPtr draw, InvestmentCatalog source, int item, Vector2 start, float side)
    {
        if (item < 0 || item >= source.Items.Count) return;
        var end = start + new Vector2(side);
        if (Get(source, item) is { } texture) draw.AddImage(texture.ImGuiId, start, end);
        else draw.AddRectFilled(start, end, ImGui.GetColorU32(ImGuiCol.FrameBg));
    }

    /** Draws a texture centred in the box at its own shape, so an icon that is not square keeps its proportions. */
    public static void DrawFitted(ImDrawListPtr draw, GpuTexture texture, Vector2 min, Vector2 max)
    {
        var box = max - min;
        float aspect = (float)texture.Width / Math.Max(1, texture.Height);
        var size = box.X > box.Y * aspect ? new Vector2(box.Y * aspect, box.Y) : new Vector2(box.X, box.X / aspect);
        var at = min + (box - size) * .5f;
        draw.AddImage(texture.ImGuiId, at, at + size);
    }

    /** Runs between frames. A new catalog restarts the streams, then what the worker read uploads within the per-frame budget. */
    public void Prepare(InvestmentCatalog? current, Dx11Renderer renderer)
    {
        if (!ReferenceEquals(current, catalog))
        {
            catalog = current;
            items.Reset(current is null ? null : () =>
            {
                var reader = new InvestmentIconReader(current);
                return (item, token) => Stack(reader.Read(current.Items[item], token));
            });
            perks.Reset(current is null ? null : () => (perk, token) => Stack(current.ReadPerkIcon(perk, token)));
            rows.Reset(current is null ? null : () => (row, token) => Stack(current.ReadIcon(row, token)));
            lock (assetTags) { assetTags.Clear(); assetKeys.Clear(); }
            // The frame adds tags while the worker reads, so each read looks its tag up under the lock.
            assets.Reset(current is null ? null : () => (key, token) =>
            {
                uint tag;
                lock (assetTags) tag = assetTags[key];
                return Stack(current.ReadIconAsset(tag, token), trim: true);
            });
        }
        GpuTexture? Upload(IconImage image)
        {
            try { return renderer.CreateMippedTexture(image.Width, image.Height, image.Levels); }
            catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        }
        items.Prepare(Upload); perks.Prepare(Upload); rows.Prepare(Upload); assets.Prepare(Upload);
    }

    /**
     * Stacks the icon's layers over its first layer's square, then makes its half-size levels down to one texel so it stays
     * smooth far below its size. A layer of another size is sampled to fit. Trim leaves out the transparent margin.
     * Null for no icon. Artwork that does not decode throws, which the stream counts as no icon; another layer that does not
     * decode is left out.
     */
    private static IconImage? Stack(InvestmentIcon? icon, bool trim = false)
    {
        if (icon is null || icon.Layers.Count == 0) return null;
        var layers = new List<RgbaImage>();
        foreach (var layer in icon.Layers)
            try { layers.Add(TexturePixels.Decode(layer)); }
            catch (Exception error) when (!ReferenceEquals(layer, icon.Texture)
                && error is NotSupportedException or InvalidDataException or ArgumentException) { }
        if (layers.Count == 0) return null;
        int width = layers[0].Width, height = layers[0].Height;
        var pixels = new byte[width * height * 4];
        foreach (var layer in layers)
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int from = (y * layer.Height / height * layer.Width + x * layer.Width / width) * 4, to = (y * width + x) * 4;
                    Over(layer.Pixels.AsSpan(from, 4), pixels.AsSpan(to, 4));
                }
        if (trim) (pixels, width, height) = Trim(pixels, width, height);
        var levels = new List<byte[]> { pixels };
        for (int w = width, h = height; w > 1 || h > 1; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            levels.Add(StudioBrand.HalveRgba(levels[^1], w, h));
        return new(width, height, levels);
    }

    /** Draws one straight-alpha texel over another, as the game's layers blend. */
    private static void Over(ReadOnlySpan<byte> source, Span<byte> target)
    {
        float a = source[3] / 255f, below = target[3] / 255f * (1 - a), alpha = a + below;
        if (alpha <= 0) return;
        for (int channel = 0; channel < 3; channel++)
            target[channel] = (byte)MathF.Round((source[channel] * a + target[channel] * below) / alpha);
        target[3] = (byte)MathF.Round(alpha * 255);
    }

    /** The box the visible texels fill, or the whole image when none is visible. */
    private static (byte[] Pixels, int Width, int Height) Trim(byte[] pixels, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] > MarginAlpha)
                { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < 0) return (pixels, width, height);
        int w = right - left + 1, h = bottom - top + 1;
        var trimmed = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            pixels.AsSpan(((top + y) * width + left) * 4, w * 4).CopyTo(trimmed.AsSpan(y * w * 4));
        return (trimmed, w, h);
    }

    public void Dispose()
    {
        items.Dispose(); perks.Dispose(); rows.Dispose(); assets.Dispose();
        catalog = null;
    }
}
