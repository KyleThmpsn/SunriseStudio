using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/**
 * The equipping block an item definition points at, as Bungie's equippingBlock. Its unique label limits how many items
 * with that label equip at once.
 */
public static class InvestmentEquippingBlock
{
    /** A unique label: FNV-1 of its name, and the hash of the text shown when the game refuses an equip. */
    private readonly record struct UniqueLabel(uint Name, uint Text);

    private const string BlockPointer = nameof(ItemDefinition.EquippingItemBlock), Bucket = "InventoryItemBlock.InventoryBucket";
    private static readonly int LabelName = TagView.FieldOffset<ItemEquippingBlock>(nameof(ItemEquippingBlock.Unk10));
    /** The unique label's text hash, which the registry does not name: the u32 after the label's name hash. */
    private static readonly int LabelText = LabelName + sizeof(uint);
    // No label lets any number equip. The exotic texts are the ones stock exotic weapons and armor carry.
    private static readonly UniqueLabel None = new(StringHash.Empty, 0);
    private static readonly UniqueLabel ExoticWeapon = new(StringBankWriter.Hash("exotic_weapon"), 0xEF7B6AD3);
    private static readonly UniqueLabel ExoticArmor = new(StringBankWriter.Hash("exotic_armor"), 0x2D5D6C45);

    /** True when a weapon or armor piece carries its kind's one-exotic label; null for other buckets or no block. */
    public static bool? ReadExoticUniqueLabel(ReadOnlySpan<byte> definition) =>
        ExoticLabel(definition) is { } kind && BlockOffset(definition) is { } block
            ? Read(definition, block).Name == kind.Name : null;

    /** Gives a weapon or armor piece its kind's one-exotic label in place of any other, or takes that label away. */
    public static byte[] SetExoticUniqueLabel(ReadOnlySpan<byte> definition, bool limited)
    {
        var kind = ExoticLabel(definition) ?? throw new InvalidDataException("Only weapons and armor carry the exotic limit.");
        int block = BlockOffset(definition) ?? throw new InvalidDataException("The item has no equipping block.");
        var current = Read(definition, block);
        // Turning the limit off keeps any other label.
        var label = limited ? kind : current.Name == kind.Name ? None : current;
        byte[] output = definition.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(block + LabelName), label.Name);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(block + LabelText), label.Text);
        return output;
    }

    private static UniqueLabel? ExoticLabel(ReadOnlySpan<byte> definition)
    {
        if (!TagView.FitsThrough<ItemDefinition>(definition, 0, Bucket))
            throw new InvalidDataException("Item definition is truncated.");
        return TagView.Member<ItemDefinition, byte>(definition, 0, Bucket) switch
        {
            <= InvestmentItemKinds.LastWeaponBucket => ExoticWeapon,
            <= InvestmentItemKinds.LastArmorBucket => ExoticArmor,
            _ => null
        };
    }

    private static UniqueLabel Read(ReadOnlySpan<byte> definition, int block) =>
        new(TagRead.U32(definition, block + LabelName), TagRead.U32(definition, block + LabelText));

    /** The block's offset, or null when the item has none. */
    private static int? BlockOffset(ReadOnlySpan<byte> definition)
    {
        if (!TagView.FitsThrough<ItemDefinition>(definition, 0, BlockPointer)) return null;
        ref readonly var pointer =
            ref TagView.Member<ItemDefinition, RelativePointer<ItemEquippingBlock>>(definition, 0, BlockPointer);
        if (pointer.IsNull) return null;
        long at = TagView.OffsetOf(definition, in pointer) + pointer.Relative;
        if (at < TagView.ClassWordSize || at > definition.Length - (LabelText + sizeof(uint))
            || TagView.ClassAt(definition, (int)at) != ItemEquippingBlock.Class)
            throw new InvalidDataException("The item's equipping block is not where its pointer says.");
        return (int)at;
    }
}
