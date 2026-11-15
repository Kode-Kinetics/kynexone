using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AdvanceTestKit;
using static Zayra.Api.Tests.AdvancePayoutLifecycleTests;

namespace Zayra.Api.Tests;

/// <summary>
/// Every refusal from the salary-advance endpoints carries a machine code and the sentence in English and Arabic (message,
/// messageAr), so the screen can show it in the reader's language. These are the refusals that used to be plain English strings.
/// </summary>
public class AdvanceBilingualRefusalTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string[] Hr = { "HR Manager" };

    private static CreateAdvanceRequest Request(Fx f, decimal amount, int installments = 2) =>
        new(f.EmployeePublicId, "Huda Saleh", amount, "Installments", installments, null, "family", f.EmployeeId);

    [Fact]
    public async Task CreateRefusals_AreBilingual()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 0m), Ct), "advance_amount_invalid");
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m, installments: 9), Ct), "advance_installments_invalid");
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 9_000m), Ct), "advance_salary_cap");
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), new[] { "Employee" }, perms: new[] { "loans.self" })
            .Create(new CreateAdvanceRequest(Guid.Empty, "", 500m, "OneTime", 1, null, "rent"), Ct), "ess_link_missing");
    }

    [Fact]
    public async Task QuotaServiceAndCooldownRefusals_AreBilingual()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var employee = await db.Employees.SingleAsync();
        employee.JoiningDate = DateTime.UtcNow.AddMonths(-2);   // policy needs 6 service months
        await db.SaveChangesAsync();
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 500m), Ct), "advance_min_service");

        employee.JoiningDate = DateTime.UtcNow.AddYears(-3);
        await db.SaveChangesAsync();
        db.SalaryAdvances.Add(new SalaryAdvance
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeePublicId, EmployeeIntId = f.EmployeeId,
            AdvanceNumber = "ADV-OLD", Status = "Closed", ApprovedAmount = 500m, TotalRepaid = 500m,
            CreatedAtUtc = DateTime.UtcNow.AddYears(-1), UpdatedAtUtc = DateTime.UtcNow.AddDays(-5),
        });
        await db.SaveChangesAsync();
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 500m), Ct), "advance_cooldown");

        (await db.SalaryAdvances.SingleAsync()).UpdatedAtUtc = DateTime.UtcNow.AddYears(-1);
        await db.SaveChangesAsync();
        await SeedPendingAsync(db, f, 300m);
        await SeedPendingAsync(db, f, 300m);
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 300m), Ct), "advance_yearly_limit");
    }

    [Fact]
    public async Task DecisionRefusals_AreBilingual()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var requester = Guid.NewGuid();
        var id = await SeedPendingAsync(db, f, 2_000m);
        (await db.SalaryAdvances.SingleAsync()).CreatedBy = requester;
        await db.SaveChangesAsync();

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, requester, Hr).Approve(id, new AdvanceApproveRequest(1_000m, 2, null), Ct), "advance_maker_is_checker");
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, f.EmployeeUserId, Hr).Approve(id, new AdvanceApproveRequest(1_000m, 2, null), Ct), "advance_subject_is_decider");
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(id, new AdvanceApproveRequest(2_000.01m, 2, null), Ct), "advance_amount_above_request");
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(id, new AdvanceApproveRequest(-1m, 2, null), Ct), "advance_amount_invalid");

        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(id, new AdvanceApproveRequest(1_000m, 2, null), Ct));
        db.ChangeTracker.Clear();
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(id, new AdvanceApproveRequest(1_000m, 2, null), Ct), "invalid_advance_state");
        AssertRefusal<ConflictObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Reject(id, new RejectRequest("late"), Ct), "invalid_advance_state");
        AssertRefusal<ConflictObjectResult>(await Advances(db, f, Guid.NewGuid(), new[] { "Finance" })
            .MarkInstallmentPaid(id, Guid.NewGuid(), new PayAdvanceInstallmentRequest(100m, DateOnly.FromDateTime(DateTime.UtcNow), null), Ct),
            "advance_installment_manual_payment_refused");
    }

    [Fact]
    public async Task PolicyRefusal_IsBilingual()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        var result = await Advances(db, f, Guid.NewGuid(), new[] { "Admin" }, perms: new[] { "loans.write" })
            .UpsertPolicy(new AdvancePolicyRequest("Bad", 150m, 2, 0, true, 3, 0, true), Ct);

        AssertRefusal<BadRequestObjectResult>(result, "advance_policy_invalid");
        Assert.Contains("0-100", JsonSerializer.Serialize(((ObjectResult)result).Value));
    }
}
