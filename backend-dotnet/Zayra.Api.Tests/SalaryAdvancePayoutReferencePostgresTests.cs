using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AdvanceTestKit;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// One bank or cash reference evidences one payout in a company. The controller checks that before it pays, but a check and the
/// write that follows are not one step: two different advances paid at the same moment with the same reference each read "no one
/// has used it" and both committed. The advance lock does not help, it is per advance. The database now refuses the second:
/// a partial unique index on (tenant, company, payment reference), with a companion for the legacy advance that has no company
/// (NULL is distinct in a unique index, so the first index alone would let two unattributed advances share a reference).
/// Real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SalaryAdvancePayoutReferencePostgresTests(PostgresFixture fixture)
{
    private static readonly string[] FinanceRole = { "Finance" };

    /// <summary>The first payout has written its reference but not committed when a second advance is paid with the same
    /// reference. The second must wait for the first (the database holds the reference), then be refused with the existing
    /// reference-used 409; exactly one advance is paid and one journal is posted.</summary>
    [Fact]
    public async Task TwoDifferentAdvancesPaidAtOnceWithTheSameReference_OnlyOneSucceeds_TheOtherIsRefusedWith409()
    {
        await using var seed = fixture.CreateDb();
        var f = await SeedAsync(seed);
        var first = await SeedApprovedAsync(seed, f, "ADV-REF-1");
        var second = await SeedApprovedAsync(seed, f, "ADV-REF-2");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var dbA = fixture.CreateDb();
        await using var dbB = fixture.CreateDb();
        var pidB = await Kit.OpenAndPidAsync(dbB, timeout.Token);

        IActionResult? resultA = null, resultB = null;
        var waited = false;
        await dbA.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await dbA.Database.BeginTransactionAsync(timeout.Token);
            resultA = await Advances(dbA, f, Guid.NewGuid(), FinanceRole).Payout(first,
                new AdvancePayoutRequest(null, "BANK-DUP-1", null), timeout.Token);
            var racing = Task.Run(() => Advances(dbB, f, Guid.NewGuid(), FinanceRole).Payout(second,
                new AdvancePayoutRequest(null, "BANK-DUP-1", null), timeout.Token));
            waited = await Kit.WaitForLockAsync(fixture, pidB, advisoryOnly: false, racing, timeout.Token);
            await tx.CommitAsync(timeout.Token);
            resultB = await racing;
        });

        Assert.IsType<OkObjectResult>(resultA);
        Assert.True(waited, "the second payout must reach the reference the first one holds before the first commits");
        var refusal = Assert.IsType<ConflictObjectResult>(resultB);
        Assert.Contains("advance_payout_reference_used", System.Text.Json.JsonSerializer.Serialize(refusal.Value));
        await using var verify = fixture.CreateDb();
        var rows = await verify.SalaryAdvances.AsNoTracking().Where(x => x.TenantId == f.TenantId).ToListAsync();
        Assert.Equal("Active", rows.Single(x => x.Id == first).Status);
        Assert.Equal("Approved", rows.Single(x => x.Id == second).Status);
        Assert.Null(rows.Single(x => x.Id == second).PaymentReference);
        Assert.Single(await verify.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == f.TenantId && x.SourceModule == "Advance" && x.EventType == "Disbursement").ToListAsync());
    }

    /// <summary>The same reference in another company is another bank's evidence: allowed. A reference on no advance (NULL) is
    /// not a reference at all: any number of advances carry none. The same reference twice in one company, or twice among the
    /// advances that have no company, is refused by the database.</summary>
    [Fact]
    public async Task TheIndexes_RefuseTheSameReferenceTwiceInACompany_AndAmongUnattributedAdvances_ButNotAcrossCompaniesOrForNoReference()
    {
        var tenant = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        await using (var db = fixture.CreateDb())
        {
            db.SalaryAdvances.AddRange(
                Row(tenant, companyA, "REF-1"), Row(tenant, companyB, "REF-1"),
                Row(tenant, companyA, null), Row(tenant, companyA, null),
                Row(tenant, null, null), Row(tenant, null, null),
                Row(tenant, null, "REF-1"), Row(Guid.NewGuid(), companyA, "REF-1"));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDb())
        {
            db.SalaryAdvances.Add(Row(tenant, companyA, "REF-1"));
            var sameCompany = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)sameCompany.InnerException!).SqlState);
        }
        await using (var db = fixture.CreateDb())
        {
            db.SalaryAdvances.Add(Row(tenant, null, "REF-1"));
            var unattributed = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)unattributed.InnerException!).SqlState);
        }
    }

    private static SalaryAdvance Row(Guid tenant, Guid? company, string? reference) => new()
    {
        TenantId = tenant, CompanyId = company, EmployeeId = Guid.NewGuid(), EmployeeName = "Ref", AdvanceNumber = $"ADV-{Guid.NewGuid():N}",
        RequestedAmount = 100m, ApprovedAmount = 100m, Status = "Active", PaymentReference = reference,
    };

    private static async Task<Guid> SeedApprovedAsync(Zayra.Api.Data.ZayraDbContext db, Fx f, string number)
    {
        var advance = new SalaryAdvance
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeePublicId, EmployeeIntId = f.EmployeeId,
            EmployeeName = "Huda Saleh", AdvanceNumber = number, RequestedAmount = 1_000m, ApprovedAmount = 1_000m,
            RepaymentType = "Installments", Installments = 2, InstallmentAmount = 500m, Status = "Approved",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-2), CreatedBy = Guid.NewGuid(),
        };
        db.SalaryAdvances.Add(advance);
        await db.SaveChangesAsync();
        return advance.Id;
    }
}
