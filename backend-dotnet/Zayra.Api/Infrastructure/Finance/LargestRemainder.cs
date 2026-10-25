using System.Numerics;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// Largest-remainder allocation of an integer minor-unit total across weighted cost centres
/// (cost-accounting contract 2.6). The parts always sum to the total exactly. Ties on the fractional
/// remainder go to the lower Code (ordinal). Output is ordered by Code (ordinal) so it does not depend
/// on input order. A negative total is allocated on its absolute value and the sign re-applied, so a
/// reversal nets every bucket to zero. Duplicate codes are merged. When there is nothing to weight by
/// (no parts, or all weights zero) the whole amount goes to a single bucket with a null Code.
/// </summary>
public static class LargestRemainder
{
    public static IReadOnlyList<(string? Code, long Amount)> Allocate(
        long totalMinor, IReadOnlyList<(string Code, long Weight)> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        foreach (var p in parts)
            if (p.Weight < 0) throw new ArgumentOutOfRangeException(nameof(parts), "Weights must be >= 0.");

        var merged = parts
            .GroupBy(p => p.Code, StringComparer.Ordinal)
            .Select(g => (Code: g.Key, Weight: g.Aggregate(BigInteger.Zero, (a, p) => a + p.Weight)))
            .OrderBy(p => p.Code, StringComparer.Ordinal)
            .ToList();

        BigInteger w = merged.Aggregate(BigInteger.Zero, (a, p) => a + p.Weight);
        if (w.IsZero) return new[] { ((string?)null, totalMinor) };

        var sign = totalMinor < 0 ? -1 : 1;
        BigInteger abs = BigInteger.Abs(totalMinor);

        var rows = merged.Select(p =>
        {
            var num = abs * p.Weight;
            var floor = BigInteger.DivRem(num, w, out var remainder);
            return (p.Code, Floor: floor, Remainder: remainder);
        }).ToList();

        var left = (int)(abs - rows.Aggregate(BigInteger.Zero, (a, r) => a + r.Floor));
        var bonus = rows
            .OrderByDescending(r => r.Remainder)   // stable: ties keep the ordinal-code order
            .Take(left)
            .Select(r => r.Code)
            .ToHashSet(StringComparer.Ordinal);

        return rows
            .Select(r => ((string?)r.Code, sign * (long)(r.Floor + (bonus.Contains(r.Code) ? 1 : 0))))
            .ToList();
    }
}
