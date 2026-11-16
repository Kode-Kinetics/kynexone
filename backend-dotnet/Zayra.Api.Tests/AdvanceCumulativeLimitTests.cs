using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AdvanceTestKit;
using static Zayra.Api.Tests.AdvancePayoutLifecycleTests;

namespace Zayra.Api.Tests;

/// <summary>
/// Decision G2: the policy's percentage-of-salary cap applies to the cumulative total of the new amount and what the employee
/// already owes or has asked for (Pending requests, Approved advances awaiting payout, the outstanding balance of Active ones),
/// refused with a bilingual advance_cumulative_limit that names the headroom left.
/// </summary>
public class AdvanceCumulativeLimitTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly string[] Hr = { "HR Manager" };

    private static CreateAdvanceRequest Request(Fx f, decimal amount, int installments = 2, DateOnly? start = null) =>
        new(f.EmployeePublicId, "Huda Saleh", amount, "Installments", installments, start, "family", f.EmployeeId);

    // ── G2: the salary cap covers everything the employee owes or has asked for ─────────────────────

    [Fact]
    public async Task Create_WhenTheNewRequestAndWhatIsAlreadyOwedExceedTheCap_IsRefused_NamingTheHeadroom()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);            // wage 10,000, cap 50% = 5,000
        var live = await SeedPendingAsync(db, f, 3_000m);
        var adv = await db.SalaryAdvances.SingleAsync(x => x.Id == live);
        adv.Status = "Active"; adv.ApprovedAmount = 3_000m; adv.OutstandingBalance = 3_000m;
        await db.SaveChangesAsync();

        var over = await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 2_000.01m), Ct);

        AssertRefusal<BadRequestObjectResult>(over, "advance_cumulative_limit");
        Assert.Contains("2000.00", System.Text.Json.JsonSerializer.Serialize(((ObjectResult)over).Value));
        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 2_000m), Ct));
    }

    [Fact]
    public async Task Create_CountsPendingRequestsAndApprovedAdvancesAwaitingPayout()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        (await db.AdvancePolicies.SingleAsync()).MaxAdvancesPerYear = 5;   // so the yearly quota is not what refuses
        await db.SaveChangesAsync();
        await SeedPendingAsync(db, f, 2_000m);                       // asked for, not yet decided
        var approved = await SeedPendingAsync(db, f, 2_500m);
        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(approved, new AdvanceApproveRequest(2_000m, 2, null), Ct));
        db.ChangeTracker.Clear();
        // Owed or asked for: 2,000 pending + 2,000 approved = 4,000 of the 5,000 cap.

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000.01m), Ct), "advance_cumulative_limit");
    }

    [Fact]
    public async Task Create_IgnoresRejectedCancelledAndClosedAdvances_InTheCumulativeCap()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        // SaveChanges stamps UpdatedAtUtc, so a Closed row always looks recently settled: switch the cooldown off, it is not
        // what this test is about.
        (await db.AdvancePolicies.SingleAsync()).CooldownMonths = 0;
        foreach (var status in new[] { "Rejected", "Cancelled", "Closed" })
            db.SalaryAdvances.Add(new SalaryAdvance
            {
                TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeePublicId, EmployeeIntId = f.EmployeeId,
                EmployeeName = "Huda Saleh", AdvanceNumber = $"ADV-X-{status}", RequestedAmount = 4_000m,
                ApprovedAmount = status == "Closed" ? 4_000m : 0m, TotalRepaid = status == "Closed" ? 4_000m : 0m,
                Status = status, CreatedAtUtc = DateTime.UtcNow.AddYears(-1), UpdatedAtUtc = DateTime.UtcNow.AddYears(-1),
            });
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 5_000m), Ct));
    }

    [Fact]
    public async Task Approve_RechecksTheCumulativeCap_CountingTheOtherOpenRequests()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);                          // cap 5,000
        var first = await SeedPendingAsync(db, f, 3_000m);
        var second = await SeedPendingAsync(db, f, 3_500m);   // both raised before the cumulative rule

        // 3,000 with the other request's 3,500 still open is over the cap; the checker may lower it.
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(first, new AdvanceApproveRequest(3_000m, 2, null), Ct),
            "advance_cumulative_limit");
        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(first, new AdvanceApproveRequest(1_500m, 2, null), Ct));
        db.ChangeTracker.Clear();

        // The second is then judged against the first's approved 1,500, not its original 3,000 request: 1,500 + 3,500 fits the
        // 5,000 cap exactly (3,000 + 3,500 would not).
        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(second, new AdvanceApproveRequest(3_500m, 2, null), Ct));
    }

}
