using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/**
 * An icon's artwork, the bitmap authoring replaces, and every layer in the order the game draws them: the rarity
 * plate, the artwork, the season watermark and the foreground.
 */
public sealed record InvestmentIcon(uint ContainerTag, uint BitmapTag, TextureData Texture,
    IReadOnlyList<TextureData> Layers);

public sealed partial class InvestmentCatalog
{
    // Icon container layers in draw order.
    private const int ArtworkOffset = 20, PlateOffset = 28, WatermarkOffset = 32, ForegroundOffset = 36;
    private const uint BitmapClass = 0x80804A69, ContainerClass = 0x80804A53;
    private static readonly int[] LayerOffsets = [PlateOffset, ArtworkOffset, WatermarkOffset, ForegroundOffset];

    /** Display icon indices select shared rows; their hashes need not match the item hash. */
    public InvestmentIcon? ReadIcon(InvestmentItem item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (item.DisplayTag == 0 || !catalog.ContainsTag(sources.Icons)) return null;
        RequireClass(catalog, item.DisplayTag, DisplayClass);
        var display = new TagReader(catalog.Read(item.DisplayTag));
        if (!display.Has(128, 2)) throw new InvalidDataException("Item icon index is truncated.");
        ushort index = display.U16(128);
        return index == ushort.MaxValue ? null : ReadIcon(index, Watermark(item), token);
    }

    public InvestmentIcon? ReadPerkIcon(int perk, CancellationToken token = default) =>
        perk >= 0 && perk < PerkDefinitions.Count && PerkDefinitions[perk].IconIndex != ushort.MaxValue
        && catalog.ContainsTag(sources.Icons) ? ReadIcon(PerkDefinitions[perk].IconIndex, token) : null;

    public InvestmentIcon? ReadIcon(int index, CancellationToken token = default) => ReadIcon(index, 0, token);

    /** True for the classes ReadIconAsset draws: icon containers and icon bitmaps. */
    public static bool IsIconAsset(uint reference) => reference is ContainerClass or BitmapClass;

    /** An icon container's layers, or an icon bitmap as one layer; null for another asset or a container with no artwork. */
    public InvestmentIcon? ReadIconAsset(uint tag, CancellationToken token = default)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || !IsIconAsset(entry.Reference)) return null;
        if (entry.Reference == ContainerClass) return ReadContainer(tag, 0, token);
        var texture = new TextureDataReader(catalog).Read(CuiBitmap.ReadTexture(catalog.Read(tag)));
        return new(0, tag, texture, [texture]);
    }

    /** The season watermark container the item art lists after the icon's, or 0 when it lists none. */
    private uint Watermark(InvestmentItem item)
    {
        if (!catalog.ContainsTag(sources.ItemArt)) return 0;
        try
        {
            return InvestmentItemArt.ReadIconContainers(catalog.Read(sources.ItemArt), item.Index) is [_, var mark, ..]
                ? mark : 0;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return 0; }
    }

    /** Icon rows are shared by items, perks and marks no item names; a row names a container. */
    private InvestmentIcon? ReadIcon(int index, uint watermark, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireClass(catalog, sources.Icons, 0x80802951);
        var icons = new TagReader(catalog.Read(sources.Icons));
        var rows = icons.Array(8, stride: 24);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException("Icon index is outside its table.");
        return ReadContainer(icons.U32(rows.Data + index * 24 + 16), watermark, token);
    }

    /**
     * A container's layers each name a bitmap. No artwork means no icon; another layer whose bitmap is missing or of
     * another class is left out. An empty watermark slot takes the artwork of the item's watermark container.
     */
    private InvestmentIcon? ReadContainer(uint containerTag, uint watermark, CancellationToken token)
    {
        RequireClass(catalog, containerTag, ContainerClass);
        var container = new TagReader(catalog.Read(containerTag));
        if (!container.Has(0, 128)) throw new InvalidDataException("Icon container is truncated.");
        uint bitmapTag = container.U32(ArtworkOffset);
        if (bitmapTag is 0 or uint.MaxValue) return null;
        RequireClass(catalog, bitmapTag, BitmapClass);
        var textures = new TextureDataReader(catalog);
        TextureData Layer(uint tag) => textures.Read(CuiBitmap.ReadTexture(catalog.Read(tag)));
        var artwork = Layer(bitmapTag);
        var layers = new List<TextureData>();
        foreach (int offset in LayerOffsets)
        {
            token.ThrowIfCancellationRequested();
            uint tag = container.U32(offset);
            if (offset == WatermarkOffset && (tag is 0 or uint.MaxValue) && catalog.TryGetEntry(watermark, out var mark)
                && mark.Reference == ContainerClass)
                tag = new TagReader(catalog.Read(watermark)).U32(ArtworkOffset);
            if (offset == ArtworkOffset) layers.Add(artwork);
            else if (tag is not (0 or uint.MaxValue) && catalog.TryGetEntry(tag, out var entry) && entry.Reference == BitmapClass)
                layers.Add(Layer(tag));
        }
        return new(containerTag, bitmapTag, artwork, layers);
    }
}
