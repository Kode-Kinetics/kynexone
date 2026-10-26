using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// P2-1: the organisation-structure import writes department and position cost centres, parent departments and cost-centre active state. Those
/// writes go through the same door guard (M-7, 409 locked_into_approved_run) and in-use check (M-8) as the forms, with the finding on the row at
/// preview time, so nothing is written for a package that a form would refuse.
/// </summary>
public class OrganizationStructureImportAttributionGuardTests
{
    private static OrganizationStructureImportController Controller(ZayraDbContext db, Guid tenant) =>
        new(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("sub", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("permission", "organization.costcenter.manage"),
                        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                    }, "Test"))
                }
            }
        };

    private sealed class W
    {
        public ZayraDbContext Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tenant = Guid.NewGuid();
        public Company A = null!;
        public CostCenter X = null!, Y = null!, Z = null!;
        public Department Dept1 = null!, Dept2 = null!, Dept3 = null!, Dept4 = null!;
        public Position Pos = null!;
        public Employee InRun = null!, KidInRun = null!;
    }

    private static async Task<W> Seed(string runStatus)
    {
        var w = new W();
        w.A = new Company { TenantId = w.Tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        w.X = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "X", Name = "X" };
        w.Y = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "Y", Name = "Y" };
        w.Z = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "Z", Name = "Z" };
        w.Dept1 = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D1", NameEn = "Dept One", CostCenterId = w.X.Id };
        w.Dept2 = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D2", NameEn = "Dept Two", CostCenterId = w.Y.Id };
        w.Dept3 = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D3", NameEn = "Dept Three", ParentDepartmentId = w.Dept1.Id };
        w.Dept4 = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D4", NameEn = "Dept Four", CostCenterId = w.Z.Id };
        w.Pos = new Position { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "P1", Title = "Analyst" };
        w.Db.AddRange(w.A, w.X, w.Y, w.Z, w.Dept1, w.Dept2, w.Dept3, w.Dept4, w.Pos);
        Employee Emp(string code, Department d, Guid? position = null) => new()
        {
            TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = code, FullName = code, Status = "Active", JoiningDate = DateTime.UtcNow,
            DepartmentId = d.Id, Department = d.NameEn, PositionId = position,
        };
        w.InRun = Emp("IN", w.Dept1, w.Pos.Id);
        w.KidInRun = Emp("KID", w.Dept3);
        w.Db.AddRange(w.InRun, w.KidInRun);
        var run = new PayrollRun { TenantId = w.Tenant, CompanyId = w.A.Id, Year = 2026, Month = 6, Status = runStatus };
        w.Db.PayrollRuns.Add(run);
        await w.Db.SaveChangesAsync();
        w.Db.PayrollSlips.AddRange(
            new PayrollSlip { TenantId = w.Tenant, CompanyId = w.A.Id, RunId = run.Id, EmployeeId = w.InRun.Id },
            new PayrollSlip { TenantId = w.Tenant, CompanyId = w.A.Id, RunId = run.Id, EmployeeId = w.KidInRun.Id });
        await w.Db.SaveChangesAsync();
        return w;
    }

    private static OrganizationStructureImportRequest Req(string? costCentres = null, string? departments = null, string? positions = null) =>
        new(null, null, costCentres, departments, null, null, null, positions);

    private static async Task<OrganizationStructureImportResult> Preview(W w, OrganizationStructureImportRequest r) =>
        (OrganizationStructureImportResult)((OkObjectResult)(await Controller(w.Db, w.Tenant).Preview(r, default)).Result!).Value!;

    private const string Locked = "A payroll run for this company is awaiting finance review or lock; reopen it first.";

    [Theory]
    [InlineData("PendingFinanceReview")]
    [InlineData("Approved")]
    public async Task ADepartmentRepoint_OfSomeoneInTheRun_IsARowError_AtPreview_AndCommitWritesNothing(string status)
    {
        var w = await Seed(status);
        var request = Req(departments: "Code,NameEn,CostCenterCode\nD1,Dept One,Y\nD4,Dept Four,Z\n");

        var preview = await Preview(w, request);
        preview.HasBlockingErrors.Should().BeTrue();
        var row = preview.Rows.Single(r => r.Status == ImportRowStatus.Error);
        row.Errors.Should().Contain(e => e.Contains(Locked));
        row.ErrorsAr.Should().NotBeNull().And.Contain(e => e != null && e.Contains("أعد فتح المسير"));
        row.RowNumber.Should().Be(2);

        (await Controller(w.Db, w.Tenant).Commit(request, default)).Result.Should().NotBeOfType<OkObjectResult>();
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Dept1.Id)).CostCenterId.Should().Be(w.X.Id);
    }

    [Fact]
    public async Task AParentChange_ThatMovesSomeoneInTheRun_IsARowError()
    {
        var w = await Seed("Approved");
        var preview = await Preview(w, Req(departments: "Code,NameEn,ParentDepartmentCode\nD3,Dept Three,D2\n"));
        preview.Rows.Should().Contain(r => r.Status == ImportRowStatus.Error && r.Errors.Any(e => e.Contains(Locked)));
    }

    [Fact]
    public async Task APositionRepoint_OfSomeoneInTheRun_IsARowError()
    {
        var w = await Seed("PendingFinanceReview");
        var preview = await Preview(w, Req(positions: "Code,Title,CostCenterCode\nP1,Analyst,Z\n"));
        preview.Rows.Should().Contain(r => r.Status == ImportRowStatus.Error && r.Errors.Any(e => e.Contains(Locked)));
    }

    [Fact]
    public async Task ADepartmentNobodyInTheRunBelongsTo_MayStillBeRepointed()
    {
        var w = await Seed("Approved");
        var request = Req(departments: "Code,NameEn,CostCenterCode\nD4,Dept Four,Y\n");
        (await Preview(w, request)).HasBlockingErrors.Should().BeFalse();
        (await Controller(w.Db, w.Tenant).Commit(request, default)).Result.Should().BeOfType<OkObjectResult>();
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Dept4.Id)).CostCenterId.Should().Be(w.Y.Id);
    }

    [Theory]
    [InlineData("Processed")]
    [InlineData("Locked")]
    public async Task TheSameRepoint_Commits_WhenNoRunIsAwaitingReviewOrLock(string status)
    {
        var w = await Seed(status);
        var request = Req(departments: "Code,NameEn,CostCenterCode\nD1,Dept One,Y\n");
        (await Preview(w, request)).HasBlockingErrors.Should().BeFalse();
        (await Controller(w.Db, w.Tenant).Commit(request, default)).Result.Should().BeOfType<OkObjectResult>();
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Dept1.Id)).CostCenterId.Should().Be(w.Y.Id);
    }

    [Fact]
    public async Task AnUnchangedLink_IsNotARepoint_EvenInALockingRun()
    {
        var w = await Seed("Approved");
        var preview = await Preview(w, Req(departments: "Code,NameEn,CostCenterCode\nD1,Renamed Dept,X\n"));
        preview.HasBlockingErrors.Should().BeFalse();
    }

    [Fact]
    public async Task DeactivatingACostCentre_StillInUse_IsARowError_WithTheInUseRule()
    {
        var w = await Seed("Processed");
        var request = Req(costCentres: "CompanyLegalName,Code,Name,IsActive\nAlpha,X,X,false\n");
        var preview = await Preview(w, request);
        preview.HasBlockingErrors.Should().BeTrue();
        preview.Rows.Single(r => r.Status == ImportRowStatus.Error).Errors.Should().Contain(e => e.Contains("is used by"));
        (await Controller(w.Db, w.Tenant).Commit(request, default)).Result.Should().NotBeOfType<OkObjectResult>();
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == w.X.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeactivatingAnUnusedCostCentre_Commits()
    {
        var w = await Seed("Processed");
        w.Db.CostCenters.Add(new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "SPARE", Name = "Spare" });
        await w.Db.SaveChangesAsync();
        var request = Req(costCentres: "CompanyLegalName,Code,Name,IsActive\nAlpha,SPARE,Spare,false\n");
        (await Preview(w, request)).HasBlockingErrors.Should().BeFalse();
        (await Controller(w.Db, w.Tenant).Commit(request, default)).Result.Should().BeOfType<OkObjectResult>();
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == "SPARE")).IsActive.Should().BeFalse();
    }
}
