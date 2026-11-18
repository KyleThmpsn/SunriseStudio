using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** An item's quality block lists one power cap row per version; the cap table holds each cap divided by ten. */
public static class InvestmentPowerCaps
{
    public const uint TableClass = C80807797.Class;
    private static readonly int RowsField = TagView.FieldOffset<C80807797>(nameof(C80807797.Table)),
        RowStride = Unsafe.SizeOf<C80807801>(), VersionStride = Unsafe.SizeOf<C80805921>();
    private static readonly int QualityField = TagView.FieldOffset<ItemDefinition>(nameof(ItemDefinition.QualityItemBlock));
    // The quality block's version array, each row a u16 power cap row.
    private static readonly int VersionsField = TagView.FieldOffset<C80807bf5>(nameof(C80807bf5.HB3a15912));
    // Rows from here up are the native no-limit values, never displayed as Power.
    private const int NoLimit = 999_000;

    /** Each row's cap in Power, in table order. */
    public static IReadOnlyList<int> Read(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        var rows = TagArray.Checked(span, RowsField, C80807801.Class, RowStride);
        var caps = new int[rows.Count];
        for (int index = 0; index < rows.Count; index++)
            caps[index] = (int)MathF.Round(TagView.At<C80807801>(span, rows.Data + index * RowStride).Unk4 * 10);
        return caps;
    }

    /** True for a cap the game displays as Power; a no-limit row is not one. */
    public static bool IsLimit(int cap) => cap < NoLimit;

    /**
     * The highest cap among the item's versions, a no-limit row's value included.
     * Null when the item has no versions or one names no row.
     */
    public static int? ItemCap(ReadOnlyMemory<byte> definition, IReadOnlyList<int> caps)
    {
        var span = definition.Span;
        if (!TagView.FitsThrough<ItemDefinition>(span, 0, nameof(ItemDefinition.QualityItemBlock))) return null;
        int quality = TagPointer.Checked(span, QualityField);
        if (quality < 0) return null;
        var versions = TagArray.Checked(span, quality + VersionsField, C80805921.Class, VersionStride);
        if (versions.Count == 0) return null;
        int highest = 0;
        for (int i = 0; i < versions.Count; i++)
        {
            int row = TagRead.U16(span, versions.Data + i * VersionStride);
            if (row >= caps.Count) return null;
            highest = Math.Max(highest, caps[row]);
        }
        return highest;
    }

    /** The lowest limit any gear reaches, the Power gear with a higher limit or none shows; null when none has one. */
    public static int? SeasonCap(InvestmentCatalog investment, PackageCatalog packages, IReadOnlyList<int> caps)
    {
        int? lowest = null;
        var gear = investment.Items.Where(item => item.Category is InvestmentCategory.Weapons or InvestmentCategory.Armor);
        foreach (var item in gear)
            try
            {
                if (ItemCap(packages.Read(item.Tag), caps) is { } cap && IsLimit(cap) && (lowest is null || cap < lowest))
                    lowest = cap;
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { }
        return lowest;
    }
}
