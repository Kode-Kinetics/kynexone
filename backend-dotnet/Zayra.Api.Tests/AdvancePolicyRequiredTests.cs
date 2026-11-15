using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AdvanceTestKit;
using static Zayra.Api.Tests.AdvancePayoutLifecycleTests;

namespace Zayra.Api.Tests;

/// <summary>
/// Audit P2-5: with no active advance policy every amount used to be allowed (only the 60-instalment hard cap applied). A request
/// now needs an active policy and is refused with a bilingual advance_policy_required otherwise.
/// </summary>
public class AdvancePolicyRequiredTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly string[] Hr = { "HR Manager" };

    private static CreateAdvanceRequest Request(Fx f, decimal amount, int installments = 2, DateOnly? start = null) =>
        new(f.EmployeePublicId, "Huda Saleh", amount, "Installments", installments, start, "family", f.EmployeeId);

    // ── Item 7: no policy, no advance ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithNoActivePolicy_IsRefused_WithABilingualCode()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, withPolicy: false);

        var result = await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m), Ct);

        AssertRefusal<BadRequestObjectResult>(result, "advance_policy_required");
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Create_WithOnlyAnInactivePolicy_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        (await db.AdvancePolicies.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m), Ct), "advance_policy_required");
    }

}
