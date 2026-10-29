using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// Reopening a hire's draft must not make the pay HR typed look lost while Finance has not yet approved it: the employee read projects the
/// pending first package (marked Pending) to the callers who see applied pay, and a draft saved again keeps or replaces that one request.
/// </summary>
public class PendingFirstPackageDraftTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Guid Hr = Guid.NewGuid();

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), null!, TestNotifications.For(db));

    private static async Task<(Guid Tenant, Employee Employee)> SeedAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var employee = new Employee { TenantId = tenant, EmployeeCode = "E1", FullName = "Alex Morgan", Status = EmployeeStatuses.Draft, CreatedBy = Hr };
        db.Employees.Add(employee);
        db.SalaryStructures.Add(new SalaryStructure { TenantId = tenant, Code = "DIRECT", Name = "Direct", Currency = "SAR" });
        await db.SaveChangesAsync(Ct);
        return (tenant, employee);
    }

    private static EmployeeSalaryBreakdownRequest Figures(decimal basic) =>
        new(basic, 3000m, null, null, null, null, null, "DIRECT", new DateOnly(2026, 11, 1), "SAR");

    private static RequestContext Ctx(Guid tenant) => new(null, null, Hr, tenant);

    [Fact]
    public async Task TheDraftRead_ShowsThePendingFirstPackage_MarkedPending_ToACallerWhoSeesPay()
    {
        await using var db = CreateDb();
        var (tenant, employee) = await SeedAsync(db);
        await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(db, employee, Figures(12000.5m), Ctx(tenant), Ct);
        await db.SaveChangesAsync(Ct);

        var detail = (await Service(db).GetAsync(tenant, employee.Id, includeSensitive: true, Ctx(tenant), Ct))!;

        detail.SalaryApprovalState.Should().Be("Pending");
        detail.SalaryBreakdown.Should().NotBeNull();
        detail.SalaryBreakdown!.BasicSalary.Should().Be(12000.5m);
        detail.SalaryBreakdown.HousingAllowance.Should().Be(3000m);
        detail.SalaryBreakdown.SalaryStructureCode.Should().Be("DIRECT");
        detail.SalaryBreakdown.EffectiveDate.Should().Be(new DateOnly(2026, 11, 1));
        detail.Salary.Should().Be(15000.5m);
        detail.PayrollProfileCode.Should().Be("DIRECT");
    }

    [Fact]
    public async Task TheDraftRead_ShowsNothingOfThePendingPackage_ToACallerWithoutSensitiveAccess()
    {
        await using var db = CreateDb();
        var (tenant, employee) = await SeedAsync(db);
        await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(db, employee, Figures(12000.5m), Ctx(tenant), Ct);
        await db.SaveChangesAsync(Ct);

        var detail = (await Service(db).GetAsync(tenant, employee.Id, includeSensitive: false, Ctx(tenant), Ct))!;

        detail.SalaryBreakdown.Should().BeNull();
        detail.Salary.Should().BeNull();
        detail.SalaryApprovalState.Should().BeNull();
    }

    [Fact]
    public async Task AnEmployeeWithNoPendingPackage_HasNoApprovalState()
    {
        await using var db = CreateDb();
        var (tenant, employee) = await SeedAsync(db);

        var detail = (await Service(db).GetAsync(tenant, employee.Id, includeSensitive: true, Ctx(tenant), Ct))!;

        detail.SalaryApprovalState.Should().BeNull();
        detail.SalaryBreakdown.Should().BeNull();
    }

    [Fact]
    public async Task SavingTheDraftAgainWithUnchangedFigures_KeepsTheOnePendingRequest()
    {
        await using var db = CreateDb();
        var (tenant, employee) = await SeedAsync(db);
        var first = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(db, employee, Figures(12000.5m), Ctx(tenant), Ct);
        await db.SaveChangesAsync(Ct);

        var again = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(db, employee, Figures(12000.5m), Ctx(tenant), Ct);
        await db.SaveChangesAsync(Ct);

        again!.Id.Should().Be(first!.Id);
        (await db.SalaryChangeRequests.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task SavingTheDraftAgainWithChangedFigures_ReplacesThePendingRequest()
    {
        await using var db = CreateDb();
        var (tenant, employee) = await SeedAsync(db);
        var first = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(db, employee, Figures(12000.5m), Ctx(tenant), Ct);
        await db.SaveChangesAsync(Ct);

        var second = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(db, employee, Figures(13000m), Ctx(tenant), Ct);
        await db.SaveChangesAsync(Ct);

        second!.Id.Should().NotBe(first!.Id);
        (await db.SalaryChangeRequests.Where(x => x.Status == SalaryChangeRequest.Pending).ToListAsync(Ct)).Should().ContainSingle().Which.BasicSalary.Should().Be(13000m);
        (await db.SalaryChangeRequests.SingleAsync(x => x.Id == first.Id, Ct)).Status.Should().Be(SalaryChangeRequest.Cancelled);
    }
}
