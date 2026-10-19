namespace SunriseStudio.Formats;

/**
 * An item definition's socket rows, which +104 points to: 80 bytes each, the initial plug at +2, the reusable and
 * randomized plug sets at +12 and +32, and the embedded members at +64, which share the plug set member layout.
 */
internal static class InvestmentSocketRows
{
    public const int Field = 104, Stride = 80, InitialPlug = 2, ReusableSet = 12, RandomizedSet = 32, Members = 64;
    public const uint RowClass = 0x808077C4;
}
