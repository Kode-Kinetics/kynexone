namespace Zayra.Api.Application.Common;

public record PagedResult<T>(IReadOnlyCollection<T> Items, int Total, int Page, int PageSize);

public static class Paging
{
    /// <summary>
    /// The row offset of <paramref name="page"/> (1-based) for <paramref name="pageSize"/>, computed in 64 bits and capped at
    /// <see cref="int.MaxValue"/> (the type <c>Skip</c> takes). <c>(page - 1) * pageSize</c> in int arithmetic wraps negative for an
    /// absurd page (<c>page=2147483647</c>), which PostgreSQL rejects as "OFFSET must not be negative" (HTTP 500). Capped, such a
    /// page is simply past the last row: an empty page with the true total, like any other page beyond the end.
    /// </summary>
    public static int Skip(int page, int pageSize) =>
        (int)Math.Min(Math.Max(0L, ((long)page - 1) * pageSize), int.MaxValue);
}
