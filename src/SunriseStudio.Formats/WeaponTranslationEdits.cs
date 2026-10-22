using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** One column of a weapon-type group: its table index, column index and display name. */
public readonly record struct WeaponTranslationColumn(int Table, int Column, string Name);

/** One table of a weapon-type group: the translation key it reads, its stat range and its rows of columns. */
public sealed record WeaponTranslationTable(int Index, int Key, float Minimum, float Maximum, int RowOffset,
    bool Interpolates, IReadOnlyList<IReadOnlyList<float>> Rows)
{
    /** The weapon stat this table reads, or null when its key names no stat. */
    public byte? Stat => WeaponTranslationEdits.StatOf(Key);
    public int Columns => Rows.Count == 0 ? 0 : Rows.Min(row => row.Count);

    /** The column's value at a stat value, read the way the game reads it. */
    public float Evaluate(int column, float stat)
    {
        int last = Rows.Count - 1;
        float span = Maximum - Minimum;
        float position = span == 0 ? 0 : Math.Clamp((stat - Minimum) * last / span, 0, last);
        position += RowOffset;
        if (!Interpolates) return Rows[Math.Clamp((int)position - RowOffset, 0, last)][column];
        int lower = Math.Clamp((int)MathF.Floor(position) - RowOffset, 0, last), upper = Math.Min(lower + 1, last);
        float blend = position - MathF.Floor(position);
        return Rows[lower][column] + (Rows[upper][column] - Rows[lower][column]) * blend;
    }
}

/** A weapon translation config turns raw item stats into gameplay values through per-type tables. */
public static class WeaponTranslationEdits
{
    /** Auto rifle, pulse, scout and SMG translation config shared by stock weapons. */
    public const uint SharedTranslation = 0x8152C7F9;
    public const byte RateStat = 14, MagazineStat = 24;
    public static readonly WeaponTranslationColumn Rate = new(0, 0, "rounds per second");
    public static readonly WeaponTranslationColumn Damage = new(0, 2, "damage per shot");
    public static readonly WeaponTranslationColumn MagazineReferenceRate = new(1, 0, "magazine reference rate");
    public static readonly WeaponTranslationColumn Precision = new(1, 1, "precision multiplier");
    public static readonly WeaponTranslationColumn MagazineBase = new(7, 0, "magazine base");
    // Weapon-type groups at +0x430; each owns a table array. A table has a row array, and each row is a float
    // array of columns.
    private const int GroupsField = 0x430, GroupStride = 0x30, TableStride = 0x38, RowsField = 16, RowStride = 16;
    private const uint GroupClass = 0x80803975, TableClass = 0x80803979, ColumnClass = 0x8080000F;
    // A table names its key at +32 and its row offset at +36, maps the stat range +40..+44 onto its rows, and
    // blends rows when +48 is set.
    private const int TableKey = 32, TableRowOffset = 36, TableMinimum = 40, TableMaximum = 44, TableInterpolates = 48;
    // Selector rows at +0x620, 40 bytes each: the row whose key at +16 equals the weapon's type key is the group.
    // Each row starts with {i8 key, f32 value} overrides that pin a key's stat.
    private const int SelectorsField = 0x620, SelectorStride = 40, SelectorKey = 16, OverrideStride = 8;

    // A group's routing entries at +0x10 write a component input from operands at +0x10, +0x24 and +0x38 with the
    // operation byte at +0xC. Its runtime logic rows at +0x20 write their output inputs from the operand rows at +0x10;
    // type 2 rows take four operands. Both run in one order, the entry's +0x4C and the row's +0x24.
    private const int RoutesField = 0x10, RouteStride = 0x50, RouteOperation = 0xC, RouteOperands = 0x10, RouteOrder = 0x4C;
    private const int LogicField = 0x20, LogicStride = 0x28, LogicOperands = 0x10, LogicType = 0x20, LogicOrder = 0x24;
    private const uint RouteClass = 0x80803981, LogicClass = 0x8080397C, OperandClass = 0x80803986, OutputClass = 0x80803987;
    private const int LogicOutputStride = 0xC, MagazineLogic = 2, MagazineOperands = 4;
    // An operand of kind 0 reads translation output +0xC, numbered from 0x1E00 as table * 0x20 + column, or its
    // constant at +0x10 when that is 0. Another kind reads what an earlier row wrote to component +0, input +4.
    private const int OperandStride = 0x14, OperandOutput = 0xC, OperandConstant = 0x10;
    private const uint FirstOutput = 0x1E00;
    private const int OutputsPerTable = 0x20;
    /** Component inputs by component selector: Magazine (1) input 0 is capacity, Barrel (2) input 22 bullets per shot. */
    public static readonly (uint Component, uint Input) Capacity = (1, 0), BulletsPerShot = (2, 22);

    // Translation key -> weapon stat; keys 2, 12 and 19 read no stat.
    private static readonly byte?[] KeyStats =
        [14, 24, null, 32, 17, 22, 20, 16, 21, 27, 18, 15, null, 25, 26, 26, 26, 30, 31, null];

    public static byte? StatOf(int key) => key >= 0 && key < KeyStats.Length ? KeyStats[key] : null;

    public static int GroupCount(ReadOnlyMemory<byte> config) =>
        new TagReader(config).Array(GroupsField, GroupClass, GroupStride).Count;

    /** The group the game selects for a weapon type key; a key no selector row holds selects group 0. */
    public static int SelectGroup(ReadOnlyMemory<byte> config, uint typeKey)
    {
        var rows = new TagReader(config).Array(SelectorsField, stride: SelectorStride);
        for (int index = 0; index < rows.Count; index++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(config.Span[(rows.Data + index * SelectorStride + SelectorKey)..]) == typeKey)
                return index < GroupCount(config) ? index : throw new InvalidDataException("A selector row has no weapon-type group.");
        return 0;
    }

    /** The selector key of each group, in group order. */
    public static IReadOnlyList<uint> SelectorKeys(ReadOnlyMemory<byte> config)
    {
        var rows = new TagReader(config).Array(SelectorsField, stride: SelectorStride);
        return Enumerable.Range(0, rows.Count).Select(index =>
            BinaryPrimitives.ReadUInt32LittleEndian(config.Span[(rows.Data + index * SelectorStride + SelectorKey)..])).ToArray();
    }

    /** Stat values the selected row pins, by translation key. */
    public static IReadOnlyDictionary<int, float> Overrides(ReadOnlyMemory<byte> config, int group)
    {
        var reader = new TagReader(config);
        var rows = reader.Array(SelectorsField, stride: SelectorStride);
        var result = new Dictionary<int, float>();
        if (group >= rows.Count) return result;
        var overrides = reader.Array(rows.Data + group * SelectorStride, stride: OverrideStride);
        for (int index = 0; index < overrides.Count; index++)
        {
            int at = overrides.Data + index * OverrideStride;
            result[(sbyte)config.Span[at]] = BinaryPrimitives.ReadSingleLittleEndian(config.Span[(at + 4)..]);
        }
        return result;
    }

    /** Every table of one group. */
    public static IReadOnlyList<WeaponTranslationTable> Tables(ReadOnlyMemory<byte> config, int group)
    {
        var reader = new TagReader(config);
        var tables = reader.Array(Group(reader, group), TableClass, TableStride);
        return Enumerable.Range(0, tables.Count).Select(index =>
        {
            int table = tables.Data + index * TableStride;
            var rows = reader.Array(table + RowsField, stride: RowStride);
            var values = Enumerable.Range(0, rows.Count).Select(row =>
            {
                var columns = reader.Array(rows.Data + row * RowStride, ColumnClass, 4);
                return (IReadOnlyList<float>)Enumerable.Range(0, columns.Count)
                    .Select(column => BinaryPrimitives.ReadSingleLittleEndian(config.Span[(columns.Data + column * 4)..])).ToArray();
            }).ToArray();
            var span = config.Span;
            return new WeaponTranslationTable(index, BinaryPrimitives.ReadInt32LittleEndian(span[(table + TableKey)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(table + TableMinimum)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(table + TableMaximum)..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(table + TableRowOffset)..]), span[table + TableInterpolates] != 0, values);
        }).ToArray();
    }

    /** Every row of the column takes `value`. */
    public static byte[] SetFlat(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException($"The {column.Name} must be a finite number.");
        byte[] result = config.ToArray();
        foreach (int at in Offsets(config, group, column))
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at), value);
        return result;
    }

    /** Every row of the column is multiplied by `factor`, so the column keeps its shape across the stat range. */
    public static byte[] Scale(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column, float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0) throw new InvalidDataException($"The {column.Name} scale must be positive.");
        byte[] result = config.ToArray();
        foreach (int at in Offsets(config, group, column))
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at), BinaryPrimitives.ReadSingleLittleEndian(result.AsSpan(at)) * factor);
        return result;
    }

    /** Native magazine size: round half to even of base times rate over the reference rate. */
    public static int Magazine(float magazineBase, float rate, float referenceRate) =>
        (int)Math.Round(magazineBase * rate / referenceRate, MidpointRounding.ToEven);

    /** True when the config has weapon-type groups; a weapon root also constructs configs that are not translators. */
    public static bool IsTranslator(ReadOnlyMemory<byte> config) =>
        new TagReader(config).TryArray(GroupsField, GroupClass, GroupStride, out var groups) && groups.Count != 0;

    /**
     * The component inputs a group writes, by component selector and input: its routing entries and its type 2 logic
     * rows, the native magazine size times bullets per shot with 0 bullets kept as 1, in their shared order.
     * Operations 0 to 10 are add, subtract, multiply, divide, copy, ceiling, floor, round half to even, clamp the third
     * operand, maximum and minimum. Other operations and logic types write nothing.
     * @param tables The group's tables, read once by the caller.
     * @param inputs The stat value each translation key reads.
     */
    public static IReadOnlyDictionary<(uint Component, uint Input), float> Replay(ReadOnlyMemory<byte> config, int group,
        IReadOnlyList<WeaponTranslationTable> tables, IReadOnlyDictionary<int, float> inputs)
    {
        var reader = new TagReader(config);
        int at = Group(reader, group);
        var routes = reader.Array(at + RoutesField, RouteClass, RouteStride);
        var logic = reader.Array(at + LogicField, LogicClass, LogicStride);
        var steps = Enumerable.Range(0, routes.Count).Select(index => routes.Data + index * RouteStride)
            .Select(row => (Order: reader.U32(row + RouteOrder), Logic: false, Row: row))
            .Concat(Enumerable.Range(0, logic.Count).Select(index => logic.Data + index * LogicStride)
                .Where(row => reader.I32(row + LogicType) == MagazineLogic)
                .Select(row => (Order: reader.U32(row + LogicOrder), Logic: true, Row: row)))
            .OrderBy(step => step.Order);
        var written = new Dictionary<(uint, uint), float>();
        foreach (var (_, isLogic, row) in steps)
        {
            if (isLogic)
            {
                var (count, operands, _) = reader.Array(row + LogicOperands, OperandClass, OperandStride);
                if (count < MagazineOperands) throw new InvalidDataException("A magazine logic row has fewer than four operands.");
                var outputs = reader.Array(row, OutputClass, LogicOutputStride);
                float reference = Operand(operands), rate = Operand(operands + OperandStride);
                float magazineBase = Operand(operands + 2 * OperandStride), bullets = Operand(operands + 3 * OperandStride);
                if (reference == 0) throw new InvalidDataException("A magazine logic row has a zero reference rate.");
                float value = Magazine(magazineBase, rate, reference) * (bullets == 0 ? 1 : bullets);
                for (int index = 0; index < outputs.Count; index++)
                    written[Input(outputs.Data + index * LogicOutputStride)] = value;
                continue;
            }
            float a = Operand(row + RouteOperands), b = Operand(row + RouteOperands + OperandStride);
            float? result = (sbyte)reader.U8(row + RouteOperation) switch
            {
                0 => a + b, 1 => a - b, 2 => a * b, 3 => a / b, 4 => a, 5 => MathF.Ceiling(a), 6 => MathF.Floor(a),
                7 => MathF.Round(a, MidpointRounding.ToEven),
                8 => MathF.Min(MathF.Max(Operand(row + RouteOperands + 2 * OperandStride), a), b),
                9 => MathF.Max(a, b), 10 => MathF.Min(a, b), _ => null
            };
            if (result is { } routed) written[Input(row)] = routed;
        }
        return written;

        (uint, uint) Input(int row) => (reader.U32(row), reader.U32(row + 4));
        float Operand(int operand)
        {
            if (!reader.Has(operand, OperandStride)) throw new InvalidDataException("A translator operand leaves the config.");
            if (reader.U32(operand) != 0) return written.GetValueOrDefault(Input(operand));
            uint output = reader.U32(operand + OperandOutput);
            if (output == 0) return BinaryPrimitives.ReadSingleLittleEndian(config.Span[(operand + OperandConstant)..]);
            int table = (int)(output - FirstOutput) / OutputsPerTable, column = (int)(output - FirstOutput) % OutputsPerTable;
            if (output < FirstOutput || table >= tables.Count || column >= tables[table].Columns)
                throw new InvalidDataException("A translator operand names an output the group does not have.");
            return tables[table].Evaluate(column, inputs.GetValueOrDefault(tables[table].Key));
        }
    }

    private static int Group(TagReader reader, int group)
    {
        var groups = reader.Array(GroupsField, GroupClass, GroupStride);
        if (group < 0 || group >= groups.Count) throw new InvalidDataException("The weapon-type group is outside the config.");
        return groups.Data + group * GroupStride;
    }

    private static IReadOnlyList<int> Offsets(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column)
    {
        var reader = new TagReader(config);
        var tables = reader.Array(Group(reader, group), TableClass, TableStride);
        if (column.Table < 0 || column.Table >= tables.Count)
            throw new InvalidDataException($"The weapon-type group has no table for the {column.Name}.");
        var rows = reader.Array(tables.Data + column.Table * TableStride + RowsField, stride: RowStride);
        return Enumerable.Range(0, rows.Count).Select(row =>
        {
            var columns = reader.Array(rows.Data + row * RowStride, ColumnClass, 4);
            return column.Column < columns.Count ? columns.Data + column.Column * 4
                : throw new InvalidDataException($"A translation row has no column for the {column.Name}.");
        }).ToArray();
    }
}
