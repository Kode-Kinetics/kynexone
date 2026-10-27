using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Tests;

/// <summary>WP-C1: largest-remainder allocation (contract 2.6; T-013, T-014 allocator invariants, T-069, T-070, T-032).</summary>
public class LargestRemainderTests
{
    private static IReadOnlyList<(string Code, long Weight)> P(params (string, long)[] p) => p;

    private static Dictionary<string, long> Map(IReadOnlyList<(string? Code, long Amount)> r)
        => r.GroupBy(x => x.Code ?? "").ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

    [Fact]
    public void T013_SAR_10001_over_three_equal_parts_gives_3334_3334_3333_ties_by_code()
    {
        var r = LargestRemainder.Allocate(10001, P(("CC3", 1), ("CC1", 1), ("CC2", 1)));
        var m = Map(r);
        m["CC1"].Should().Be(3334);
        m["CC2"].Should().Be(3334);
        m["CC3"].Should().Be(3333);
        r.Sum(x => x.Amount).Should().Be(10001);
    }

    [Fact]
    public void T013_KWD_100001_fils_over_three_equal_parts_sums_exactly()
    {
        var r = LargestRemainder.Allocate(CurrencyMinorUnits.ToMinor(100.001m, "KWD"), P(("A", 1), ("B", 1), ("C", 1)));
        r.Select(x => x.Amount).OrderByDescending(x => x).Should().Equal(33334, 33334, 33333);
        r.Sum(x => x.Amount).Should().Be(100001);
    }

    [Fact]
    public void T013_rerun_100_times_is_identical()
    {
        var first = LargestRemainder.Allocate(10001, P(("B", 1), ("A", 1), ("C", 1)));
        for (var i = 0; i < 100; i++)
            LargestRemainder.Allocate(10001, P(("B", 1), ("A", 1), ("C", 1))).Should().Equal(first);
    }

    [Fact]
    public void Weighted_split_gives_largest_fractional_remainder_the_extra_unit()
    {
        // 10 over weights 1:2 -> exact 3.33 / 6.67 -> floors 3/6, rem 1 goes to B (0.67 > 0.33)
        var m = Map(LargestRemainder.Allocate(10, P(("A", 1), ("B", 2))));
        m["A"].Should().Be(3);
        m["B"].Should().Be(7);
    }

    [Fact]
    public void Zero_total_weight_returns_single_null_cost_centre_bucket()
    {
        var r = LargestRemainder.Allocate(500, P(("A", 0), ("B", 0)));
        r.Should().ContainSingle();
        r[0].Code.Should().BeNull();
        r[0].Amount.Should().Be(500);
    }

    [Fact]
    public void Empty_parts_returns_single_null_bucket()
    {
        var r = LargestRemainder.Allocate(500, P());
        r.Should().ContainSingle().Which.Should().Be(((string?)null, 500L));
    }

    [Fact]
    public void Zero_weight_part_receives_nothing_when_others_have_weight()
    {
        var m = Map(LargestRemainder.Allocate(7, P(("A", 0), ("B", 1), ("C", 1))));
        m["A"].Should().Be(0);
        (m["B"] + m["C"]).Should().Be(7);
    }

    [Fact]
    public void Single_bucket_gets_everything()
    {
        var r = LargestRemainder.Allocate(12345, P(("ONLY", 9)));
        r.Should().ContainSingle().Which.Should().Be(("ONLY", 12345L));
    }

    [Fact]
    public void Zero_total_gives_all_zero()
    {
        LargestRemainder.Allocate(0, P(("A", 1), ("B", 3))).Should().OnlyContain(x => x.Amount == 0);
    }

    [Fact]
    public void Negative_total_is_allocated_on_absolute_value_and_sign_reapplied()
    {
        var pos = Map(LargestRemainder.Allocate(10001, P(("A", 1), ("B", 1), ("C", 1))));
        var neg = Map(LargestRemainder.Allocate(-10001, P(("A", 1), ("B", 1), ("C", 1))));
        foreach (var k in pos.Keys) neg[k].Should().Be(-pos[k]); // T-070 void nets every bucket to zero
    }

    [Fact]
    public void Duplicate_codes_are_merged_deterministically()
    {
        var m = Map(LargestRemainder.Allocate(9, P(("A", 1), ("A", 1), ("B", 1))));
        m["A"].Should().Be(6);
        m["B"].Should().Be(3);
    }

    [Fact]
    public void Negative_weight_is_rejected()
    {
        var act = () => LargestRemainder.Allocate(10, P(("A", -1), ("B", 2)));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Very_large_values_do_not_overflow()
    {
        var m = Map(LargestRemainder.Allocate(long.MaxValue, P(("A", long.MaxValue), ("B", long.MaxValue - 1))));
        (m["A"] + m["B"]).Should().Be(long.MaxValue);
    }

    // ---- property style (seeded; no FsCheck in the test project) ----

    [Fact]
    public void T014_T069_properties_hold_over_seeded_random_inputs()
    {
        var rng = new Random(1337);
        for (var run = 0; run < 1000; run++)
        {
            var n = rng.Next(1, 13);
            var parts = Enumerable.Range(0, n)
                .Select(i => ($"CC{rng.Next(0, 1000):D4}_{i}", (long)(rng.Next(0, 4) == 0 ? 0 : rng.Next(1, 500_000))))
                .ToList();
            var total = rng.NextInt64(0, 5_000_000_000);
            var r = LargestRemainder.Allocate(total, parts);

            r.Sum(x => x.Amount).Should().Be(total, "sum(parts) == whole");
            r.Should().OnlyContain(x => x.Amount >= 0, "no negative part");

            var w = parts.Sum(p => p.Item2);
            if (w == 0) { r.Should().ContainSingle().Which.Code.Should().BeNull(); continue; }
            var m = Map(r);
            foreach (var (code, weight) in parts)
            {
                var exactShare = (double)((System.Numerics.BigInteger)total * weight) / w;
                Math.Abs(m[code] - exactShare).Should().BeLessThan(1.0 + 1e-6, "within one minor unit of exact share");
            }

            // order independence
            var shuffled = parts.OrderBy(_ => rng.Next()).ToList();
            Map(LargestRemainder.Allocate(total, shuffled)).Should().BeEquivalentTo(m);

            // negation symmetry
            var neg = Map(LargestRemainder.Allocate(-total, parts));
            foreach (var k in m.Keys) neg[k].Should().Be(-m[k]);
        }
    }

    [Fact]
    public void T032_order_independent_output_ordering_is_ordinal_by_code()
    {
        var a = LargestRemainder.Allocate(100, P(("B", 1), ("a", 1), ("C", 1)));
        var b = LargestRemainder.Allocate(100, P(("C", 1), ("B", 1), ("a", 1)));
        a.Should().Equal(b);
        a.Select(x => x.Code).Should().Equal("B", "C", "a"); // ordinal: uppercase before lowercase
    }
}
