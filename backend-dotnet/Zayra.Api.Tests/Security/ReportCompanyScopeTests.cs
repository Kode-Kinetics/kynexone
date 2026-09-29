using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// F10 — company scoping holds on every report path, including the ones with no ambient company filter.
///
/// <para>Two kinds of hole, both real Postgres:</para>
/// <list type="bullet">
/// <item>The scheduled-report worker runs with no HTTP user, so the database's company filter is open
/// for it. Its only restriction was an employee-id list, which recruitment rows (candidates are not
/// employees) and payroll-run selection ignored — a Company A owner was mailed Company B's pipeline,
/// and their payroll register came back empty whenever Company B had run payroll more recently.</item>
/// <item>Reports whose rows are not keyed by employee id either served every company (Saudization
/// listed every Saudi establishment in the tenant) or, for visa, passport and contract expiry, answered
/// a company-scoped user with an empty list that read as "nothing is expiring".</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class ReportCompanyScopeTests
{
    private readonly PostgresFixture _fx;
    public ReportCompanyScopeTests(PostgresFixture fx) => _fx = fx;

    private sealed record World(Guid TenantId, Guid CompanyA, Guid CompanyB, Employee Alpha, Employee Beta);

    [Fact]
    public async Task VisaExpiry_ForACompanyScopedUser_ListsTheirCompanysVisas_NotAnEmptyList()
    {
        var w = await SeedWorldAsync("SAU");
        await using (var seed = _fx.CreateDb())
        {
            var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(25));
            seed.VisaRecords.AddRange(
                new VisaRecord { TenantId = w.TenantId, CompanyId = w.CompanyA, EmployeeId = w.Alpha.PublicId, EmployeeName = w.Alpha.FullName, VisaType = "Residence", VisaNumber = "VA-1", ExpiryDate = expiry, Status = "Active" },
                new VisaRecord { TenantId = w.TenantId, CompanyId = w.CompanyB, EmployeeId = w.Beta.PublicId, EmployeeName = w.Beta.FullName, VisaType = "Residence", VisaNumber = "VB-1", ExpiryDate = expiry, Status = "Active" });
            await seed.SaveChangesAsync();
        }
        var user = ScopedUser(w.TenantId, w.CompanyA, "reports.read", "employees.read", "employees.write", "compliance.read");
        await using var db = _fx.CreateDbWithAccessor(new FixedAccessor { HttpContext = new DefaultHttpContext { User = user } });

        var result = await Controller(db, user).RunReport(new RunReportRequest("compliance.visa-expiry", null), CancellationToken.None);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain("Alpha Employee", "a company-scoped HR user must see their own company's expiring visas");
        json.Should().NotContain("Beta Employee");
    }

    [Fact]
    public async Task Saudization_ForACompanyScopedUser_ListsOnlyTheirEstablishments()
    {
        var w = await SeedWorldAsync("SA");
        var user = ScopedUser(w.TenantId, w.CompanyA, "reports.read", "employees.read", "employees.write", "qiwa.read");
        await using var db = _fx.CreateDbWithAccessor(new FixedAccessor { HttpContext = new DefaultHttpContext { User = user } });

        var result = await Controller(db, user).RunReport(new RunReportRequest("compliance.saudization", null), CancellationToken.None);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(Name(w, "A"));
        json.Should().NotContain(Name(w, "B"), "a sibling establishment's Saudization standing is Company B's business");
    }

    [Fact]
    public async Task ScheduledRecruitmentPipeline_ForACompanyScopedOwner_ExcludesTheSiblingCompany()
    {
        var w = await SeedWorldAsync("SAU");
        await using (var seed = _fx.CreateDb())
        {
            var opening = new JobOpening { TenantId = w.TenantId, JobCode = $"JOB-{Guid.NewGuid():N}", Title = "Engineer", Status = "Open", HeadCount = 2 };
            var candA = new Candidate { TenantId = w.TenantId, CompanyId = w.CompanyA, FirstName = "Cand", LastName = "Alpha", Email = $"a-{Guid.NewGuid():N}@x.test" };
            var candB = new Candidate { TenantId = w.TenantId, CompanyId = w.CompanyB, FirstName = "Cand", LastName = "Beta", Email = $"b-{Guid.NewGuid():N}@x.test" };
            seed.JobOpenings.Add(opening);
            seed.Candidates.AddRange(candA, candB);
            seed.JobApplications.AddRange(
                new JobApplication { TenantId = w.TenantId, CompanyId = w.CompanyA, JobOpeningId = opening.Id, CandidateId = candA.Id, JobTitle = "Engineer", CandidateName = "Cand Alpha", Stage = "AlphaStage", Status = "Active" },
                new JobApplication { TenantId = w.TenantId, CompanyId = w.CompanyB, JobOpeningId = opening.Id, CandidateId = candB.Id, JobTitle = "Engineer", CandidateName = "Cand Beta", Stage = "BetaStage", Status = "Active" });
            await seed.SaveChangesAsync();
        }
        var scheduleId = await SeedScheduleAsync(w, w.CompanyA, "recruitment.pipeline", "recruiting@example.test",
            "reports.schedule", "employees.read", "employees.write", "recruitment.read");

        var (delivered, execution) = await RunWorkerAsync(scheduleId, "recruiting@example.test");

        execution.Status.Should().Be("Success", execution.ErrorMessage);
        delivered.Should().Contain("AlphaStage");
        delivered.Should().NotContain("BetaStage", "the worker has no ambient company filter; the owner is scoped to Company A");
    }

    [Fact]
    public async Task ScheduledPayrollRegister_ForACompanyScopedOwner_ReportsTheirCompanysLatestRun()
    {
        var w = await SeedWorldAsync("SAU");
        await using (var seed = _fx.CreateDb())
        {
            // Company B ran payroll for a LATER month. The worker used to pick "the latest run in the
            // tenant" — Company B's — and filter it down to Company A's employees: an empty register.
            var runA = new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyA, Year = 2026, Month = 7, Status = "Locked" };
            var runB = new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyB, Year = 2026, Month = 8, Status = "Locked" };
            seed.PayrollRuns.AddRange(runA, runB);
            seed.PayrollSlips.AddRange(
                new PayrollSlip { TenantId = w.TenantId, CompanyId = w.CompanyA, RunId = runA.Id, EmployeeId = w.Alpha.Id, EmployeeCode = w.Alpha.EmployeeCode, EmployeeName = w.Alpha.FullName, Department = "HR", BasicSalary = 5000, GrossSalary = 6000, NetSalary = 5800, Status = "Approved" },
                new PayrollSlip { TenantId = w.TenantId, CompanyId = w.CompanyB, RunId = runB.Id, EmployeeId = w.Beta.Id, EmployeeCode = w.Beta.EmployeeCode, EmployeeName = w.Beta.FullName, Department = "HR", BasicSalary = 9500, GrossSalary = 10000, NetSalary = 9700, Status = "Approved" });
            await seed.SaveChangesAsync();
        }
        var scheduleId = await SeedScheduleAsync(w, w.CompanyA, "payroll.register", "payroll@example.test",
            "reports.schedule", "employees.read", "employees.write", "payroll.read");

        var (delivered, execution) = await RunWorkerAsync(scheduleId, "payroll@example.test");

        execution.Status.Should().Be("Success", execution.ErrorMessage);
        delivered.Should().Contain("Alpha Employee");
        delivered.Should().NotContain("Beta Employee");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static string Name(World w, string suffix) => $"Scope {suffix} {w.TenantId:N}";

    private async Task<World> SeedWorldAsync(string countryCode)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var probe = new World(tenantId, Guid.Empty, Guid.Empty, null!, null!);
        var companyA = new Company { TenantId = tenantId, LegalNameEn = Name(probe, "A"), CountryCode = countryCode, RegistrationNumber = $"SA-{Guid.NewGuid():N}", IsActive = true };
        var companyB = new Company { TenantId = tenantId, LegalNameEn = Name(probe, "B"), CountryCode = countryCode, RegistrationNumber = $"SB-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.AddRange(companyA, companyB);
        await db.SaveChangesAsync();

        var alpha = new Employee { TenantId = tenantId, CompanyId = companyA.Id, EmployeeCode = $"SA-{Guid.NewGuid():N}"[..12], FullName = "Alpha Employee", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1), Department = "HR", Nationality = "Saudi", WorkEmail = $"alpha-{Guid.NewGuid():N}@example.test" };
        var beta = new Employee { TenantId = tenantId, CompanyId = companyB.Id, EmployeeCode = $"SB-{Guid.NewGuid():N}"[..12], FullName = "Beta Employee", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1), Department = "HR", Nationality = "Saudi", WorkEmail = $"beta-{Guid.NewGuid():N}@example.test" };
        db.Employees.AddRange(alpha, beta);
        await db.SaveChangesAsync();
        return new World(tenantId, companyA.Id, companyB.Id, alpha, beta);
    }

    /// <summary>A schedule owned by a user granted exactly one company and the given permissions.</summary>
    private async Task<Guid> SeedScheduleAsync(World w, Guid companyId, string reportKey, string recipient, params string[] permissionKeys)
    {
        await using var db = _fx.CreateDb();
        var userId = Guid.NewGuid();
        var role = new Role { TenantId = w.TenantId, Name = $"Scoped {userId:N}", NormalizedName = $"SCOPED {userId:N}" };
        db.Users.Add(new User
        {
            Id = userId, TenantId = w.TenantId, Email = $"{userId:N}@example.test", NormalizedEmail = $"{userId:N}@EXAMPLE.TEST",
            FullName = "Scoped Owner", PasswordHash = "hash", IsActive = true, IsGroupScope = false,
        });
        db.Roles.Add(role);
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
        foreach (var key in permissionKeys)
        {
            var permission = await db.Permissions.FirstOrDefaultAsync(p => p.Key == key);
            if (permission is null)
            {
                permission = new Permission { Key = key, Module = "Test" };
                db.Permissions.Add(permission);
            }
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        }
        db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = w.TenantId, UserId = userId, CompanyId = companyId, Role = "HR", IsActive = true });
        var schedule = new ReportSchedule
        {
            TenantId = w.TenantId, CreatedBy = userId, ReportKey = reportKey, ReportName = reportKey,
            Category = "Test", FiltersJson = "{}", Frequency = "Daily", DeliveryMethod = "Email",
            Recipients = recipient, ExportFormat = "JSON", IsActive = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        db.ReportSchedules.Add(schedule);
        await db.SaveChangesAsync();
        return schedule.Id;
    }

    private async Task<(string Delivered, ReportExecutionLog Execution)> RunWorkerAsync(Guid scheduleId, string recipient)
    {
        // No accessor: the worker has no HTTP user, so the database's company filter is open for it,
        // exactly as in production.
        await using var db = _fx.CreateDb();
        var email = new RecordingEmail();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IEmailService>(email);
        services.AddSingleton<IDataScopeService>(new DataScopeService(db));
        services.AddSingleton<Zayra.Api.Infrastructure.Notifications.INotificationService>(TestNotifications.For(db));
        await using var provider = services.BuildServiceProvider();
        var worker = new ReportScheduleWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);

        var execution = await db.ReportExecutionLogs.AsNoTracking().SingleAsync(x => x.ScheduleId == scheduleId);
        var delivered = email.Messages.Where(m => m.To == recipient)
            .Select(m => Encoding.UTF8.GetString(m.Attachment.Data)).FirstOrDefault() ?? string.Empty;
        return (delivered, execution);
    }

    private static ReportsController Controller(ZayraDbContext db, ClaimsPrincipal user) =>
        new(db, new DataScopeService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    private static ClaimsPrincipal ScopedUser(Guid tenantId, Guid companyId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Scoped HR"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, EmailAttachment Attachment)> Messages { get; } = [];
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            Messages.Add((toAddress, attachments!.Single()));
            return Task.CompletedTask;
        }
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
