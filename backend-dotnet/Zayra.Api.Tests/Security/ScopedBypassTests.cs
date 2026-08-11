using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Wave 0 — coverage for the approved query-filter bypass abstraction.
///
/// <para>These exist because the independent security review found the helper had shipped with ZERO
/// tests and carried a real ordering defect: <c>SystemWide</c> returned
/// <c>set.IgnoreQueryFilters().Take(n)</c> and expected the caller to append a <c>Where</c>. EF composes
/// that as <c>SELECT * FROM (SELECT * FROM t LIMIT n) WHERE ...</c> — an arbitrary n rows chosen FIRST
/// and filtered afterwards. With more rows in the table than the batch size, the sweep matches almost
/// nothing, so the "bounded batch" presented as a safety control was silently a correctness bug.
/// <c>ClaimsMatchingRows_EvenWhenNonMatchingRowsOutnumberTheBatch</c> is the regression test for it.</para>
/// </summary>
public class ScopedBypassTests
{
    private const string Why = "Test justification long enough to satisfy the twenty-character floor.";

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── SystemWide: filter, then order, then bound ────────────────────────────

    [Fact]
    public void ClaimsMatchingRows_EvenWhenNonMatchingRowsOutnumberTheBatch()
    {
        using var db = NewDb();
        var now = DateTime.UtcNow;

        // 200 rows that must NOT be swept, inserted first so a naive Take(50) would capture only these.
        for (var i = 0; i < 200; i++)
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                TenantId = Guid.NewGuid(), Outcome = DeliveryOutcomes.Queued,
                LeaseExpiresAtUtc = null,
            });
        // 3 genuinely stuck rows with expired leases, last in insertion order.
        for (var i = 0; i < 3; i++)
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                TenantId = Guid.NewGuid(), Outcome = DeliveryOutcomes.Sending,
                LeaseExpiresAtUtc = now.AddMinutes(-(i + 1)),
            });
        db.SaveChanges();

        var swept = ScopedBypass.SystemWide(db.NotificationDeliveries, 50, Why,
                d => d.Outcome == DeliveryOutcomes.Sending
                  && d.LeaseExpiresAtUtc != null && d.LeaseExpiresAtUtc < now,
                d => d.LeaseExpiresAtUtc!)
            .ToList();

        swept.Should().HaveCount(3,
            "the bound must apply AFTER the predicate — filtering a pre-truncated window would sweep none");
    }

    [Fact]
    public void OrdersOldestFirst_SoABacklogDrainsInAgeOrder()
    {
        using var db = NewDb();
        var now = DateTime.UtcNow;
        foreach (var minutes in new[] { 5, 60, 30 })
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                TenantId = Guid.NewGuid(), Outcome = DeliveryOutcomes.Sending,
                LeaseExpiresAtUtc = now.AddMinutes(-minutes),
            });
        db.SaveChanges();

        var swept = ScopedBypass.SystemWide(db.NotificationDeliveries, 2, Why,
                d => d.Outcome == DeliveryOutcomes.Sending,
                d => d.LeaseExpiresAtUtc!)
            .ToList();

        swept.Should().HaveCount(2);
        swept[0].LeaseExpiresAtUtc.Should().BeBefore(swept[1].LeaseExpiresAtUtc!.Value,
            "the earliest-stuck rows must drain first or they starve behind newer ones forever");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void RefusesAnUnboundedOrAbsurdBatch(int batchSize)
    {
        using var db = NewDb();
        var act = () => ScopedBypass.SystemWide(db.NotificationDeliveries, batchSize, Why,
            d => true, d => d.Id);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "an unbounded cross-tenant sweep lets one tenant's backlog stall every other tenant");
    }

    // ── Justification floor ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("because")]
    public void RefusesAnEmptyOrPlaceholderJustification(string justification)
    {
        using var db = NewDb();
        var act = () => ScopedBypass.SystemWide(db.NotificationDeliveries, 10, justification,
            d => true, d => d.Id);

        act.Should().Throw<ArgumentException>(
            "an unexplained bypass is precisely what this abstraction exists to prevent");
    }

    // ── ForCompanies: the user-triggered helper must fail closed ──────────────

    [Fact]
    public void ForCompanies_WithNoAuthorisedCompanies_ReturnsNothing()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = tenantId, CompanyId = companyId });
        db.SaveChanges();

        var rows = ScopedBypass.ForCompanies(db.EmployeeFinalSettlements, tenantId, Array.Empty<Guid>(), Why).ToList();

        rows.Should().BeEmpty(
            "an empty authorised-company set must fail CLOSED — returning everything would invert the control");
    }

    [Fact]
    public void ForCompanies_ExcludesOtherTenantsAndOtherCompanies()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = tenantId, CompanyId = mine });
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = tenantId, CompanyId = theirs });
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = Guid.NewGuid(), CompanyId = mine });
        db.SaveChanges();

        var rows = ScopedBypass.ForCompanies(db.EmployeeFinalSettlements, tenantId, new[] { mine }, Why).ToList();

        rows.Should().HaveCount(1, "only the caller's own tenant AND authorised company may be returned");
        rows[0].CompanyId.Should().Be(mine);
    }

    [Fact]
    public void ForCompanies_ExcludesUnattributedRowsByDefault()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = tenantId, CompanyId = mine });
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = tenantId, CompanyId = null });
        db.SaveChanges();

        ScopedBypass.ForCompanies(db.EmployeeFinalSettlements, tenantId, new[] { mine }, Why)
            .ToList().Should().HaveCount(1,
                "on an operational table a null company is a backfill transient, not 'shared with everyone'");

        ScopedBypass.ForCompanies(db.EmployeeFinalSettlements, tenantId, new[] { mine }, Why, includeUnattributed: true)
            .ToList().Should().HaveCount(2, "opting in must be explicit");
    }

    // ── TenantWide: company dropped, tenant never ─────────────────────────────
    // Uses EmployeeFinalSettlement: a genuinely ICompanyScopedOperational record. GlJournalExport is
    // deliberately NOT company-scoped (its CompanyId is the filter an export was taken under, and NULL
    // means "group-wide"), so it cannot exercise the company-scoped helpers.

    [Fact]
    public void TenantWide_SpansCompaniesButNeverTenants()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = tenantId, CompanyId = Guid.NewGuid() });
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = tenantId, CompanyId = Guid.NewGuid() });
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement { TenantId = Guid.NewGuid(), CompanyId = Guid.NewGuid() });
        db.SaveChanges();

        var rows = ScopedBypass.TenantWide(db.EmployeeFinalSettlements, tenantId, Why).ToList();

        rows.Should().HaveCount(2, "every entity in the tenant, and nothing outside it");
        rows.Should().OnlyContain(r => r.TenantId == tenantId);
    }
}
