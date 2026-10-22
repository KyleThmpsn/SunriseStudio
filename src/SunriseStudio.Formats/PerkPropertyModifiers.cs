using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** A change a perk's attached entity makes to one input of a component interface: an amount added, or a factor. */
public readonly record struct PerkPropertyModifier(ushort Component, ushort Input, bool Multiplies, float Amount);

/**
 * A perk's property modifiers. Its program names the object definitions it attaches, each definition constructs its
 * owners, and an owner keeps its modifier settings records in a typed array.
 */
public static class PerkPropertyModifiers
{
    // Settings records of 0x58 bytes: the amount at +0x28, the operation at +0x2C, 0 Add or 1 Multiply, the input at
    // +0x4A and the component interface at +0x4C. Any other operation leaves the input alone.
    private const uint RecordClass = 0x80803B06, ArrayMarker = 0x80809FBD;
    private const int RecordStride = 0x58, AmountField = 0x28, OperationField = 0x2C, InputField = 0x4A, ComponentField = 0x4C;
    private const byte Add = 0, Multiply = 1;

    /** Every Add or Multiply record the perk's attached entities hold, owner by owner; a display label holds none. */
    public static IReadOnlyList<PerkPropertyModifier> Read(PackageCatalog catalog, int perk)
    {
        if (!InvestmentSandboxPerkAuthoring.IsActive(catalog, perk)) return [];
        return PerkBehaviorCensus.Definitions(InvestmentSandboxPerkAuthoring.Read(catalog, perk).Gameplay)
            .Where(tag => catalog.TryGetEntry(tag, out var entry) && entry.Reference == PerkBehaviorCensus.ObjectDefinitionClass)
            .SelectMany(tag => WeaponConfigEdits.ConstructionConfigs(catalog.Read(tag))).Distinct()
            .SelectMany(owner => Records(catalog.Read(owner))).ToArray();
    }

    private static IEnumerable<PerkPropertyModifier> Records(byte[] owner)
    {
        var reader = new TagReader(owner);
        for (int header = 4; header + 16 <= owner.Length; header += 4)
        {
            if (reader.U32(header - 4) != ArrayMarker || reader.U32(header + 8) != RecordClass) continue;
            ulong count = reader.U64(header);
            if (count > (ulong)((owner.Length - header - 16) / RecordStride))
                throw new InvalidDataException("Property modifier records leave their owner.");
            for (int index = 0; index < (int)count; index++)
            {
                int row = header + 16 + index * RecordStride;
                byte operation = reader.U8(row + OperationField);
                if (operation is not (Add or Multiply)) continue;
                yield return new(reader.U16(row + ComponentField), reader.U16(row + InputField), operation == Multiply,
                    BinaryPrimitives.ReadSingleLittleEndian(owner.AsSpan(row + AmountField)));
            }
        }
    }
}
