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
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// A probation longer than the company's own maximum (Setup > Payroll rules) is never silently saved: the first
/// attempt is answered with a warning, and the same request with the acknowledgement goes through and is audited.
/// </summary>
public class EmployeeProbationRuleTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, Guid Company)> SeedAsync(ZayraDbContext db, int? maxProbationDays)
    {
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = $"t-{tenant:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        var company = new Company
        {
            TenantId = tenant, LegalNameEn = "Co", TradeName = "Co", CountryCode = "SA", Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        if (maxProbationDays is { } max)
            db.CompanyPayrollRules.Add(new CompanyPayrollRule { TenantId = tenant, CompanyId = company.Id, RuleKey = PayrollRuleKeys.MaxProbationDays, RuleValue = max.ToString() });
        await db.SaveChangesAsync();
        return (tenant, company.Id);
    }

    private static EmployeeManagementService Svc(ZayraDbContext db) => new(db, new AuditService(db), new Docs(), new Notes());
    private static RequestContext Ctx(Guid tenant) => new(null, "test", Guid.NewGuid(), tenant);

    private static readonly DateOnly Start = new(2026, 1, 1);

    private static EmployeeCreateRequest Req(Guid company, DateOnly? probationEnd, bool ack = false) => new(
        EmployeeCode: null, ManualEmployeeCode: false, EnglishName: "Probation Hire", ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Indian", MaritalStatus: null, PersonalEmail: null, WorkEmail: null,
        MobileNumber: null, ProfilePhotoUrl: null, CompanyId: company, BranchId: null, DepartmentId: null, DesignationId: null,
        GradeId: null, CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
        EmploymentType: "Full-time", ContractType: "Unlimited", JoiningDate: Start.ToDateTime(TimeOnly.MinValue),
        ConfirmationDate: null, ProbationStartDate: Start, ProbationEndDate: probationEnd, NoticePeriodDays: null,
        WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null,
        PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null, AcknowledgeProbationOverLimit: ack);

    [Fact]
    public async Task Create_WithProbationOverTheCompanyMaximum_IsRefusedWithTheNumbers_AndSavesNothing()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, 90);
        // 1 Jan + 119 days = 30 Apr: 120 days counting both ends.
        var act = () => Svc(db).CreateAsync(tenant, Req(company, Start.AddDays(119)), Ctx(tenant), default);

        var ex = (await act.Should().ThrowAsync<ProbationOverLimitException>()).Subject.Single();
        ex.Days.Should().Be(120);
        ex.MaxDays.Should().Be(90);
        (await db.Employees.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_WithTheAcknowledgement_SavesAndAuditsTheOverrun()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, 90);

        var created = await Svc(db).CreateAsync(tenant, Req(company, Start.AddDays(119), ack: true), Ctx(tenant), default);

        created.Id.Should().BeGreaterThan(0);
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "employee.probation_over_limit_acknowledged");
        audit.Metadata.Should().Contain("120").And.Contain("90");
    }

    [Fact]
    public async Task Create_ExactlyAtTheMaximum_NeedsNoAcknowledgement()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, 90);
        // 1 Jan to 31 Mar is 90 days counting both ends.
        (await Svc(db).CreateAsync(tenant, Req(company, Start.AddDays(89)), Ctx(tenant), default)).Id.Should().BeGreaterThan(0);
        (await db.AuditLogs.CountAsync(a => a.Action == "employee.probation_over_limit_acknowledged")).Should().Be(0);
    }

    [Fact]
    public async Task Create_WhenTheCompanySetNoMaximum_IsNeverWarned()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, null);
        (await Svc(db).CreateAsync(tenant, Req(company, Start.AddDays(400)), Ctx(tenant), default)).Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Update_ThatLeavesTheProbationDatesAlone_IsNotBlockedByAnOverlongRecord()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, null);
        var created = await Svc(db).CreateAsync(tenant, Req(company, Start.AddDays(119)), Ctx(tenant), default);
        // The company later sets a maximum the existing record already exceeds.
        db.CompanyPayrollRules.Add(new CompanyPayrollRule { TenantId = tenant, CompanyId = company, RuleKey = PayrollRuleKeys.MaxProbationDays, RuleValue = "90" });
        await db.SaveChangesAsync();

        var same = Req(company, Start.AddDays(119)) with { JobTitle = "Analyst" };
        (await Svc(db).UpdateAsync(tenant, created.Id, same, Ctx(tenant), default)).Should().NotBeNull();

        var longer = Req(company, Start.AddDays(150)) with { JobTitle = "Analyst" };
        var act = () => Svc(db).UpdateAsync(tenant, created.Id, longer, Ctx(tenant), default);
        await act.Should().ThrowAsync<ProbationOverLimitException>("changing the dates to a longer probation is what is checked");
    }

    private sealed class Docs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
    {
        public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument("f", "t", "u", "p"));
        public string ResolvePath(string storageUrl) => "/tmp";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class Notes : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }
}
