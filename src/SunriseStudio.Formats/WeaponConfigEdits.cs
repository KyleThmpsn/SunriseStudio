using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/**
 * A weapon config cloned for one weapon is retagged inside itself and in that weapon's gameplay root. The weapon content
 * config also holds the ammo type the weapon uses.
 */
public static class WeaponConfigEdits
{
    /**
     * Retags the reciprocal owner pairs that name the config itself.
     * @return The retagged bytes and the number of owner pairs found.
     */
    public static (byte[] Bytes, int Pairs) RetagConfig(ReadOnlyMemory<byte> config, uint source, uint target)
    {
        byte[] bytes = config.ToArray();
        HashSet<int> owners = [];
        int pairs = 0;
        for (int at = 0; at + 16 <= bytes.Length; at += 4)
        {
            if (U32(bytes, at) != source) continue;
            long peer = I64(bytes, at + 8);
            uint type = U32(bytes, at + 4);
            if (peer < 0 || peer > bytes.Length - 16 || type < 0x80800000 || type >= 0x84000000) continue;
            if (U32(bytes, (int)peer) != source || I64(bytes, (int)peer + 8) != at) continue;
            owners.Add(at); owners.Add((int)peer);
            if (at < peer) pairs++;
        }
        // A typed one-way link names an owner-pair side and carries that pair's peer class.
        HashSet<int> links = [];
        for (int at = 0; at + 16 <= bytes.Length; at += 4)
        {
            if (U32(bytes, at) != source || owners.Contains(at)) continue;
            long side = I64(bytes, at + 8);
            if (side < 0 || side > bytes.Length - 16 || !owners.Contains((int)side)) continue;
            long partner = I64(bytes, (int)side + 8);
            if (partner >= 0 && partner <= bytes.Length - 8 && U32(bytes, (int)partner + 4) == U32(bytes, at + 4)) links.Add(at);
        }
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
            if (U32(bytes, at) == source && !owners.Contains(at) && !links.Contains(at))
                throw new InvalidDataException("A weapon config has an unclassified owner reference.");
        foreach (int at in owners.Concat(links)) Put32(bytes, at, target);
        return (bytes, pairs);
    }

    /** The configs a gameplay root constructs, in its construction array at `+16`. */
    public static IReadOnlyList<uint> ConstructionConfigs(ReadOnlyMemory<byte> root)
    {
        var reader = new TagReader(root);
        var rows = reader.Array(16, 0x80809C04, 12);
        return Enumerable.Range(0, rows.Count).Select(index => reader.U32(rows.Data + index * 12)).ToArray();
    }

    /** Typed construction and binding links must all select the same owned config. */
    public static byte[] RetagRoot(ReadOnlyMemory<byte> root, uint source, uint target)
    {
        if (ConstructionConfigs(root).Count(tag => tag == source) != 1)
            throw new InvalidDataException("The weapon root needs one matching config.");
        byte[] result = root.ToArray();
        var reader = new TagReader(root);
        foreach (var layout in new[] { (16, 0x80809C04u, 12, new[] { 0 }),
            (32, 0x80809BC9u, 72, new[] { 8, 40 }), (48, 0x80809BC9u, 72, new[] { 8, 40 }),
            (88, 0x80809C22u, 40, new[] { 16 }), (104, 0x80809C20u, 24, new[] { 0 }) })
        {
            var rows = reader.Array(layout.Item1, layout.Item2, layout.Item3);
            for (int index = 0; index < rows.Count; index++)
                foreach (int offset in layout.Item4)
                {
                    int at = rows.Data + index * layout.Item3 + offset;
                    if (reader.U32(at) == source) Put32(result, at, target);
                }
        }
        for (int at = 0; at + 4 <= result.Length; at += 4)
            if (U32(result, at) == source)
                throw new InvalidDataException("The weapon root has an unclassified config reference.");
        return result;
    }

    // Root binding rows {u32 binding, u32 first descriptor | resource count << 16} at +72 select 24-byte descriptors
    // {u32 config, u32 class, u64 offset in that config} at +104.
    private const uint WeaponContentBinding = 0x5F0DD954, WeaponContentClass = 0x80803ACB, DescriptorClass = 0x80809C20;
    private const int BindingsField = 72, BindingStride = 8, DescriptorsField = 104, DescriptorStride = 24;
    // Weapon content names its definition {u32 config, u32 class, u64 offset}. The definition has its default property
    // row at +128 and rows perks can select from the array at +576; a 448-byte row enables its ammo override at +52 and
    // holds the zero-based ammo type at +53.
    private const uint ContentDefinitionClass = 0x80803AC9, ContentPropertyClass = 0x80803ACF;
    private const int DefaultProperties = 128, VariantProperties = 576, PropertyStride = 448, AmmoOverride = 52;

    /** The config holding the weapon content a gameplay root binds, and the content's offset in it. */
    public static (uint Config, int Content) WeaponContent(ReadOnlyMemory<byte> root)
    {
        var reader = new TagReader(root);
        var bindings = reader.Array(BindingsField, stride: BindingStride);
        int[] rows = [.. Enumerable.Range(0, bindings.Count).Select(index => bindings.Data + index * BindingStride)
            .Where(row => reader.U32(row) == WeaponContentBinding)];
        if (rows.Length != 1) throw new InvalidDataException("The weapon root needs one weapon content binding.");
        uint selector = reader.U32(rows[0] + 4);
        var descriptors = reader.Array(DescriptorsField, DescriptorClass, DescriptorStride);
        int index = (int)(selector & 0xFFFF);
        if ((selector >> 16 & 0x7FFF) != 1 || index >= descriptors.Count)
            throw new InvalidDataException("The weapon content binding needs one descriptor.");
        int descriptor = descriptors.Data + index * DescriptorStride;
        if (reader.U32(descriptor + 4) != WeaponContentClass) throw new InvalidDataException("The weapon content has an unsupported class.");
        return (reader.U32(descriptor), checked((int)reader.I64(descriptor + 8)));
    }

    /** Forces ammo type 1 Primary, 2 Special or 3 Heavy in the weapon content's default and variant property rows. */
    public static byte[] SetAmmoOverride(ReadOnlyMemory<byte> config, uint configTag, int content, int ammo)
    {
        var reader = new TagReader(config);
        if (!reader.Has(content, 16) || reader.U32(content) != configTag || reader.U32(content + 4) != ContentDefinitionClass)
            throw new InvalidDataException("The weapon content does not name its definition in its own config.");
        int definition = checked((int)reader.I64(content + 8));
        if (!reader.Has(definition, VariantProperties + 16)) throw new InvalidDataException("The weapon content definition is truncated.");
        var variants = reader.Array(definition + VariantProperties, ContentPropertyClass, PropertyStride);
        int[] rows = [definition + DefaultProperties, .. Enumerable.Range(0, variants.Count).Select(index => variants.Data + index * PropertyStride)];
        // Rows hold 0 or 1 and a type up to 2; anything else is a layout this editor does not know.
        if (rows.Any(row => reader.U8(row + AmmoOverride) > 1 || reader.U8(row + AmmoOverride) == 1 && reader.U8(row + AmmoOverride + 1) > 2))
            throw new InvalidDataException("A weapon content row has an unsupported ammo override.");
        byte[] output = config.ToArray();
        foreach (int row in rows) { output[row + AmmoOverride] = 1; output[row + AmmoOverride + 1] = checked((byte)(ammo - 1)); }
        return output;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
