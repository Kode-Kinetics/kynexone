using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// Who may read the reconciliation (payroll.read), who may sign it (payroll.approve only), and which runs a caller can reach at
/// all. Roles carry the permissions the real seeder gives them, so the gate and the catalogue cannot drift apart.
/// </summary>
public class PayrollReconAccessTests
{
    internal static async Task<(ManpowerReconServiceTests.Kit Kit, Dictionary<string, List<string>> Roles)> SeedAsync()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync();
        k.Db.Tenants.Add(new Tenant { Id = k.TenantId, Name = "Recon Tenant", Slug = $"recon-{Guid.NewGuid():N}"[..20] });
        await k.Db.SaveChangesAsync();
        await new Zayra.Api.Infrastructure.Seed.AuthSeeder(k.Db).EnsureTenantRolesAsync(k.TenantId, CancellationToken.None);
        var roles = (await k.Db.Roles.AsNoTracking().Where(r => r.TenantId == k.TenantId)
                .Select(r => new { r.Name, Keys = r.RolePermissions.Select(rp => rp.Permission!.Key).ToList() }).ToListAsync())
            .ToDictionary(r => r.Name, r => r.Keys);
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1000);
        k.AddEmployee(2); k.AddSlip(k.Prior, 2, 800);
        k.Db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        { TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = k.Current.Id, EmployeeId = 2, Mode = "Exclude", Reason = "hold", Outcome = PayrollRunSelectionOutcomes.Excluded });
        await k.Db.SaveChangesAsync();
        return (k, roles);
    }

    internal static int Status(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        ForbidResult => 403,
        NotFoundResult => 404,
        StatusCodeResult s => s.StatusCode,
        FileResult => 200,
        ContentResult c => c.StatusCode ?? 200,
        _ => throw new InvalidOperationException($"Unexpected result {r.GetType().Name}"),
    };

    internal static string Error(IActionResult r) => ReconHarness.Json(((ObjectResult)r).Value).GetProperty("error").GetString()!;

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("Payroll Manager")]
    [InlineData("Payroll Officer")]
    [InlineData("HR Manager")]
    [InlineData("HR Director")]
    [InlineData("Finance Approver")]
    [InlineData("Auditor")]
    public async Task EveryRoleWithPayrollRead_CanOpenTheReconciliation_ItsDrillDown_AndTheExport(string role)
    {
        var (k, roles) = await SeedAsync();
        roles[role].Should().Contain("payroll.read", "fixture sanity");
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, role, roles[role]);

        var res = await ctrl.Get(k.Current.Id, null, default);
        res.Should().BeOfType<OkObjectResult>();
        ReconHarness.Body<ReconDto>(res).Mode.Should().Be(ReconModes.Full);
        (await ctrl.Employees(k.Current.Id, "volume", "NOT_PAID", null, null, null, 1, 50, default)).Should().BeOfType<OkObjectResult>();
        Status(await ctrl.GetSignoff(k.Current.Id, null, default)).Should().Be(200);
        (await ctrl.Export(k.Current.Id, "csv", null, default)).Should().BeAssignableTo<FileResult>();
    }

    [Theory]
    [InlineData("HR Officer")]
    [InlineData("Manager")]
    [InlineData("Employee")]
    [InlineData("Recruiter")]
    [InlineData("Compliance Officer")]
    public async Task RolesWithoutPayrollRead_AreRefusedEverywhere(string role)
    {
        var (k, roles) = await SeedAsync();
        roles[role].Should().NotContain("payroll.read", "fixture sanity");
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, role, roles[role]);

        (await ctrl.Get(k.Current.Id, null, default)).Should().BeOfType<ForbidResult>();
        (await ctrl.Employees(k.Current.Id, "volume", "NOT_PAID", null, null, null, 1, 50, default)).Should().BeOfType<ForbidResult>();
        (await ctrl.GetSignoff(k.Current.Id, null, default)).Should().BeOfType<ForbidResult>();
        (await ctrl.Export(k.Current.Id, "xlsx", null, default)).Should().BeOfType<ForbidResult>();
        (await ctrl.Signoff(k.Current.Id, new ReconSignoffRequest(0, "x", null, 0, null, null), default)).Should().BeOfType<ForbidResult>();
        (await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("a long enough reason"), default)).Should().BeOfType<ForbidResult>();
    }

    // ── Signing is payroll.approve, and only that ────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("Payroll Manager")]
    [InlineData("HR Manager")]
    [InlineData("Finance Approver")]
    public async Task TheRolesHoldingPayrollApprove_CanSign(string role)
    {
        var (k, roles) = await SeedAsync();
        roles[role].Should().Contain("payroll.approve", "fixture sanity");
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, role, roles[role]);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);

        var res = await ctrl.Signoff(k.Current.Id, req, default);
        Status(res).Should().Be(201, res is ObjectResult o ? System.Text.Json.JsonSerializer.Serialize(o.Value) : "");
        ReconHarness.Body<ReconSignoffDto>(res).Status.Should().Be(ReconSignoffStatuses.Active);
    }

    [Theory]
    [InlineData("Payroll Officer")]
    [InlineData("HR Director")]
    [InlineData("Auditor")]
    public async Task ReadersWithoutPayrollApprove_CannotSign_OrWithdraw(string role)
    {
        var (k, roles) = await SeedAsync();
        roles[role].Should().Contain("payroll.read").And.NotContain("payroll.approve", "fixture sanity");
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, role, roles[role]);
        var admin = ReconHarness.Recon(k.Db, k.TenantId, "Admin", ReconHarness.SignPermissions);
        var req = await ReconHarness.ValidRequestAsync(admin, k.Current.Id);

        (await ctrl.Signoff(k.Current.Id, req, default)).Should().BeOfType<ForbidResult>();
        (await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("not mine to withdraw"), default)).Should().BeOfType<ForbidResult>();
        (await k.Db.PayrollRunReconSignoffs.AsNoTracking().CountAsync()).Should().Be(0);
    }

    // ── Company, tenant and scope ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACallerWithoutAccessToTheRunsCompany_GetsNotFound_OnEveryEndpoint()
    {
        var (k, _) = await SeedAsync();
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Admin", ReconHarness.SignPermissions, entityScopeJson: ReconHarness.CompanyScope(Guid.NewGuid()));

        Status(await ctrl.Get(k.Current.Id, null, default)).Should().Be(404);
        Status(await ctrl.Employees(k.Current.Id, "volume", "NOT_PAID", null, null, null, 1, 50, default)).Should().Be(404);
        Status(await ctrl.GetSignoff(k.Current.Id, null, default)).Should().Be(404);
        Status(await ctrl.Export(k.Current.Id, "xlsx", null, default)).Should().Be(404);
        Status(await ctrl.Signoff(k.Current.Id, new ReconSignoffRequest(1, "x", null, 0, null, null), default)).Should().Be(404);
        Status(await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("a long enough reason"), default)).Should().Be(404);
    }

    [Fact]
    public async Task ACallerScopedToTheRunsCompany_CanRead()
    {
        var (k, _) = await SeedAsync();
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Admin", ReconHarness.SignPermissions, entityScopeJson: ReconHarness.CompanyScope(k.CompanyId));
        Status(await ctrl.Get(k.Current.Id, null, default)).Should().Be(200);
    }

    [Fact]
    public async Task ARunOfAnotherTenant_IsNotFound()
    {
        var (k, _) = await SeedAsync();
        var ctrl = ReconHarness.Recon(k.Db, Guid.NewGuid(), "Admin", ReconHarness.SignPermissions);
        Status(await ctrl.Get(k.Current.Id, null, default)).Should().Be(404);
        Status(await ctrl.Signoff(k.Current.Id, new ReconSignoffRequest(1, "x", null, 0, null, null), default)).Should().Be(404);
    }

    [Fact]
    public async Task ADepartmentScopedReader_SeesAPartialBridge_ButCannotSign()
    {
        var (k, _) = await SeedAsync();
        var scoped = new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = new[] { 1 } };
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Payroll Manager", ReconHarness.SignPermissions, dataScope: scoped);

        var dto = ReconHarness.Body<ReconDto>(await ctrl.Get(k.Current.Id, null, default));
        dto.Scope.Partial.Should().BeTrue();
        dto.Scope.CanSignOff.Should().BeFalse();

        var res = await ctrl.Signoff(k.Current.Id, new ReconSignoffRequest(k.Current.ProcessVersion, "x", null, 0, null, null), default);
        Status(res).Should().Be(403);
        Error(res).Should().Be("signoff_requires_organization_scope");
    }

    // ── The existing endpoint honours company scope too ──────────────────────────────────────────

    private static PayrollController OldEndpoint(ManpowerReconServiceTests.Kit k, string? entityScopeJson) =>
        ReconHarness.Payroll(k.Db, k.TenantId, "Admin", new[] { "payroll.read" }, entityScopeJson: entityScopeJson);

    [Fact]
    public async Task TheExistingReconciliationEndpoint_HonoursCompanyScope()
    {
        var (k, _) = await SeedAsync();
        var res = await OldEndpoint(k, ReconHarness.CompanyScope(Guid.NewGuid())).Reconciliation(k.Current.Id, default);
        res.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task TheExistingReconciliationEndpoint_StillServesACallerWithAccess()
    {
        var (k, _) = await SeedAsync();
        (await OldEndpoint(k, ReconHarness.CompanyScope(k.CompanyId)).Reconciliation(k.Current.Id, default)).Should().BeOfType<OkObjectResult>();
        (await OldEndpoint(k, null).Reconciliation(k.Current.Id, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TheExistingReconciliationEndpoint_UsesPeriodOwningPriors_SoAReplacementIsComparedAgainst()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync(withPrior: false);
        var voided = k.AddRun(2026, 9, "Voided");
        var replacement = k.AddRun(2026, 9, "Locked", PayrollRunTypes.Replacement);
        replacement.ParentRunId = voided.Id;
        k.AddEmployee(1); k.AddSlip(replacement, 1, 1000); k.AddSlip(k.Current, 1, 1000);
        await k.Db.SaveChangesAsync();

        var body = ReconHarness.Json(((OkObjectResult)await OldEndpoint(k, null).Reconciliation(k.Current.Id, default)).Value);
        body.GetProperty("priorPeriod").GetString().Should().Be("2026-09");
        body.GetProperty("priorHeadcount").GetInt32().Should().Be(1);
    }

    // ── Off-cycle ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnOffCycleRun_ReadsAsNotApplicable_AndRefusesASignoff()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync(currentType: PayrollRunTypes.OffCycle);
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Admin", ReconHarness.SignPermissions);

        ReconHarness.Body<ReconDto>(await ctrl.Get(k.Current.Id, null, default)).Mode.Should().Be(ReconModes.NotApplicable);
        var res = await ctrl.Signoff(k.Current.Id, new ReconSignoffRequest(k.Current.ProcessVersion, "x", null, 0, null, null), default);
        Status(res).Should().Be(409);
        Error(res).Should().Be("recon_not_applicable");
        Status(await ctrl.Export(k.Current.Id, "csv", null, default)).Should().Be(409);
    }

    [Fact]
    public async Task AnInvalidPriorRunId_IsABadRequest_NotASilentFallback()
    {
        var (k, _) = await SeedAsync();
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Admin", ReconHarness.SignPermissions);
        var res = await ctrl.Get(k.Current.Id, Guid.NewGuid(), default);
        Status(res).Should().Be(400);
        Error(res).Should().Be("prior_run_invalid");
    }

    [Fact]
    public async Task TheDrillDown_RefusesAnUnknownSection_AndAnUnknownCategory()
    {
        var (k, _) = await SeedAsync();
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Admin", ReconHarness.SignPermissions);
        Status(await ctrl.Employees(k.Current.Id, "nope", null, null, null, null, 1, 50, default)).Should().Be(400);
        Status(await ctrl.Employees(k.Current.Id, "volume", "NOT_A_CATEGORY", null, null, null, 1, 50, default)).Should().Be(400);
    }
}
