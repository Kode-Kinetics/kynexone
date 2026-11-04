using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// The "No probation" exception. A hire with the box ticked stores no probation dates and is confirmed from the
/// joining date; a hire without it is stored exactly as before.
/// </summary>
public class ProbationWaivedTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Fx(Guid TenantId, Company Company, Department Dept, Designation Desig);

    private static async Task<Fx> Seed(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Probation", Slug = $"t-{tenantId:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Probation Legal Entity", TradeName = "Probation", CountryCode = "SA",
            Jurisdiction = "test", RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        var dept = new Department { TenantId = tenantId, Code = "OPS", NameEn = "Operations", IsActive = true };
        var desig = new Designation { TenantId = tenantId, Code = "OPS-OFF", TitleEn = "Operations Officer", IsActive = true };
        db.AddRange(company, dept, desig);
        await db.SaveChangesAsync();
        return new Fx(tenantId, company, dept, desig);
    }

    private static EmployeeManagementService Svc(ZayraDbContext db) =>
        new(db, new AuditService(db), new PwDocs(), new PwNotifications(), new EstablishmentGuardService(db));

    private static RequestContext Ctx(Guid tenantId) => new(null, "test", Guid.NewGuid(), tenantId);

    private static readonly DateTime Joining = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    private static EmployeeCreateRequest Req(Fx fx, bool waived, DateOnly? probStart = null, DateOnly? probEnd = null) =>
        new(
            EmployeeCode: null, ManualEmployeeCode: false, EnglishName: "Omar Khan", ArabicName: null,
            PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: "Indian", MaritalStatus: null,
            PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
            CompanyId: fx.Company.Id, BranchId: null, DepartmentId: fx.Dept.Id, DesignationId: fx.Desig.Id,
            GradeId: null, CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null,
            SecondLevelManagerEmployeeId: null, EmploymentType: "Full-time", ContractType: "Unlimited",
            JoiningDate: Joining, ConfirmationDate: null, ProbationStartDate: probStart,
            ProbationEndDate: probEnd, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null,
            ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null,
            PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null, ProbationWaived: waived);

    private static async Task<Employee> Stored(ZayraDbContext db, int id)
    {
        db.ChangeTracker.Clear();
        return await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id);
    }

    [Fact]
    public async Task AWaivedHire_StoresNoProbationDates_AndIsConfirmedFromTheJoiningDate()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);

        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, waived: true), Ctx(fx.TenantId), default);

        var e = await Stored(db, created.Id);
        e.ProbationWaived.Should().BeTrue();
        e.ProbationStartDate.Should().BeNull();
        e.ProbationEndDate.Should().BeNull();
        e.ConfirmationDate.Should().Be(new DateOnly(2026, 11, 9));
    }

    [Fact]
    public async Task AHireWithoutTheExceptionBox_IsStoredAsBefore()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        var end = new DateOnly(2027, 2, 7);

        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, waived: false, probEnd: end), Ctx(fx.TenantId), default);

        var e = await Stored(db, created.Id);
        e.ProbationWaived.Should().BeFalse();
        e.ProbationEndDate.Should().Be(end);
        e.ConfirmationDate.Should().BeNull("confirmation is recorded when probation is completed, never typed at hire");
    }
}

file sealed class PwDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument("f", "t", "u", "p"));
    public string ResolvePath(string storageUrl) => "/tmp";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class PwNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}
