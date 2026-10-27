using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// CTO scope decision on strict separation of duties: the INITIAL attribution of a newly created employee (a hire) is recorded as
/// <c>employee.cost_attribution_initialised</c>, which does NOT count toward the Lock/export conflict; only a change to an EXISTING
/// employee's attribution (<c>employee.cost_attribution_changed</c>) does. Otherwise every admin who onboards staff would be barred
/// from locking that month.
/// </summary>
public class CostAttributionHireSodTests
{
    private const string Period = "2026-07";

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Department Ops, Department Fin);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR", CostAccountingEnabled = true };
        var opsCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var finCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "FIN", Name = "Fin" };
        var ops = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPSD", NameEn = "Operations", CostCenterId = opsCc.Id, IsActive = true };
        var fin = new Department { TenantId = tenant, CompanyId = a.Id, Code = "FIND", NameEn = "Finance", CostCenterId = finCc.Id, IsActive = true };
        db.AddRange(a, opsCc, finCc, ops, fin);
        await db.SaveChangesAsync();
        return new World(db, tenant, a, ops, fin);
    }

    private static RequestContext Ctx(World w, Guid user) => new("127.0.0.1", "test", user, w.Tenant);

    private static EmployeeManagementService Service(ZayraDbContext db) => new(db, new AuditService(db), new HireSodStorage(), TestNotifications.For(db));

    private static EmployeeCreateRequest Form(World w, string code, Guid department) => new(
        EmployeeCode: code, ManualEmployeeCode: true, EnglishName: "Hire Test", ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Pakistani", MaritalStatus: null, PersonalEmail: null,
        WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: w.A.Id, BranchId: null, DepartmentId: department,
        DesignationId: null, GradeId: null, CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null,
        SecondLevelManagerEmployeeId: null, EmploymentType: "Full-Time", ContractType: "Unlimited",
        JoiningDate: new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc), ConfirmationDate: null, ProbationStartDate: null,
        ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
        LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null,
        CostCenterReason: null);

    private static Task<bool> Conflict(World w, Guid user) => CostAccountingSod.HasConflictAsync(w.Db, w.Tenant, w.A.Id, Period, user, default);

    [Fact]
    public void TheInitialisationAction_IsNotInTheSodSet_AndTheChangeActionIs()
    {
        CostAccountingSod.RelevantActions.Should().NotContain("employee.cost_attribution_initialised");
        CostAccountingSod.RelevantActions.Should().Contain("employee.cost_attribution_changed");
    }

    [Fact]
    public async Task AnAdminWhoHiresAnEmployee_CanStillLockAndExportThePeriod_WithCostingOn()
    {
        var w = await Seed();
        var admin = Guid.NewGuid();
        await Service(w.Db).CreateAsync(w.Tenant, Form(w, "H1", w.Ops.Id), Ctx(w, admin), default);

        var rows = await w.Db.AuditLogs.AsNoTracking().Where(a => a.Action.StartsWith("employee.cost_attribution")).ToListAsync();
        rows.Should().ContainSingle(r => r.Action == "employee.cost_attribution_initialised" && r.UserId == admin && r.CompanyId == w.A.Id,
            "the hire's first attribution is still on the trail");
        rows.Should().NotContain(r => r.Action == "employee.cost_attribution_changed");
        (await Conflict(w, admin)).Should().BeFalse();
    }

    [Fact]
    public async Task AnAdminWhoMovesAnExistingEmployeesDepartment_IsBarredFromThatPeriod()
    {
        var w = await Seed();
        var hr = Guid.NewGuid();
        var mover = Guid.NewGuid();
        var created = await Service(w.Db).CreateAsync(w.Tenant, Form(w, "H1", w.Ops.Id), Ctx(w, hr), default);

        await Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "H1", w.Fin.Id), Ctx(w, mover), default);

        (await w.Db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "employee.cost_attribution_changed" && a.UserId == mover)).Should().Be(1);
        (await Conflict(w, mover)).Should().BeTrue();
        (await Conflict(w, hr)).Should().BeFalse("hiring is not a change to an existing employee");
    }

    [Fact]
    public async Task AnUpdateThatDoesNotMoveAttribution_EmitsNothing()
    {
        var w = await Seed();
        var u = Guid.NewGuid();
        var created = await Service(w.Db).CreateAsync(w.Tenant, Form(w, "H1", w.Ops.Id), Ctx(w, Guid.NewGuid()), default);
        await Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "H1", w.Ops.Id), Ctx(w, u), default);
        (await Conflict(w, u)).Should().BeFalse();
    }
}

file sealed class HireSodStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, Microsoft.AspNetCore.Http.IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
