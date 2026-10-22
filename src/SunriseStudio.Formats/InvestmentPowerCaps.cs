namespace SunriseStudio.Formats;

/** An item's quality block lists one power cap row per version; the cap table holds each cap divided by ten. */
public static class InvestmentPowerCaps
{
    public const uint TableClass = 0x80807797;
    private const uint RowClass = 0x80807801, VersionClass = 0x80805921;
    private const int RowsField = 8, RowStride = 8, CapField = 4, QualityBlock = 72, VersionsField = 96;
    // Rows from here up are the native no-limit values, never displayed as Power.
    private const int NoLimit = 999_000;

    /** Each row's cap in Power, in table order. */
    public static IReadOnlyList<int> Read(ReadOnlyMemory<byte> table)
    {
        var reader = new TagReader(table);
        var rows = reader.Array(RowsField, RowClass, RowStride);
        float Cap(int index) => BitConverter.Int32BitsToSingle(reader.I32(rows.Data + index * RowStride + CapField));
        return [.. Enumerable.Range(0, rows.Count).Select(index => (int)MathF.Round(Cap(index) * 10))];
    }

    /**
     * The highest cap among the item's versions; a version with no limit takes the highest cap the game displays.
     * Null when the item has no versions or one names no row.
     */
    public static int? ItemCap(ReadOnlyMemory<byte> definition, IReadOnlyList<int> caps)
    {
        var reader = new TagReader(definition);
        int quality = reader.Relative(QualityBlock);
        if (quality < 0) return null;
        var versions = reader.Array(quality + VersionsField, VersionClass, 2);
        if (versions.Count == 0) return null;
        int displayed = caps.Where(cap => cap < NoLimit).DefaultIfEmpty(0).Max();
        int highest = 0;
        for (int i = 0; i < versions.Count; i++)
        {
            int row = reader.U16(versions.Data + i * 2);
            if (row >= caps.Count) return null;
            highest = Math.Max(highest, caps[row] >= NoLimit ? displayed : caps[row]);
        }
        return highest;
    }
}
