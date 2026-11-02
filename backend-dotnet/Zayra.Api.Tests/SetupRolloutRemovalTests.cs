using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The rest of Setup follows the one removal rule (never used = deleted, used = turned off and restorable, a refusal says why):
/// loan types, bonus types, scorecards, onboarding checklists, shifts, payslip templates, and the capabilities map.
/// </summary>
public class SetupRolloutRemovalTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static T Wire<T>(T c, Guid tenant, params string[] permissions) where T : ControllerBase
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"), new("is_group_scope", "true"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return c;
    }

    private sealed class AllScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);
    private static string Outcome(IActionResult r) => Body(r).GetProperty("outcome").GetString()!;

    // ── Loan types ──────────────────────────────────────────────────────────────────────

    private static LoanTypeRequest LoanReq(string code = "PERSONAL", string name = "Personal loan", decimal max = 5000) =>
        new(code, name, null, max, 12, "Monthly", true, 0, 6, true);

    [Fact]
    public async Task Loan_type_edit_changes_limits_and_an_unused_code_but_not_a_used_code()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var t = new LoanType { TenantId = tenant, Code = "PERSONAL", NameEn = "Personal loan", MaxAmount = 1000 };
        db.LoanTypes.Add(t); await db.SaveChangesAsync();
        var ctl = Wire(new LoansController(db, new AllScope()), tenant, "loans.write");

        (await ctl.UpdateLoanType(t.Id, LoanReq("RENAMED", "Personal loan", 9000), default)).Should().BeOfType<OkObjectResult>();
        var saved = await db.LoanTypes.SingleAsync();
        saved.MaxAmount.Should().Be(9000);
        saved.Code.Should().Be("RENAMED");

        db.EmployeeLoans.Add(new EmployeeLoan { TenantId = tenant, LoanTypeId = t.Id });
        await db.SaveChangesAsync();
        var refused = await ctl.UpdateLoanType(t.Id, LoanReq("CHANGED-AGAIN"), default);
        refused.Should().BeOfType<ConflictObjectResult>();
        Body(refused).GetProperty("code").GetString().Should().Be("LOAN_TYPE_CODE_IN_USE");
        (await db.LoanTypes.SingleAsync()).Code.Should().Be("RENAMED");
    }

    [Fact]
    public async Task Loan_type_remove_always_turns_off_and_names_the_reason_when_a_loan_used_it()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var unused = new LoanType { TenantId = tenant, Code = "A", NameEn = "A" };
        var used = new LoanType { TenantId = tenant, Code = "B", NameEn = "B" };
        db.LoanTypes.AddRange(unused, used);
        db.EmployeeLoans.Add(new EmployeeLoan { TenantId = tenant, LoanTypeId = used.Id });
        await db.SaveChangesAsync();
        var ctl = Wire(new LoansController(db, new AllScope()), tenant, "loans.write");

        // A loan type is always turned off, never erased (grade limits derive their facility from its code), and restorable.
        Outcome(await ctl.RemoveLoanType(unused.Id, default)).Should().Be("turnedOff");
        (await db.LoanTypes.SingleAsync(x => x.Id == unused.Id)).IsDeleted.Should().BeFalse();
        Outcome(await ctl.RestoreLoanType(unused.Id, default)).Should().Be("restored");

        var off = await ctl.RemoveLoanType(used.Id, default);
        Outcome(off).Should().Be("turnedOff");
        Body(off).GetProperty("usage").GetProperty("summary").GetString().Should().Contain("1 loans");
        (await db.LoanTypes.SingleAsync(x => x.Id == used.Id)).IsActive.Should().BeFalse();

        Outcome(await ctl.RestoreLoanType(used.Id, default)).Should().Be("restored");
        (await db.LoanTypes.SingleAsync(x => x.Id == used.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Loan_type_list_shows_turned_off_rows_only_to_a_Setup_writer_who_asks()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        db.LoanTypes.AddRange(new LoanType { TenantId = tenant, Code = "ON", NameEn = "On" }, new LoanType { TenantId = tenant, Code = "OFF", NameEn = "Off", IsActive = false });
        await db.SaveChangesAsync();

        var writer = Wire(new LoansController(db, new AllScope()), tenant, "loans.write");
        ((IEnumerable<LoanType>)((OkObjectResult)await writer.ListLoanTypes(false, default)).Value!).Should().HaveCount(1);
        ((IEnumerable<LoanType>)((OkObjectResult)await writer.ListLoanTypes(true, default)).Value!).Should().HaveCount(2);

        var reader = Wire(new LoansController(db, new AllScope()), tenant, "loans.read");
        ((IEnumerable<LoanType>)((OkObjectResult)await reader.ListLoanTypes(true, default)).Value!).Should().HaveCount(1);
    }

    // ── Bonus types ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bonus_type_unused_is_deleted_then_restorable_and_used_is_turned_off()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var unused = new BonusType { TenantId = tenant, Code = "FEST", NameEn = "Festival" };
        var used = new BonusType { TenantId = tenant, Code = "PERF", NameEn = "Performance" };
        db.BonusTypes.AddRange(unused, used);
        db.BonusBatches.Add(new BonusBatch { TenantId = tenant, BonusTypeId = used.Id });
        await db.SaveChangesAsync();
        var ctl = Wire(new BonusesController(db, new AllScope()), tenant, "payroll.write");

        Outcome(await ctl.DeleteBonusType(unused.Id, default)).Should().Be("deleted");
        Outcome(await ctl.DeleteBonusType(used.Id, default)).Should().Be("turnedOff");
        (await db.BonusTypes.IgnoreQueryFilters().SingleAsync(x => x.Id == used.Id)).IsDeleted.Should().BeFalse();

        Outcome(await ctl.RestoreBonusType(unused.Id, default)).Should().Be("restored");
        var back = await db.BonusTypes.IgnoreQueryFilters().SingleAsync(x => x.Id == unused.Id);
        back.IsDeleted.Should().BeFalse();
        back.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Bonus_type_restore_is_refused_when_its_code_was_taken_meanwhile()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var gone = new BonusType { TenantId = tenant, Code = "FEST", NameEn = "Festival", IsDeleted = true, IsActive = false };
        db.BonusTypes.AddRange(gone, new BonusType { TenantId = tenant, Code = "FEST", NameEn = "Festival 2" });
        await db.SaveChangesAsync();
        var r = await Wire(new BonusesController(db, new AllScope()), tenant, "payroll.write").RestoreBonusType(gone.Id, default);
        r.Should().BeOfType<ConflictObjectResult>();
        Body(r).GetProperty("code").GetString().Should().Be("BONUS_TYPE_CODE_TAKEN");
    }

    // ── Scorecards ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scorecard_in_use_by_a_cycle_is_turned_off_not_deleted_and_can_be_restored()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var used = new PerformanceScorecardTemplate { TenantId = tenant, Name = "Annual" };
        var free = new PerformanceScorecardTemplate { TenantId = tenant, Name = "Spare" };
        db.PerformanceScorecardTemplates.AddRange(used, free);
        db.PerformanceCycles.Add(new PerformanceCycle { TenantId = tenant, Name = "2026", DefaultScorecardTemplateId = used.Id });
        await db.SaveChangesAsync();
        var ctl = Wire(new ScorecardTemplatesController(db), tenant);

        Outcome(await ctl.Delete(free.Id, default)).Should().Be("deleted");
        var off = await ctl.Delete(used.Id, default);
        Outcome(off).Should().Be("turnedOff");
        Body(off).GetProperty("usage").GetProperty("summary").GetString().Should().Contain("review cycles");
        (await db.PerformanceScorecardTemplates.SingleAsync()).IsActive.Should().BeFalse();
        Outcome(await ctl.Restore(used.Id, default)).Should().Be("restored");
        (await db.PerformanceScorecardTemplates.SingleAsync()).IsActive.Should().BeTrue();
    }

    // ── Onboarding checklists ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Onboarding_checklist_used_by_tasks_is_retired_and_an_unused_one_is_deleted_then_restorable()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var used = new OnboardingChecklist { TenantId = tenant, Code = "STD", Name = "Standard" };
        var free = new OnboardingChecklist { TenantId = tenant, Code = "ALT", Name = "Alt" };
        db.OnboardingChecklists.AddRange(used, free);
        db.OnboardingTasks.Add(new OnboardingTask { TenantId = tenant, ChecklistId = used.Id });
        await db.SaveChangesAsync();
        var ctl = Wire(new OnboardingController(db), tenant);

        Outcome(await ctl.RemoveChecklist(free.Id, default)).Should().Be("deleted");
        Outcome(await ctl.RemoveChecklist(used.Id, default)).Should().Be("turnedOff");
        (await db.OnboardingChecklists.SingleAsync(x => x.Id == used.Id)).IsActive.Should().BeFalse();

        Outcome(await ctl.RestoreChecklist(free.Id, default)).Should().Be("restored");
        (await db.OnboardingChecklists.SingleAsync(x => x.Id == free.Id)).IsDeleted.Should().BeFalse();
    }

    // ── Shifts ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Shift_with_assignments_is_turned_off_with_a_reason_and_restorable()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var used = new ShiftDefinition { TenantId = tenant, Code = "DAY", Name = "Day" };
        var free = new ShiftDefinition { TenantId = tenant, Code = "NIGHT", Name = "Night" };
        db.ShiftDefinitions.AddRange(used, free);
        db.ShiftAssignments.Add(new ShiftAssignment { TenantId = tenant, ShiftDefinitionId = used.Id, EmployeeId = 1 });
        await db.SaveChangesAsync();
        var ctl = Wire(new ShiftsController(db, new AllScope(), null!), tenant);

        var off = await ctl.DeleteDefinition(used.Id, default);
        Outcome(off).Should().Be("turnedOff");
        Body(off).GetProperty("usage").GetProperty("summary").GetString().Should().Contain("roster assignments");
        Outcome(await ctl.DeleteDefinition(free.Id, default)).Should().Be("deleted");
        Outcome(await ctl.RestoreDefinition(used.Id, default)).Should().Be("restored");
        (await db.ShiftDefinitions.SingleAsync()).IsActive.Should().BeTrue();
    }

    // ── Payslip templates ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Payslip_template_named_by_an_issued_payslip_is_turned_off_never_deleted_and_the_default_is_protected()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var used = new PayslipTemplate { TenantId = tenant, Name = "Standard", Status = "active" };
        var free = new PayslipTemplate { TenantId = tenant, Name = "Spare", Status = "draft" };
        var def = new PayslipTemplate { TenantId = tenant, Name = "Default", Status = "active", IsDefault = true };
        db.PayslipTemplates.AddRange(used, free, def);
        db.Payslips.Add(new Payslip { TenantId = tenant, PayslipTemplateId = used.Id });
        await db.SaveChangesAsync();
        var ctl = Wire(new PayslipTemplatesController(db, null!, null!), tenant, "payroll.write");

        (await ctl.Delete(def.Id, default)).Should().BeOfType<ConflictObjectResult>();
        var off = await ctl.Delete(used.Id, default);
        Outcome(off).Should().Be("turnedOff");
        Body(off).GetProperty("usage").GetProperty("summary").GetString().Should().Contain("issued payslips");
        (await db.PayslipTemplates.SingleAsync(x => x.Id == used.Id)).Status.Should().Be("off");
        Outcome(await ctl.Delete(free.Id, default)).Should().Be("deleted");

        Outcome(await ctl.Restore(used.Id, default)).Should().Be("restored");
        (await db.PayslipTemplates.SingleAsync(x => x.Id == used.Id)).Status.Should().Be("active");
    }

    // ── Capabilities ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_report_the_forms_the_server_cannot_save_as_off()
    {
        var result = (OkObjectResult)Wire(new CapabilitiesController(), Guid.NewGuid(), "security.manage").Get();
        var features = JsonSerializer.SerializeToElement(result.Value).GetProperty("features");
        features.GetProperty("approvalDelegations.write").GetBoolean().Should().BeFalse();
        features.GetProperty("approvalAuthorities.write").GetBoolean().Should().BeFalse();
    }
}
