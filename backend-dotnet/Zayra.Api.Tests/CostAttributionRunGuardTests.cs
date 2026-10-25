using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A, AM-9 (door guard): while a payroll run of the company is awaiting finance review or lock, a change that would
/// alter what that run resolves to is refused with M-7 (409 <c>locked_into_approved_run</c>). S-005 (the WP-A doors), S-042 (the refusal discloses
/// nothing about the run), S-043 (hires, non-attribution edits and people outside the run are not blocked).
/// </summary>
public class CostAttributionRunGuardTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    private sealed class W
    {
        public ZayraDbContext Db = Db();
        public Guid Tenant = Guid.NewGuid();
        public Company A = null!;
        public CostCenter X = null!, Y = null!, Z = null!;
        public Department Dept1 = null!, Dept2 = null!, Dept3 = null!;
        public Position Pos = null!;
        public Employee InRun = null!, OutOfRun = null!, InheritsParent = null!;
        public PayrollRun Run = null!;
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
        w.Pos = new Position { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "P1", Title = "Analyst" };
        w.Db.AddRange(w.A, w.X, w.Y, w.Z, w.Dept1, w.Dept2, w.Dept3, w.Pos);
        Employee Emp(string code, Department d, Guid? position = null) => new()
        {
            TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = code, FullName = code, Status = "Active", JoiningDate = DateTime.UtcNow,
            DepartmentId = d.Id, Department = d.NameEn, PositionId = position,
        };
        w.InRun = Emp("IN", w.Dept1, w.Pos.Id);
        w.OutOfRun = Emp("OUT", w.Dept2);
        w.InheritsParent = Emp("KID", w.Dept3);
        w.Db.AddRange(w.InRun, w.OutOfRun, w.InheritsParent);
        w.Run = new PayrollRun { TenantId = w.Tenant, CompanyId = w.A.Id, Year = 2026, Month = 6, Status = runStatus };
        w.Db.PayrollRuns.Add(w.Run);
        await w.Db.SaveChangesAsync();
        w.Db.PayrollSlips.AddRange(
            new PayrollSlip { TenantId = w.Tenant, CompanyId = w.A.Id, RunId = w.Run.Id, EmployeeId = w.InRun.Id },
            new PayrollSlip { TenantId = w.Tenant, CompanyId = w.A.Id, RunId = w.Run.Id, EmployeeId = w.InheritsParent.Id });
        await w.Db.SaveChangesAsync();
        return w;
    }

    private static OrganizationSetupService Svc(W w) => new(w.Db, new AuditService(w.Db));
    private static DepartmentRequest Dept(W w, Department d, Guid? parent, Guid? cc) =>
        new(null, parent, cc, d.Code, d.NameEn, null, null, true, w.A.Id);

    public static IEnumerable<object[]> Locking() => [["PendingFinanceReview"], ["Approved"]];
    public static IEnumerable<object[]> NotLocking() => [["Processed"], ["Locked"], ["Draft"]];

    private static async Task Locked(Func<Task> act)
    {
        var ex = (await act.Should().ThrowAsync<CostCentreRuleException>()).Which;
        (ex.Code, ex.Status).Should().Be(("locked_into_approved_run", 409));
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_RepointingTheDepartmentOfSomeoneInTheRun_IsRefused_AndWritesNothing(string status)
    {
        var w = await Seed(status);
        await Locked(() => Svc(w).UpdateDepartmentAsync(w.Tenant, w.Dept1.Id, Dept(w, w.Dept1, null, w.Y.Id), Ctx(w.Tenant), default));
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Dept1.Id)).CostCenterId.Should().Be(w.X.Id);
    }

    [Theory]
    [MemberData(nameof(NotLocking))]
    public async Task S005_TheSameChange_Succeeds_OnceNoRunIsAwaitingReviewOrLock(string status)
    {
        var w = await Seed(status);
        var updated = await Svc(w).UpdateDepartmentAsync(w.Tenant, w.Dept1.Id, Dept(w, w.Dept1, null, w.Y.Id), Ctx(w.Tenant), default);
        updated!.CostCenterId.Should().Be(w.Y.Id);
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_ADepartmentNobodyInTheRunBelongsTo_MayStillBeRepointed(string status)
    {
        var w = await Seed(status);
        (await Svc(w).UpdateDepartmentAsync(w.Tenant, w.Dept2.Id, Dept(w, w.Dept2, null, w.Z.Id), Ctx(w.Tenant), default))!.CostCenterId.Should().Be(w.Z.Id);
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_ReparentingIsRefusedOnlyWhenItChangesWhatSomeoneInTheRunResolvesTo(string status)
    {
        var w = await Seed(status);
        // Dept1 has its own cost centre, so its parent never matters: allowed.
        await Svc(w).UpdateDepartmentAsync(w.Tenant, w.Dept1.Id, Dept(w, w.Dept1, w.Dept2.Id, w.X.Id), Ctx(w.Tenant), default);
        // Dept3 has none and inherits from its parent: moving it from Dept1 (X) to Dept2 (Y) moves KID from X to Y.
        await Locked(() => Svc(w).UpdateDepartmentAsync(w.Tenant, w.Dept3.Id, Dept(w, w.Dept3, w.Dept2.Id, null), Ctx(w.Tenant), default));
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_ChangingTheClassOfACostCentreTheRunResolvesTo_IsRefused_ButAnUnusedOneIsNot(string status)
    {
        var w = await Seed(status);
        await Locked(() => Svc(w).UpdateCostCenterAsync(w.Tenant, w.X.Id,
            new CostCenterRequest(w.A.Id, "X", "X", FunctionalClass: "COGS", Reason: "reclassify"), Ctx(w.Tenant), default));
        var ok = await Svc(w).UpdateCostCenterAsync(w.Tenant, w.Z.Id,
            new CostCenterRequest(w.A.Id, "Z", "Z", FunctionalClass: "COGS", Reason: "reclassify"), Ctx(w.Tenant), default);
        ok!.FunctionalClass.Should().Be("COGS");
        // A rename is not a re-class.
        (await Svc(w).UpdateCostCenterAsync(w.Tenant, w.X.Id, new CostCenterRequest(w.A.Id, "X", "X renamed"), Ctx(w.Tenant), default))!.Name.Should().Be("X renamed");
    }

    private static PositionsController Positions(W w) => new(w.Db, new AuditService(w.Db))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, "organization.write", "organization.costcenter.manage") } }
    };

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_GivingAPositionAHeldByTheRunACostCentre_IsRefused(string status)
    {
        var w = await Seed(status);
        var body = new PositionRequest("P1", "Analyst", w.A.Id, null, null, w.Y.Id, null, null, 1m, 0m, "SAR", new DateOnly(2026, 1, 1), null);
        var result = await Positions(w).Update(w.Pos.Id, body, default);
        ((ObjectResult)result).StatusCode.Should().Be(409);
        (await w.Db.Positions.AsNoTracking().SingleAsync(p => p.Id == w.Pos.Id)).CostCenterId.Should().BeNull();
    }

    private static Dictionary<string, JsonElement> Patch(params (string K, string V)[] items) =>
        items.ToDictionary(i => i.K, i => JsonDocument.Parse(JsonSerializer.Serialize(i.V)).RootElement.Clone());

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_MovingSomeoneInTheRunToAnotherDepartment_IsRefused_AndTheEmployeeIsLeftAsItWas(string status)
    {
        var w = await Seed(status);
        await Locked(() => EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, w.InRun, Patch(("department", "Dept Two")), Guid.NewGuid(), default));
        await w.Db.SaveChangesAsync();
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.InRun.Id);
        (stored.DepartmentId, stored.Department).Should().Be((w.Dept1.Id, "Dept One"));
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_PinningACostCentreOnSomeoneInTheRun_IsRefused(string status)
    {
        var w = await Seed(status);
        await Locked(() => EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, w.InRun, Patch(("costCenter", "Z")), Guid.NewGuid(), default));
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S043_PeopleOutsideTheRun_AndEditsThatMoveNoCost_AreNotBlocked(string status)
    {
        var w = await Seed(status);
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, w.OutOfRun, Patch(("department", "Dept One")), Guid.NewGuid(), default);
        w.OutOfRun.DepartmentId.Should().Be(w.Dept1.Id);
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, w.InRun, Patch(("jobTitle", "Senior Analyst")), Guid.NewGuid(), default);
        w.InRun.JobTitle.Should().Be("Senior Analyst");
        // The same department by another spelling is not a move.
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, w.InRun, Patch(("department", "dept one")), Guid.NewGuid(), default);
        w.InRun.DepartmentId.Should().Be(w.Dept1.Id);
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S043_AHire_DuringTheRun_IsNotBlocked(string status)
    {
        var w = await Seed(status);
        var service = new Zayra.Api.Infrastructure.Employees.EmployeeManagementService(w.Db, new AuditService(w.Db), new GuardTestStorage(), TestNotifications.For(w.Db));
        var created = await service.CreateAsync(w.Tenant, NewHire(w), Ctx(w.Tenant), default);
        created.EmployeeCode.Should().Be("NEW-HIRE");
    }

    private static EmployeeCreateRequest NewHire(W w) => new(
        EmployeeCode: "NEW-HIRE", ManualEmployeeCode: true, EnglishName: "New Hire", ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Pakistani", MaritalStatus: null, PersonalEmail: null,
        WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: w.A.Id, BranchId: null, DepartmentId: w.Dept1.Id,
        DesignationId: null, GradeId: null, CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null,
        SecondLevelManagerEmployeeId: null, EmploymentType: "Full-Time", ContractType: "Unlimited",
        JoiningDate: new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc), ConfirmationDate: null, ProbationStartDate: null,
        ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
        LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null);

    [Fact]
    public async Task S042_TheRefusal_NamesTheRealUnlockPath_AndDisclosesNothingAboutTheRun()
    {
        var w = await Seed("Approved");
        var ex = (await FluentActions.Awaiting(() => Svc(w).UpdateDepartmentAsync(w.Tenant, w.Dept1.Id, Dept(w, w.Dept1, null, w.Y.Id), Ctx(w.Tenant), default))
            .Should().ThrowAsync<CostCentreRuleException>()).Which;

        ex.Message.Should().Contain("awaiting finance review or lock").And.Contain("Reopen");
        ex.Message.Should().NotContain("send back", "SendBack only exists from PendingFinanceReview; the real paths are Reopen or Lock");
        ex.MessageAr.Should().NotBeNullOrWhiteSpace();
        var body = JsonSerializer.Serialize(ex.ToPayload());
        body.Should().NotContain(w.Run.Id.ToString()).And.NotContain("2026").And.NotContain("\"IN\"").And.NotContain(w.InRun.Id.ToString());
        body.Should().NotContainEquivalentOf("period").And.NotContainEquivalentOf("total");
    }
}

file sealed class GuardTestStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
