using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

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
        int size = Unsafe.SizeOf<TagOffsetPointer>();
        HashSet<int> owners = [];
        int pairs = 0;
        for (int at = 0; at + size <= bytes.Length; at += 4)
        {
            var owner = TagOffsetPointer.Read(bytes, at);
            if (owner.Tag != source) continue;
            long peer = owner.Offset;
            uint type = owner.Word;
            if (peer < 0 || peer > bytes.Length - size || type < 0x80800000 || type >= 0x84000000) continue;
            var back = TagOffsetPointer.Read(bytes, (int)peer);
            if (back.Tag != source || back.Offset != at) continue;
            owners.Add(at); owners.Add((int)peer);
            if (at < peer) pairs++;
        }
        // A typed one-way link names an owner-pair side and carries that pair's peer class.
        HashSet<int> links = [];
        for (int at = 0; at + size <= bytes.Length; at += 4)
        {
            var link = TagOffsetPointer.Read(bytes, at);
            if (link.Tag != source || owners.Contains(at)) continue;
            long side = link.Offset;
            if (side < 0 || side > bytes.Length - size || !owners.Contains((int)side)) continue;
            long partner = TagOffsetPointer.Read(bytes, (int)side).Offset;
            if (partner is >= 0 and <= int.MaxValue && TagView.FitsThrough<TagOffsetPointer>(bytes, (int)partner, PeerClass)
                && TagRead.U32(bytes, (int)partner + TagView.FieldOffset<TagOffsetPointer>(PeerClass)) == link.Word)
                links.Add(at);
        }
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
            if (TagRead.U32(bytes, at) == source && !owners.Contains(at) && !links.Contains(at))
                throw new InvalidDataException("A weapon config has an unclassified owner reference.");
        foreach (int at in owners.Concat(links)) Put32(bytes, at, target);
        return (bytes, pairs);
    }

    /** The configs a gameplay root constructs, in its construction array. */
    public static IReadOnlyList<uint> ConstructionConfigs(ReadOnlyMemory<byte> root)
    {
        int stride = Unsafe.SizeOf<SPatternComponent>();
        var rows = TagArray.Checked(root.Span, Components, SPatternComponent.Class, stride);
        return Enumerable.Range(0, rows.Count).Select(index => TagView.At<SPatternComponent>(root.Span, rows.Data + index * stride).Config)
            .ToArray();
    }

    /** Typed construction and binding links must all select the same owned config. */
    public static byte[] RetagRoot(ReadOnlyMemory<byte> root, uint source, uint target)
    {
        if (ConstructionConfigs(root).Count(tag => tag == source) != 1)
            throw new InvalidDataException("The weapon root needs one matching config.");
        byte[] result = root.ToArray();
        foreach (var (field, type, stride, offsets) in ConfigLinks)
        {
            var rows = TagArray.Checked(root.Span, field, type, stride);
            for (int index = 0; index < rows.Count; index++)
                foreach (int offset in offsets)
                {
                    int at = rows.Data + index * stride + offset;
                    if (TagRead.U32(root.Span, at) == source) Put32(result, at, target);
                }
        }
        for (int at = 0; at + 4 <= result.Length; at += 4)
            if (TagRead.U32(result, at) == source)
                throw new InvalidDataException("The weapon root has an unclassified config reference.");
        return result;
    }

    // The second word of a config pointer carries the peer class.
    private const string PeerClass = nameof(TagOffsetPointer.Word);

    // The gameplay root's construction array.
    private static readonly int Components = TagView.FieldOffset<SEntity>(nameof(SEntity.Components));

    // Root arrays whose rows name a config: field, row class, row size, config offsets.
    private static readonly (int Field, uint Class, int Stride, int[] Offsets)[] ConfigLinks =
    [
        (Components, SPatternComponent.Class, Unsafe.SizeOf<SPatternComponent>(),
            [TagView.FieldOffset<SPatternComponent>(nameof(SPatternComponent.Config))]),
        (TagView.FieldOffset<SEntity>("HE3929a53.ConnectionList"), SEntityBindingRow.Class,
            Unsafe.SizeOf<SEntityBindingRow>(), BindingConfigs()),
        (TagView.FieldOffset<SEntity>("HE3929a53.Unknown0x30MustBe0"), SEntityBindingRow.Class,
            Unsafe.SizeOf<SEntityBindingRow>(), BindingConfigs()),
        (TagView.FieldOffset<SEntity>("H83bfb495.InterfaceDescriptors"), ActorDescriptorRow.Class,
            Unsafe.SizeOf<ActorDescriptorRow>(), [TagView.FieldOffset<ActorDescriptorRow>(nameof(ActorDescriptorRow.Config))]),
        (TagView.FieldOffset<SEntity>("H83bfb495.LinksD24ByteRowsConfigAt0"), ActorDescriptorMetadata.Class,
            Unsafe.SizeOf<ActorDescriptorMetadata>(),
            [TagView.FieldOffset<ActorDescriptorMetadata>(nameof(ActorDescriptorMetadata.Config))])
    ];

    // A config pointer's tag is its first word.
    private static int[] BindingConfigs() =>
    [
        TagView.FieldOffset<SEntityBindingRow>(nameof(SEntityBindingRow.ConfigA)),
        TagView.FieldOffset<SEntityBindingRow>(nameof(SEntityBindingRow.ConfigB))
    ];

    // The interface a gameplay root binds its weapon content under.
    private const uint WeaponContentBinding = 0x5F0DD954;
    // Of a binding row's descriptor count, the low 15 bits count descriptors.
    private const int DescriptorCountMask = 0x7FFF;
    // Root binding rows select descriptors, each a config pointer to one resource in that config.
    private static readonly int ContentBindings = TagView.FieldOffset<SEntity>("H83bfb495.H72b7ff87.Buckets"),
        ContentDescriptors = TagView.FieldOffset<SEntity>("H83bfb495.LinksD24ByteRowsConfigAt0");
    // Weapon content names its definition by a config pointer. The definition has its default property row, and rows
    // perks can select.
    private static readonly int DefaultProperties =
            TagView.FieldOffset<SWeaponHudConfigResource>(nameof(SWeaponHudConfigResource.HDe30eaea)),
        VariantProperties = TagView.FieldOffset<SWeaponHudConfigResource>(nameof(SWeaponHudConfigResource.FiringRows448Byte));
    /**
     * Two bytes of a property row the registry does not name, the two after the u32 at Unk30: the first enables the row's
     * ammo override and the second holds the zero-based ammo type.
     */
    private static readonly int AmmoOverride =
            TagView.FieldOffset<SWeaponFiringRow>(nameof(SWeaponFiringRow.Unk30)) + sizeof(uint),
        AmmoType = AmmoOverride + 1;
    // The highest zero-based ammo type: Primary, Special, Heavy.
    private const byte LastAmmoType = 2;

    /** The config holding the weapon content a gameplay root binds, and the content's offset in it. */
    public static (uint Config, int Content) WeaponContent(ReadOnlyMemory<byte> root)
    {
        var span = root.Span;
        int stride = Unsafe.SizeOf<UnknownEntityInterfaceMatch>();
        var bindings = TagArray.Checked(span, ContentBindings, stride: stride);
        int[] rows = [.. Enumerable.Range(0, bindings.Count).Select(index => bindings.Data + index * stride)
            .Where(row => TagView.At<UnknownEntityInterfaceMatch>(root.Span, row).InterfaceHash == WeaponContentBinding)];
        if (rows.Length != 1) throw new InvalidDataException("The weapon root needs one weapon content binding.");
        ref readonly var binding = ref TagView.At<UnknownEntityInterfaceMatch>(span, rows[0]);
        var descriptors = TagArray.Checked(span, ContentDescriptors, ActorDescriptorMetadata.Class,
            Unsafe.SizeOf<ActorDescriptorMetadata>());
        if ((binding.DescriptorCount & DescriptorCountMask) != 1 || binding.FirstDescriptor >= descriptors.Count)
            throw new InvalidDataException("The weapon content binding needs one descriptor.");
        var content = TagView.At<ActorDescriptorMetadata>(span,
            descriptors.Data + binding.FirstDescriptor * Unsafe.SizeOf<ActorDescriptorMetadata>()).Config;
        if (content.Word != C80803acb.Class) throw new InvalidDataException("The weapon content has an unsupported class.");
        return (content.Tag, checked((int)content.Offset));
    }

    /** Forces ammo type 1 Primary, 2 Special or 3 Heavy in the weapon content's default and variant property rows. */
    public static byte[] SetAmmoOverride(ReadOnlyMemory<byte> config, uint configTag, int content, int ammo)
    {
        var span = config.Span;
        int[] rows = PropertyRows(span, configTag, content);
        // Rows hold 0 or 1 and a type up to the last; anything else is a layout this editor does not know.
        foreach (int row in rows)
            if (TagRead.U8(span, row + AmmoOverride) > 1
                || TagRead.U8(span, row + AmmoOverride) == 1 && TagRead.U8(span, row + AmmoType) > LastAmmoType)
                throw new InvalidDataException("A weapon content row has an unsupported ammo override.");
        byte[] output = config.ToArray();
        foreach (int row in rows) { output[row + AmmoOverride] = 1; output[row + AmmoType] = checked((byte)(ammo - 1)); }
        return output;
    }

    /** The weapon content definition's default property row, then its variant rows. */
    private static int[] PropertyRows(ReadOnlySpan<byte> config, uint configTag, int content)
    {
        if (!TagView.Fits<TagOffsetPointer>(config, content)) throw new InvalidDataException("The weapon content is truncated.");
        var owner = TagOffsetPointer.Read(config, content);
        if (owner.Tag != configTag || owner.Word != SWeaponHudConfigResource.Class)
            throw new InvalidDataException("The weapon content does not name its definition in its own config.");
        int definition = checked((int)owner.Offset);
        if (!TagView.FitsThrough<SWeaponHudConfigResource>(config, definition,
                nameof(SWeaponHudConfigResource.FiringRows448Byte)))
            throw new InvalidDataException("The weapon content definition is truncated.");
        int stride = Unsafe.SizeOf<SWeaponFiringRow>();
        var variants = TagArray.Checked(config, definition + VariantProperties, SWeaponFiringRow.Class, stride);
        return [definition + DefaultProperties,
            .. Enumerable.Range(0, variants.Count).Select(index => variants.Data + index * stride)];
    }

    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
