using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** A weapon's ammo type as its display records it: 1 Primary, 2 Special, 3 Heavy, 0 for none. */
public static class InvestmentAmmoType
{
    // A weapon display's ammo classification: class word at +316, u16 type at +320.
    private const int ClassField = 316, TypeField = 320;
    private const uint Class = 0x80805D1A;

    /** Ammo type names, Primary first. */
    public static readonly IReadOnlyList<string> Names = ["Primary", "Special", "Heavy"];

    /** True for an ammo type a weapon can have. */
    public static bool Valid(int ammo) => ammo >= 1 && ammo <= Names.Count;

    /** The ammo type's name; empty for none. */
    public static string Name(int ammo) => Valid(ammo) ? Names[ammo - 1] : "";

    /** The display's ammo type, 0 for none, or null when the display has no ammo classification. */
    public static int? ReadDisplay(ReadOnlySpan<byte> display) =>
        display.Length >= TypeField + 2 && BinaryPrimitives.ReadUInt32LittleEndian(display[ClassField..]) == Class
            ? BinaryPrimitives.ReadUInt16LittleEndian(display[TypeField..]) : null;

    /** An item's ammo type, or null when it has none or its display does not read. */
    public static int? Read(PackageCatalog packages, InvestmentItem item)
    {
        try
        {
            return item.DisplayTag != 0 && ReadDisplay(packages.Read(item.DisplayTag)) is { } ammo && Valid(ammo) ? ammo : null;
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException) { return null; }
    }

    public static byte[] SetDisplay(ReadOnlySpan<byte> display, int ammo)
    {
        if (ReadDisplay(display) is null) throw new InvalidDataException("The weapon display has no ammo classification.");
        byte[] output = display.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(TypeField), checked((ushort)ammo));
        return output;
    }
}
