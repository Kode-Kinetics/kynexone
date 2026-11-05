using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>Company, tenant and department scope against real Postgres, where the query filters and row visibility are the production ones.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class ManpowerReconScopePostgresTests
{
    private readonly PostgresFixture _fx;
    public ManpowerReconScopePostgresTests(PostgresFixture fx) => _fx = fx;

    private static int StatusOf(IActionResult r) => PayrollReconAccessTests.Status(r);

    [Fact]
    public async Task ADepartmentScopedCaller_GetsAPartialBridge_AndCannotSign()
    {
        var s = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);
        var firstEmployee = s.Db.Employees.OrderBy(e => e.EmployeeCode).First().Id;
        var scoped = new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = new[] { firstEmployee } };
        var ctrl = ReconHarness.Recon(s.Db, s.TenantId, "Payroll Manager", ReconHarness.SignPermissions, dataScope: scoped);

        var dto = ReconHarness.Body<ReconDto>(await ctrl.Get(s.Current.Id, null, default));
        dto.Scope.Partial.Should().BeTrue();
        dto.Scope.CanSignOff.Should().BeFalse();
        dto.Volume.Closing.Should().Be(1);
        dto.Volume.Opening.Should().Be(1, "the other employee's prior slip is outside the caller's scope");

        var res = await ctrl.Signoff(s.Current.Id, new ReconSignoffRequest(s.Current.ProcessVersion, "x", null, 0, null, null), default);
        StatusOf(res).Should().Be(403);
        PayrollReconAccessTests.Error(res).Should().Be("signoff_requires_organization_scope");
        (await ctrl.Export(s.Current.Id, "csv", null, default)).Should().BeOfType<FileContentResult>()
            .Which.FileDownloadName.Should().EndWith("-PARTIAL.csv");
    }

    [Fact]
    public async Task ACallerWithoutAccessToTheRunsCompany_GetsNotFound_FromBothEndpoints()
    {
        var s = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);
        var other = ReconHarness.CompanyScope(Guid.NewGuid());

        var recon = ReconHarness.Recon(s.Db, s.TenantId, "Admin", ReconHarness.SignPermissions, entityScopeJson: other);
        StatusOf(await recon.Get(s.Current.Id, null, default)).Should().Be(404);
        StatusOf(await recon.Signoff(s.Current.Id, new ReconSignoffRequest(1, "x", null, 0, null, null), default)).Should().Be(404);

        var old = ReconHarness.Payroll(s.Db, s.TenantId, "Admin", new[] { "payroll.read" }, entityScopeJson: other);
        (await old.Reconciliation(s.Current.Id, default)).Should().BeOfType<NotFoundResult>();

        var allowed = ReconHarness.Payroll(s.Db, s.TenantId, "Admin", new[] { "payroll.read" }, entityScopeJson: ReconHarness.CompanyScope(s.Kit.CompanyId));
        (await allowed.Reconciliation(s.Current.Id, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ARunOfAnotherTenant_IsNotFound()
    {
        var mine = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);
        var theirs = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);

        var ctrl = ReconHarness.Recon(mine.Db, mine.TenantId, "Admin", ReconHarness.SignPermissions);
        StatusOf(await ctrl.Get(theirs.Current.Id, null, default)).Should().Be(404);
        StatusOf(await ctrl.GetSignoff(theirs.Current.Id, null, default)).Should().Be(404);
        StatusOf(await ctrl.Employees(theirs.Current.Id, "volume", "CONTINUING", null, null, null, 1, 50, default)).Should().Be(404);
        StatusOf(await ctrl.Get(mine.Current.Id, null, default)).Should().Be(200);

        var old = ReconHarness.Payroll(mine.Db, mine.TenantId, "Admin", new[] { "payroll.read" });
        (await old.Reconciliation(theirs.Current.Id, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task APriorRunOfAnotherTenant_CannotBeChosenAsTheComparison()
    {
        var mine = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);
        var theirs = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);
        var ctrl = ReconHarness.Recon(mine.Db, mine.TenantId, "Admin", ReconHarness.SignPermissions);
        var res = await ctrl.Get(mine.Current.Id, theirs.Prior.Id, default);
        StatusOf(res).Should().Be(400);
        PayrollReconAccessTests.Error(res).Should().Be("prior_run_invalid");
    }
}
