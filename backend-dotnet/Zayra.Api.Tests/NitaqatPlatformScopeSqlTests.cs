using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// The in-memory provider treats `x.TenantId == null` as plain LINQ, where null
/// equals null. PostgreSQL does not: `col = NULL` is never true. Every Nitaqat
/// reference read reaches the platform-default rows through
/// ScopedBypass.NullableTenantWide(set, null, …), where the null arrives as a
/// PARAMETER rather than a literal — so if EF's null-semantics rewriting did not
/// kick in, every read would silently return nothing and every establishment on a
/// real deployment would be refused a band forever while the whole unit suite
/// stayed green.
///
/// This asserts the actual Npgsql SQL. No database connection is needed —
/// ToQueryString compiles the query without executing it.
/// </summary>
public class NitaqatPlatformScopeSqlTests
{
    [Fact]
    public void PlatformDefaultReads_CompileToAnIsNullPredicate_NotEqualsNull()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=u;Password=p")
            .Options;

        using var db = new ZayraDbContext(options);

        const string why =
            "Compiling the platform-default reference read to inspect its SQL; never executed.";

        var sql = ScopedBypass
            .NullableTenantWide(db.NitaqatBandThresholds, null, why)
            .ToQueryString();

        sql.Should().Contain("IS NULL",
            "a platform-default row lives under TenantId = NULL, and `TenantId = @p` with a null "
            + "parameter matches zero rows on PostgreSQL");
        sql.Should().NotMatchRegex(@"TenantId""?\s*=\s*@",
            "a bare parameter equality would silently hide every platform-default row");
    }

    [Fact]
    public void TenantScopedReferenceReads_StillPinTheTenant()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=u;Password=p")
            .Options;

        using var db = new ZayraDbContext(options);

        var sql = ScopedBypass
            .NullableTenantWide(db.NitaqatBandThresholds, Guid.NewGuid(),
                "Compiling the tenant-override reference read to inspect its SQL; never executed.")
            .ToQueryString();

        sql.Should().Contain("tenant_id", "the tenant predicate must survive the bypass");
    }
}
