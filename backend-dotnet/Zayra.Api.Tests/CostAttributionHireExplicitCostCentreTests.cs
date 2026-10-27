using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>A hire given an explicit cost centre is still a hire: it is recorded as the initial attribution and does not count toward the lock/export separation-of-duties check. Moving an existing employee still does.</summary>
public class CostAttributionHireExplicitCostCentreTests
{
    private const string Period = "2026-07";
    private const string Reason = "Seconded to the project";

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Department Ops, CostCenter Own);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR", CostAccountingEnabled = true };
        var opsCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var own = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OWN", Name = "Own" };
        var ops = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPSD", NameEn = "Operations", CostCenterId = opsCc.Id, IsActive = true };
        db.AddRange(a, opsCc, own, ops);
        await db.SaveChangesAsync();
        return new World(db, tenant, a, ops, own);
    }

    private static EmployeeManagementService Service(ZayraDbContext db) => new(db, new AuditService(db), new HireCcStorage(), TestNotifications.For(db));
    private static RequestContext Ctx(World w, Guid user) => new("127.0.0.1", "test", user, w.Tenant);

    private static EmployeeCreateRequest Form(World w, string code, Guid? costCentre, string? reason) => new(
        EmployeeCode: code, ManualEmployeeCode: true, EnglishName: "Hire Test", ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Pakistani", MaritalStatus: null, PersonalEmail: null,
        WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: w.A.Id, BranchId: null, DepartmentId: w.Ops.Id,
        DesignationId: null, GradeId: null, CostCenterId: costCentre, JobTitle: null, ReportingManagerEmployeeId: null,
        SecondLevelManagerEmployeeId: null, EmploymentType: "Full-Time", ContractType: "Unlimited",
        JoiningDate: new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc), ConfirmationDate: null, ProbationStartDate: null,
        ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
        LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null,
        CostCenterReason: reason);

    [Fact]
    public async Task AHireWithAnExplicitCostCentre_DoesNotBarTheHirer_AndKeepsTheReasonOnTheTrail()
    {
        var w = await Seed();
        var hirer = Guid.NewGuid();

        await Service(w.Db).CreateAsync(w.Tenant, Form(w, "H1", w.Own.Id, Reason), Ctx(w, hirer), default);

        (await CostAccountingSod.HasConflictAsync(w.Db, w.Tenant, w.A.Id, Period, hirer, default)).Should().BeFalse();
        var rows = await w.Db.AuditLogs.AsNoTracking().Where(a => a.UserId == hirer && a.Action.StartsWith("employee.cost_")).ToListAsync();
        rows.Should().NotContain(r => r.Action == "employee.cost_centre_override.changed" || r.Action == "employee.cost_attribution_changed");
        rows.Should().Contain(r => r.Action == "employee.cost_attribution_initialised" && r.Metadata!.Contains(Reason));
    }

    [Fact]
    public async Task AReasonIsStillRequired_ForAHireWithAnExplicitCostCentre()
    {
        var w = await Seed();

        var act = () => Service(w.Db).CreateAsync(w.Tenant, Form(w, "H1", w.Own.Id, null), Ctx(w, Guid.NewGuid()), default);

        await act.Should().ThrowAsync<Zayra.Api.Application.Organization.CostCentreRuleException>();
    }

    [Fact]
    public async Task MovingAnExistingEmployeeToAnExplicitCostCentre_StillBarsTheMover()
    {
        var w = await Seed();
        var created = await Service(w.Db).CreateAsync(w.Tenant, Form(w, "H1", null, null), Ctx(w, Guid.NewGuid()), default);
        var mover = Guid.NewGuid();

        await Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "H1", w.Own.Id, Reason), Ctx(w, mover), default);

        (await CostAccountingSod.HasConflictAsync(w.Db, w.Tenant, w.A.Id, Period, mover, default)).Should().BeTrue();
    }
}

file sealed class HireCcStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, Microsoft.AspNetCore.Http.IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
