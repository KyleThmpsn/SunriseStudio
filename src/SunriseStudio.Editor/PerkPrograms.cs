using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/**
 * The sandbox perk each perk gameplay program runs, so a program's browser row can show that perk's icon and tooltip.
 * A program several perks run takes the first of them. Read once per investment catalog.
 */
internal static class PerkPrograms
{
    private static readonly ConditionalWeakTable<InvestmentCatalog, Dictionary<uint, int>> Cache = [];

    /** The sandbox perk the program tag runs, or null for any other tag or a program no perk names. */
    public static int? PerkOf(PackageCatalog packages, InvestmentCatalog investment, uint program) =>
        Cache.GetValue(investment, _ => Read(packages)).TryGetValue(program, out int perk) ? perk : null;

    /** Each perk's gameplay name resolved through the runtime name map; a perk that names no program is left out. */
    private static Dictionary<uint, int> Read(PackageCatalog packages)
    {
        var perks = new Dictionary<uint, int>();
        if (!packages.ContainsTag(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag)) return perks;
        try
        {
            byte[] names = packages.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag);
            foreach (var display in SandboxPerkDisplays.Read(packages))
                try
                {
                    perks.TryAdd(InvestmentSandboxPerkAuthoring.ResolveRuntimeName(names, display.GameplayHash), display.Index);
                }
                catch (InvalidDataException) { }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { }
        return perks;
    }
}
