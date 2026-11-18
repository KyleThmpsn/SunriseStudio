using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** A weapon's ammo type as its display records it: 1 Primary, 2 Special, 3 Heavy, 0 for none. */
public static class InvestmentAmmoType
{
    // The display's +18 pointer names its type record, whose first u16 is the ammo type.
    private static readonly int TypeRecordField =
        TagView.FieldOffset<ItemDisplayStrings>(nameof(ItemDisplayStrings.EquippingItemBlock));

    /** Ammo type names, Primary first. */
    public static readonly IReadOnlyList<string> Names = ["Primary", "Special", "Heavy"];

    /** True for an ammo type a weapon can have. */
    public static bool Valid(int ammo) => ammo >= 1 && ammo <= Names.Count;

    /** The ammo type's name; empty for none. */
    public static string Name(int ammo) => Valid(ammo) ? Names[ammo - 1] : "";

    /** The display's ammo type, 0 for none, or null when the display has no type record. */
    public static int? ReadDisplay(ReadOnlySpan<byte> display) =>
        TypeRecord(display) is { } record ? TagRead.U16(display, record) : null;

    /** Where the display's type record starts, or null when it has none. */
    private static int? TypeRecord(ReadOnlySpan<byte> display)
    {
        if (!TagView.FitsThrough<ItemDisplayStrings>(display, 0, nameof(ItemDisplayStrings.EquippingItemBlock))) return null;
        int record = TagPointer.TryChecked(display, TypeRecordField);
        return record >= 0 && record <= display.Length - sizeof(ushort) && TagView.ClassAt(display, record) == C80805d1a.Class
            ? record : null;
    }

    /** An item's ammo type, or null when it has none or its display does not read. */
    public static int? Read(PackageCatalog packages, InvestmentItem item)
    {
        try
        {
            return item.DisplayTag != 0 && ReadDisplay(packages.Read(item.DisplayTag)) is { } ammo && Valid(ammo) ? ammo : null;
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException) { return null; }
    }

    /** The display with its type record's ammo type set. */
    public static byte[] SetDisplay(ReadOnlySpan<byte> display, int ammo)
    {
        int record = TypeRecord(display) ?? throw new InvalidDataException("The weapon display has no ammo classification.");
        byte[] output = display.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(record), checked((ushort)ammo));
        return output;
    }
}
