using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** A weapon's fixed damage type is one marker sandbox perk among its perks; a Kinetic weapon carries none. */
public static class InvestmentDamageType
{
    /** Choice values: Kinetic, then the three elements in marker order. */
    public static readonly string[] Names = ["Kinetic", "Arc", "Solar", "Void"];
    public const int Kinetic = 0;
    // Marker perk hashes for Arc, Solar and Void: the family launch weapons carry, then the later one.
    private static readonly uint[][] Markers = [[0xB0C2E8FA, 0x30D3A473, 0x4F978D3C], [0xCCC507A5, 0xCFCF0160, 0x10A9B235]];

    /** True when the installation holds every marker perk. */
    public static bool Installed(InvestmentCatalog investment) =>
        Markers.SelectMany(family => family).All(hash => investment.PerkDefinitions.Any(perk => perk.Hash == hash));

    /** The family and damage type of a marker perk, or null for any other perk. */
    public static (int Family, int Type)? Marker(InvestmentCatalog investment, int perk)
    {
        if (perk < 0 || perk >= investment.PerkCount) return null;
        uint hash = investment.PerkDefinitions[perk].Hash;
        for (int family = 0; family < Markers.Length; family++)
        {
            int element = Array.IndexOf(Markers[family], hash);
            if (element >= 0) return (family, element + 1);
        }
        return null;
    }

    // A weapon with a socket of this type takes its damage type from the socket's plug.
    public const ushort DamageSocketType = 68;

    /**
     * A weapon's damage type: its damage socket's plug's marker perk when it has that socket, else its own; null when
     * the perks hold more than one marker.
     */
    public static int? ReadWeapon(InvestmentCatalog investment, PackageCatalog packages, InvestmentItem item)
    {
        try
        {
            var detail = investment.ReadItem(item.Tag, packages.Read(item.Tag));
            var perks = detail.Perks;
            if (detail.Sockets.FirstOrDefault(socket => socket.Type == DamageSocketType) is { } socket)
            {
                if (socket.InitialPlug >= investment.Items.Count) return null;
                var plug = investment.Items[socket.InitialPlug];
                perks = investment.ReadItem(plug.Tag, packages.Read(plug.Tag)).Perks;
            }
            return Read(investment, [.. perks.Select(perk => (int)perk.Index)]);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException) { return null; }
    }

    /** The damage type a weapon's own perks fix, or null when they carry more than one marker. */
    public static int? Read(InvestmentCatalog investment, IReadOnlyCollection<int> perks)
    {
        var markers = perks.Select(perk => Marker(investment, perk)).OfType<(int Family, int Type)>().ToArray();
        return markers.Length switch { 0 => Kinetic, 1 => markers[0].Type, _ => null };
    }
}
