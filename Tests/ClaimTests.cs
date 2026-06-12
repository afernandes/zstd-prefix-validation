using Xunit;
using Zstd129214;

namespace Zstd129214.Tests;

/// <summary>
/// Each test pins one numeric fact cited in the dotnet/runtime#129214 discussion,
/// at 96 MiB scale (affordable in test time). Self-delta: compress a buffer using
/// itself as the prefix — an effective prefix produces ~0% output.
/// </summary>
public class ClaimTests
{
    [Fact]
    public void Quality15_Prefix96Mi_IsEffective()
    {
        var (pct, _) = Experiments.SelfDeltaManaged(15, 96);
        Assert.True(pct < 0.05, $"q15 should reference the prefix (expected ~0.011%), got {pct:F3}%");
    }

    [Fact]
    public void Quality19_Prefix96Mi_IsIgnored_TheOriginalBug()
    {
        var (pct, _) = Experiments.SelfDeltaManaged(19, 96);
        Assert.True(pct > 99, $"q19 should ignore the 96 MiB prefix (expected ~100%), got {pct:F3}%");
    }

    [Fact]
    public void Quality3_Prefix96Mi_MidEffectiveness()
    {
        var (pct, _) = Experiments.SelfDeltaManaged(3, 96);
        Assert.True(pct is > 0.1 and < 0.3, $"q3 expected ~0.155%, got {pct:F3}%");
    }

    [Fact]
    public void Quality19_HashLog27_Native_FixesThePrefix()
    {
        var prefix = new byte[96 << 20];
        new Random(42).NextBytes(prefix);
        var target = (byte[])prefix.Clone();
        var (delta, _, roundTrip, _, _) = Experiments.RealNative(prefix, target, quality: 19, hashLog: 27);
        double pct = delta * 100.0 / target.Length;
        Assert.True(roundTrip, "native round-trip failed");
        Assert.True(pct < 0.05, $"q19+hashLog27 should index the whole prefix (expected ~0.008%), got {pct:F3}%");
    }
}
