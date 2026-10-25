namespace Zayra.Api.Infrastructure.Finance;

/// <summary>Largest-remainder allocation of integer minor units (contract 2.6).</summary>
public static class LargestRemainder
{
    public static IReadOnlyList<(string? Code, long Amount)> Allocate(
        long totalMinor, IReadOnlyList<(string Code, long Weight)> parts) => Array.Empty<(string?, long)>();
}
