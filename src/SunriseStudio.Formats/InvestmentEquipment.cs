using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** The equipment block an item definition points at from +16; its label limits how many such items equip at once. */
public static class InvestmentEquipment
{
    /** A unique-equip label: FNV-1 of its name, and the hash of the text shown when the game refuses an equip. */
    private readonly record struct Label(uint Name, uint Text);

    // The block's class marker precedes it; the label is {u32 name, u32 text} at block +16.
    private const int DefinitionBlock = 16, LabelField = 16;
    private const uint BlockClass = 0x80807C02;
    // Definition +184 is the inventory bucket.
    private const int BucketField = 184;
    // No label lets any number equip. The exotic texts are the ones stock exotic weapons and armor carry.
    private static readonly Label None = new(InvestmentItemClone.EmptyStringHash, 0);
    private static readonly Label ExoticWeapon = new(InvestmentItemClone.Fnv1("exotic_weapon"), 0xEF7B6AD3);
    private static readonly Label ExoticArmor = new(InvestmentItemClone.Fnv1("exotic_armor"), 0x2D5D6C45);

    /** True when a weapon or armor piece carries its kind's one-exotic label; null for other buckets or no block. */
    public static bool? ReadExoticLimit(ReadOnlySpan<byte> definition) =>
        ExoticLabel(definition) is { } kind && BlockOffset(definition) is { } block ? Read(definition, block).Name == kind.Name : null;

    /** Gives a weapon or armor piece its kind's one-exotic label in place of any other, or takes that label away. */
    public static byte[] SetExoticLimit(ReadOnlySpan<byte> definition, bool limited)
    {
        var kind = ExoticLabel(definition) ?? throw new InvalidDataException("Only weapons and armor carry the exotic limit.");
        int block = BlockOffset(definition) ?? throw new InvalidDataException("The item has no equipment block.");
        var current = Read(definition, block);
        // Turning the limit off keeps any other label.
        var label = limited ? kind : current.Name == kind.Name ? None : current;
        byte[] output = definition.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(block + LabelField), label.Name);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(block + LabelField + 4), label.Text);
        return output;
    }

    private static Label? ExoticLabel(ReadOnlySpan<byte> definition)
    {
        if (definition.Length <= BucketField) throw new InvalidDataException("Item definition is truncated.");
        return definition[BucketField] switch
        {
            <= InvestmentItemKinds.LastWeaponBucket => ExoticWeapon,
            <= InvestmentItemKinds.LastArmorBucket => ExoticArmor,
            _ => null
        };
    }

    private static Label Read(ReadOnlySpan<byte> definition, int block) =>
        new(BinaryPrimitives.ReadUInt32LittleEndian(definition[(block + LabelField)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(definition[(block + LabelField + 4)..]));

    /** The block's offset, or null when the item has none. */
    private static int? BlockOffset(ReadOnlySpan<byte> definition)
    {
        if (definition.Length < DefinitionBlock + 8) return null;
        long relative = BinaryPrimitives.ReadInt64LittleEndian(definition[DefinitionBlock..]);
        if (relative == 0) return null;
        long at = DefinitionBlock + relative;
        if (at < 4 || at > definition.Length - (LabelField + 8)
            || BinaryPrimitives.ReadUInt32LittleEndian(definition[(int)(at - 4)..]) != BlockClass)
            throw new InvalidDataException("The item's equipment block is not where its pointer says.");
        return (int)at;
    }
}
