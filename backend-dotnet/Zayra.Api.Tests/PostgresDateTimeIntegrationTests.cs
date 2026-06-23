using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

// ── Postgres container shared across all tests in this class ───────────────────
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        // Build the full schema from the EF model (faster than running all migrations)
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    public ZayraDbContext CreateDbWithAccessor(IHttpContextAccessor accessor) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(ConnectionString)
            .Options,
        accessor);

    // Minimal tenant seed required for import tests (role resolver looks up "Employee" role)
    public static async Task<Guid> SeedMinimalTenant(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "PG Test Tenant", Slug = $"pg-{Guid.NewGuid():N}" };
        var role   = new Role  { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee" };
        db.Tenants.Add(tenant);
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return tenant.Id;
    }
}

// ── Postgres DateTime integration tests ────────────────────────────────────────
[Trait("Category", "Integration")]
public class PostgresDateTimeIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fx;
    public PostgresDateTimeIntegrationTests(PostgresFixture fx) => _fx = fx;

    // ── Regression guard: prove that Kind=Unspecified is REJECTED by Npgsql ──
    // If this starts PASSING (i.e. no exception), it means Npgsql's strict mode
    // was disabled — that would silently allow the original bug back in.
    [Fact]
    public async Task Postgres_KindUnspecified_IsRejectedForTimestamptz()
    {
        await using var db = _fx.CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        await db.SaveChangesAsync();

        db.Employees.Add(new Employee
        {
            TenantId     = tenantId,
            EmployeeCode = $"BAD-{Guid.NewGuid():N}",
            FullName     = "Unspecified Kind",
            // JoiningDate is timestamp with time zone; Kind=Unspecified throws in Npgsql 6+
            JoiningDate  = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Unspecified),
        });

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }

    // ── Confirm Kind=Utc is accepted ──────────────────────────────────────────
    [Fact]
    public async Task Postgres_KindUtc_IsAcceptedForTimestamptz()
    {
        await using var db = _fx.CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T2", Slug = $"t2-{tenantId:N}" });
        await db.SaveChangesAsync();

        db.Employees.Add(new Employee
        {
            TenantId     = tenantId,
            EmployeeCode = $"OK-{Guid.NewGuid():N}",
            FullName     = "UTC Kind",
            JoiningDate  = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        });

        await db.SaveChangesAsync(); // must not throw
        Assert.True(await db.Employees.AnyAsync(e => e.TenantId == tenantId));
    }

    // ── End-to-end: CSV with YYYY-MM-DD dates must succeed on real Postgres ───
    // This is the exact scenario that 500'd on the live import before the fix.
    [Fact]
    public async Task Postgres_CsvImport_DateWithoutTimezone_CreatesEmployeesAndPayrollProfiles()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var ctrl = CreateController(db, tenantId);

        // 5-row sample with YYYY-MM-DD dates (no Z suffix) — the format that was causing 500s
        const string csv =
            "EmployeeCode,FullName,ArabicName,JoiningDate,BasicSalary,HousingAllowance,TransportAllowance,OtherAllowance,Currency,IBAN,BankName,MolId\n" +
            "EMP-PG-001,Ahmed Al-Rashidi,أحمد الراشدي,2024-01-15,8000,2000,1000,500,SAR,SA0380000000608010167519,Al Rajhi Bank,MOL-001\n" +
            "EMP-PG-002,Fatima Al-Zahrani,فاطمة الزهراني,2023-06-01,7000,1500,800,300,SAR,SA0380000000608010167520,Al Rajhi Bank,MOL-002\n" +
            "EMP-PG-003,Mohammed Al-Harbi,محمد الحربي,2022-03-20,9000,2500,1200,400,SAR,SA0380000000608010167521,Al Rajhi Bank,MOL-003\n" +
            "EMP-PG-004,Noura Al-Ghamdi,نورة الغامدي,2021-09-10,6500,1200,700,200,,SA0380000000608010167522,Al Rajhi Bank,\n" +
            "EMP-PG-005,Khalid Al-Dosari,خالد الدوسري,2020-12-05,10000,3000,1500,600,SAR,SA0380000000608010167523,Al Rajhi Bank,MOL-005\n";

        var result = await ctrl.Import(
            new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":5", json);
        Assert.Contains("\"skipped\":0", json);

        // 5 employees created
        var empCount = await db.Employees.CountAsync(e => e.TenantId == tenantId && !e.IsDeleted);
        Assert.Equal(5, empCount);

        // 5 payroll profiles (all rows have salary or IBAN)
        var profileCount = await db.EmployeePayrollProfiles.CountAsync(p => p.TenantId == tenantId);
        Assert.Equal(5, profileCount);

        // 5 salary structures (all rows have BasicSalary > 0)
        var salaryCount = await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == tenantId);
        Assert.Equal(5, salaryCount);

        // Spot-check: EMP-PG-001 totals
        var emp1 = await db.Employees.FirstAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-PG-001");
        Assert.Equal(11500m, emp1.Salary);
        Assert.Equal(DateTimeKind.Utc, emp1.JoiningDate.Kind);

        // Blank Currency row (EMP-PG-004) defaults to SAR
        var emp4 = await db.Employees.FirstAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-PG-004");
        var profile4 = await db.EmployeePayrollProfiles.FirstAsync(p => p.TenantId == tenantId && p.EmployeeId == emp4.Id);
        Assert.Equal("SAR", profile4.SalaryCurrency);
    }

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            new PgFakeDocumentStorage(),
            new NotificationService(db, new PgFakeEmailService(), NullLogger<NotificationService>.Instance),
            new PgFakeHijriDateService(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db),
            new PgFakeLetterService());

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
        }, "Test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return ctrl;
    }
}

file sealed class PgFakeDocumentStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
}

file sealed class PgFakeHijriDateService : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date)
        => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class PgFakeEmailService : IEmailService
{
    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}

file sealed class PgFakeLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
