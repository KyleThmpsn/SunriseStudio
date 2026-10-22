using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/**
 * An icon's artwork, the bitmap authoring replaces, and every layer in the order the game draws them: the rarity
 * plate, the artwork, the season watermark and the foreground.
 */
public sealed record InvestmentIcon(uint ContainerTag, uint BitmapTag, TextureData Texture,
    IReadOnlyList<TextureData> Layers);

/** The shared tables icon reads use, each read on first use, so many reads can share one copy. */
internal struct InvestmentIconTables
{
    public byte[]? Icons, Art;
}

public sealed partial class InvestmentCatalog
{
    /** Display icon indices select shared rows; their hashes need not match the item hash. */
    public InvestmentIcon? ReadIcon(InvestmentItem item, CancellationToken token = default)
    {
        var tables = new InvestmentIconTables();
        return ReadIcon(item, ref tables, token);
    }

    /** Reuses the table bytes in tables, or reads them into tables on first use. */
    internal InvestmentIcon? ReadIcon(InvestmentItem item, ref InvestmentIconTables tables, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.DisplayTag == 0 || !catalog.ContainsTag(sources.Icons)) return null;
        RequireClass(catalog, item.DisplayTag, DisplayClass);
        byte[] display = catalog.Read(item.DisplayTag);
        const string iconIndex = "DisplayProperties.IconIndex";
        if (!TagView.FitsThrough<ItemDisplayStrings>(display, 0, iconIndex)) throw new InvalidDataException("Item icon index is truncated.");
        ushort index = TagView.At<ushort>(display, TagView.FieldOffset<ItemDisplayStrings>(iconIndex));
        return index == ushort.MaxValue ? null : ReadIcon(index, Watermark(item, ref tables), ref tables, token);
    }

    public InvestmentIcon? ReadPerkIcon(int perk, CancellationToken token = default)
    {
        var tables = new InvestmentIconTables();
        return perk >= 0 && perk < PerkDefinitions.Count && PerkDefinitions[perk].IconIndex != ushort.MaxValue
            && catalog.ContainsTag(sources.Icons) ? ReadIcon(PerkDefinitions[perk].IconIndex, 0, ref tables, token) : null;
    }

    /** One shared icon row without an item's season watermark, such as a mark no item names. */
    public InvestmentIcon? ReadIcon(int index, CancellationToken token = default)
    {
        var tables = new InvestmentIconTables();
        return catalog.ContainsTag(sources.Icons) ? ReadIcon(index, 0, ref tables, token) : null;
    }

    /** True for the classes ReadIconAsset draws: icon containers and icon bitmaps. */
    public static bool IsIconAsset(uint reference) => reference is SIconContainer.Class or SIconBitmap.Class;

    /** An icon container's layers, or an icon bitmap as one layer; null for another asset or a container with no artwork. */
    public InvestmentIcon? ReadIconAsset(uint tag, CancellationToken token = default)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || !IsIconAsset(entry.Reference)) return null;
        if (entry.Reference == SIconContainer.Class) return ReadContainer(tag, 0, token);
        var texture = new TextureDataReader(catalog).Read(CuiBitmap.ReadTexture(catalog.Read(tag)));
        return new(0, tag, texture, [texture]);
    }

    /** The season watermark container the item art lists after the icon's, or 0 when it lists none. */
    private uint Watermark(InvestmentItem item, ref InvestmentIconTables tables)
    {
        if (!catalog.ContainsTag(sources.ItemArt)) return 0;
        try
        {
            tables.Art ??= catalog.Read(sources.ItemArt);
            return InvestmentItemArt.ReadIconContainers(tables.Art, item.Index) is [_, var mark, ..] ? mark : 0;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return 0; }
    }

    /** Icon rows are shared by items, perks and marks no item names; a row names a container. */
    private InvestmentIcon? ReadIcon(int index, uint watermark, ref InvestmentIconTables tables, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (tables.Icons is null)
        {
            RequireClass(catalog, sources.Icons, IconMap.Class);
            tables.Icons = catalog.Read(sources.Icons);
        }
        byte[] icons = tables.Icons;
        var rows = TagArray.Checked(icons, TagView.FieldOffset<IconMap>("Table"), stride: Unsafe.SizeOf<C80802957>());
        if (index < 0 || index >= rows.Count) throw new InvalidDataException("Icon index is outside its table.");
        uint containerTag = TagView.At<C80802957>(icons, rows.Data + index * Unsafe.SizeOf<C80802957>()).Unk10;
        return ReadContainer(containerTag, watermark, token);
    }

    /**
     * A container's layers each name a bitmap. No artwork means no icon; another layer whose bitmap is missing, of
     * another class or unreadable is left out. An empty watermark slot takes the artwork of the item's watermark container.
     */
    private InvestmentIcon? ReadContainer(uint containerTag, uint watermark, CancellationToken token)
    {
        RequireClass(catalog, containerTag, SIconContainer.Class);
        byte[] bytes = catalog.Read(containerTag);
        if (!TagView.Fits<SIconContainer>(bytes, 0)) throw new InvalidDataException("Icon container is truncated.");
        var container = TagView.At<SIconContainer>(bytes);
        uint bitmapTag = container.IconBitmap0PrimaryBitmap;
        if (bitmapTag is 0 or uint.MaxValue) return null;
        RequireClass(catalog, bitmapTag, SIconBitmap.Class);
        var textures = new TextureDataReader(catalog);
        TextureData Layer(uint tag) => textures.Read(CuiBitmap.ReadTexture(catalog.Read(tag)));
        var artwork = Layer(bitmapTag);
        uint mark = container.IconBitmap3;
        if (mark is (0 or uint.MaxValue) && catalog.TryGetEntry(watermark, out var entry)
            && entry.Reference == SIconContainer.Class && catalog.Read(watermark) is var marks
            && TagView.Fits<SIconContainer>(marks, 0))
            mark = TagView.At<SIconContainer>(marks).IconBitmap0PrimaryBitmap;
        // Draw order: the rarity plate, the artwork, the season watermark, then the foreground.
        uint[] order = [container.IconBitmap2, bitmapTag, mark, container.IconBitmap4];
        var layers = new List<TextureData>();
        for (int layer = 0; layer < order.Length; layer++)
        {
            token.ThrowIfCancellationRequested();
            uint tag = order[layer];
            if (layer == 1) layers.Add(artwork);
            else if (tag is not (0 or uint.MaxValue) && catalog.TryGetEntry(tag, out var bitmap)
                && bitmap.Reference == SIconBitmap.Class)
                try { layers.Add(Layer(tag)); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException) { }
        }
        return new(containerTag, bitmapTag, artwork, layers);
    }
}

/** Reads many item icons with one copy of the shared tables, which ReadIcon reads again on every call. Not thread-safe. */
public sealed class InvestmentIconReader(InvestmentCatalog catalog)
{
    private InvestmentIconTables tables;

    public InvestmentIcon? Read(InvestmentItem item,
        CancellationToken token = default) => catalog.ReadIcon(item, ref tables, token);
}
