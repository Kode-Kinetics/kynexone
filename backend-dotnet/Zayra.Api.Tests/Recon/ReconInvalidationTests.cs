using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// A sign-off reviews specific figures, so whatever can change those figures retires it in the same transaction: a re-process,
/// a reopen, a send-back, a void. The process version only ever goes up.
/// </summary>
public class ReconInvalidationTests
{
    private static readonly string[] Lock = { "payroll.lock", "payroll.approve", "payroll.read" };

    private static async Task<(GlPostingKit Kit, PayrollRun Run)> RunWithSignoffAsync(string status)
    {
        var kit = await GlPostingKit.CreateAsync();
        var run = await kit.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, Array.Empty<(int, string, string, decimal)>(), 5_000m);
        run.Status = status;
        kit.Db.PayrollRunReconSignoffs.Add(NewSignoff(kit, run));
        await kit.Db.SaveChangesAsync();
        return (kit, run);
    }

    private static PayrollRunReconSignoff NewSignoff(GlPostingKit kit, PayrollRun run) => new()
    {
        TenantId = kit.TenantId, CompanyId = kit.CompanyId, PayrollRunId = run.Id, ProcessVersion = run.ProcessVersion,
        SnapshotHash = new string('b', 64), SnapshotJson = "{}", SignedOffByUserId = Guid.NewGuid(), SignedOffByName = "Reviewer",
        Status = ReconSignoffStatuses.Active,
    };

    private static async Task AssertSupersededAsync(GlPostingKit kit, string reason)
    {
        kit.Db.ChangeTracker.Clear();
        var rows = await kit.Db.PayrollRunReconSignoffs.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Status.Should().Be(ReconSignoffStatuses.Superseded);
        rows[0].SupersededReason.Should().Be(reason);
        rows[0].SupersededAtUtc.Should().NotBeNull();
        var audit = await kit.Db.PayrollAuditLogs.AsNoTracking().Where(a => a.Action == "payroll.recon.superseded").ToListAsync();
        audit.Should().ContainSingle();
        audit[0].MetadataJson.Should().Contain(reason);
    }

    [Fact]
    public async Task Reopen_SupersedesTheActiveSignoff()
    {
        var (kit, run) = await RunWithSignoffAsync("Processed");
        (await kit.Payroll(role: "Admin", permissions: Lock).ReopenRun(run.Id, new PayrollReasonRequest("wrong attendance import"), default))
            .Should().BeOfType<OkObjectResult>();
        await AssertSupersededAsync(kit, ReconSupersededReasons.Reopened);
    }

    [Fact]
    public async Task SendBack_SupersedesTheActiveSignoff()
    {
        var (kit, run) = await RunWithSignoffAsync("PendingFinanceReview");
        (await kit.Payroll(role: "Admin", permissions: Lock).SendBack(run.Id, new PayrollDecisionRequest("numbers look off"), default))
            .Should().BeOfType<OkObjectResult>();
        await AssertSupersededAsync(kit, ReconSupersededReasons.SentBack);
    }

    [Fact]
    public async Task Void_SupersedesTheActiveSignoff()
    {
        var (kit, run) = await RunWithSignoffAsync("Processed");
        (await kit.Payroll(role: "Admin", permissions: Lock).VoidRun(run.Id, new PayrollDecisionRequest("duplicate run"), default))
            .Should().BeOfType<OkObjectResult>();
        await AssertSupersededAsync(kit, ReconSupersededReasons.Voided);
    }

    [Fact]
    public async Task ARefusedReopen_LeavesTheSignoffActive()
    {
        var (kit, run) = await RunWithSignoffAsync("Locked");
        (await kit.Payroll(role: "Admin", permissions: Lock).ReopenRun(run.Id, new PayrollReasonRequest("too late"), default))
            .Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().BeOneOf(400, 422);
        kit.Db.ChangeTracker.Clear();
        (await kit.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync()).Status.Should().Be(ReconSignoffStatuses.Active);
    }

    [Fact]
    public async Task OnlyTheActiveSignoff_IsSuperseded_AnEarlierSupersededOneIsLeftAlone()
    {
        var (kit, run) = await RunWithSignoffAsync("Processed");
        var old = NewSignoff(kit, run);
        old.Status = ReconSignoffStatuses.Superseded; old.SupersededAtUtc = DateTime.UtcNow.AddDays(-2); old.SupersededReason = ReconSupersededReasons.Withdrawn;
        kit.Db.PayrollRunReconSignoffs.Add(old);
        await kit.Db.SaveChangesAsync();

        await ReconSignoffService.SupersedeActiveAsync(kit.Db, kit.TenantId, run.Id, ReconSupersededReasons.Reopened, null, default);
        await kit.Db.SaveChangesAsync();

        var rows = await kit.Db.PayrollRunReconSignoffs.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.Id == old.Id).SupersededReason.Should().Be(ReconSupersededReasons.Withdrawn);
        rows.Single(r => r.Id != old.Id).SupersededReason.Should().Be(ReconSupersededReasons.Reopened);
    }

    [Fact]
    public async Task SupersedingWhenNothingIsActive_WritesNothing_NotEvenAnAuditRow()
    {
        var kit = await GlPostingKit.CreateAsync();
        var run = await kit.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, Array.Empty<(int, string, string, decimal)>(), 5_000m);
        await ReconSignoffService.SupersedeActiveAsync(kit.Db, kit.TenantId, run.Id, ReconSupersededReasons.Voided, null, default);
        await kit.Db.SaveChangesAsync();
        (await kit.Db.PayrollAuditLogs.AsNoTracking().CountAsync(a => a.Action == "payroll.recon.superseded")).Should().Be(0);
    }

    // ── Process: the version bump ────────────────────────────────────────────────────────────────

    private static async Task<(GlPostingKit Kit, PayrollRun Run)> DraftRunWithOneEmployeeAsync()
    {
        var kit = await GlPostingKit.CreateAsync();
        var structure = new SalaryStructure { TenantId = kit.TenantId, Code = "BASE", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true };
        kit.Db.SalaryStructures.Add(structure);
        await kit.Db.SaveChangesAsync();
        var emp = new Employee
        {
            TenantId = kit.TenantId, CompanyId = kit.CompanyId, EmployeeCode = "R001", FullName = "Process Subject", Status = "Active",
            JoiningDate = new DateTime(2022, 3, 1), WorkEmail = "r001@recon.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        kit.Db.Employees.Add(emp);
        await kit.Db.SaveChangesAsync();
        kit.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = kit.TenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id, Currency = "SAR",
            BasicSalary = 12_000m, HousingAllowance = 3_000m, TransportAllowance = 800m, EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        kit.Db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = kit.TenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = "MOL-R001", SalaryCurrency = "SAR" });
        var now = DateTime.UtcNow;
        var run = new PayrollRun { TenantId = kit.TenantId, CompanyId = kit.CompanyId, Year = now.Year, Month = now.Month, Status = "Draft", CreatedByUserId = Guid.NewGuid() };
        kit.Db.PayrollRuns.Add(run);
        await kit.Db.SaveChangesAsync();
        kit.Db.ChangeTracker.Clear();
        return (kit, run);
    }

    [Fact]
    public async Task EveryProcess_RaisesTheProcessVersionByOne_AndAReopenNeverResetsIt()
    {
        var (kit, run) = await DraftRunWithOneEmployeeAsync();
        run.ProcessVersion.Should().Be(0);

        (await ReconHarness.Payroll(kit.Db, kit.TenantId, "Admin", Lock).Process(run.Id, default)).Should().BeOfType<OkObjectResult>();
        kit.Db.ChangeTracker.Clear();
        (await kit.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).ProcessVersion.Should().Be(1);

        (await kit.Payroll(role: "Admin", permissions: Lock).ReopenRun(run.Id, new PayrollReasonRequest("recheck the inputs"), default)).Should().BeOfType<OkObjectResult>();
        kit.Db.ChangeTracker.Clear();
        var reopened = await kit.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        reopened.Status.Should().Be("Draft");
        reopened.ProcessVersion.Should().Be(1, "a reopen must not lower the version");

        (await ReconHarness.Payroll(kit.Db, kit.TenantId, "Admin", Lock).Process(run.Id, default)).Should().BeOfType<OkObjectResult>();
        kit.Db.ChangeTracker.Clear();
        (await kit.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).ProcessVersion.Should().Be(2);
    }

    [Fact]
    public async Task Process_SupersedesAnActiveSignoff_AsReprocessed()
    {
        var (kit, run) = await DraftRunWithOneEmployeeAsync();
        // A sign-off can only be Active on a Draft run through a fault elsewhere; Process must still retire it.
        kit.Db.PayrollRunReconSignoffs.Add(NewSignoff(kit, run));
        await kit.Db.SaveChangesAsync();
        kit.Db.ChangeTracker.Clear();

        (await ReconHarness.Payroll(kit.Db, kit.TenantId, "Admin", Lock).Process(run.Id, default)).Should().BeOfType<OkObjectResult>();
        await AssertSupersededAsync(kit, ReconSupersededReasons.Reprocessed);
    }
}
