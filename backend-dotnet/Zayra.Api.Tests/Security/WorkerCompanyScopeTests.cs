using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 2 (spec 9): background jobs must respect the company dimension — the Qiwa
/// worker skips employees of deactivated legal entities, and the AI insight engine
/// analyzes payroll per company (stamping AIInsight.CompanyId) instead of blending
/// sibling companies' payroll into one meaningless tenant-wide variance.
/// </summary>
public class WorkerCompanyScopeTests
{
    // ── Qiwa worker: inactive legal entity is skipped, active one is processed ──

    [Fact]
    public async Task QiwaWorker_SkipsEmployeesOfInactiveCompany_ProcessesActiveCompany()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var active = new Company { TenantId = tenantId, LegalNameEn = "Active LLC", IsActive = true };
        var inactive = new Company { TenantId = tenantId, LegalNameEn = "Dormant LLC", IsActive = false };
        db.Companies.AddRange(active, inactive);
        db.Employees.Add(QiwaReadyEmployee(tenantId, 1, active.Id));
        db.Employees.Add(QiwaReadyEmployee(tenantId, 2, inactive.Id));
        db.QiwaTenantConnections.Add(new QiwaTenantConnection { TenantId = tenantId });
        db.QiwaApiCredentials.Add(new QiwaApiCredential { TenantId = tenantId, ClientId = "cid", EncryptedClientSecret = Protect("secret"), Environment = "sandbox" });
        db.QiwaSyncLogs.Add(new QiwaSyncLog { TenantId = tenantId, EmployeeId = 1, Status = QiwaSyncLogStatuses.Pending, Direction = "Push", MaxRetries = 3 });
        db.QiwaSyncLogs.Add(new QiwaSyncLog { TenantId = tenantId, EmployeeId = 2, Status = QiwaSyncLogStatuses.Pending, Direction = "Push", MaxRetries = 3 });
        await db.SaveChangesAsync();

        var spy = new SpyAdapter();
        await CreateWorker(db, spy).ProcessOnceAsync(CancellationToken.None);

        spy.PushedEmployeeCodes.Should().Contain("EMP-001", "the active company's employee syncs normally");
        spy.PushedEmployeeCodes.Should().NotContain("EMP-002",
            "a deactivated legal entity must not keep syncing to Qiwa — its establishment registration is per-company");

        var skipped = await db.QiwaSyncLogs.FirstAsync(l => l.EmployeeId == 2);
        skipped.Status.Should().Be(QiwaSyncLogStatuses.Pending,
            "skip (not dead-letter): the log resumes automatically if the company is reactivated");
    }

    // ── AI insight engine: per-company payroll variance, stamped CompanyId ──────

    [Fact]
    public async Task AiInsightEngine_AnalyzesPayrollVariance_PerCompany_AndStampsCompanyId()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "T", Slug = $"wk-{Guid.NewGuid():N}"[..20] });
        var alpha = Guid.NewGuid();
        var beta = Guid.NewGuid();
        // Alpha: stable payroll (no insight). Beta: +50% spike (insight, stamped Beta).
        for (var m = 1; m <= 4; m++)
        {
            db.PayrollRuns.Add(new PayrollRun { TenantId = tenantId, CompanyId = alpha, Year = 2026, Month = m, Status = "Approved", TotalNetSalary = 100_000m });
            db.PayrollRuns.Add(new PayrollRun { TenantId = tenantId, CompanyId = beta, Year = 2026, Month = m, Status = "Approved", TotalNetSalary = m == 4 ? 150_000m : 100_000m });
        }
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new SingleDbScopeFactory(db), NullLogger<AiInsightEngine>.Instance);
        await engine.AnalyzeTenantAsync(db, llm: null, tenantId, CancellationToken.None);

        var variance = await db.AIInsights.IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId && i.InsightType == "PayrollVariance")
            .ToListAsync();
        variance.Should().ContainSingle("only Beta's series moved — blending companies would have diluted it below threshold");
        variance[0].CompanyId.Should().Be(beta, "company-specific insights must carry their legal entity");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly IDataProtectionProvider Protection = DataProtectionProvider.Create("ZayraTests");

    private static string Protect(string value) =>
        Protection.CreateProtector(QiwaIntegrationService.SecretPurpose).Protect(value);

    private static QiwaSyncWorker CreateWorker(ZayraDbContext db, IQiwaApiAdapter adapter) =>
        new(new SingleDbScopeFactory(db), adapter, new QiwaOAuthTokenCache(), Protection, NullLogger<QiwaSyncWorker>.Instance);

    private static Employee QiwaReadyEmployee(Guid tenantId, int id, Guid companyId) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        EmployeeCode = $"EMP-{id:D3}",
        FullName = "Test Employee",
        Status = "Active",
        SaudiOrNonSaudi = "Saudi",
        IdType = "NationalId",
        IdNumber = "1234567890",
        Nationality = "Saudi",
        OccupationCode = "2421",
        EstablishmentId = "7000123456",
        WorkLocationId = "WL-1",
        ContractReference = "CONTRACT-1",
    };

    private sealed class SpyAdapter : IQiwaApiAdapter
    {
        public List<string> PushedEmployeeCodes { get; } = [];
        public string AdapterName => "spy";
        public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct)
            => Task.FromResult<string?>("spy-token");
        public Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
        {
            PushedEmployeeCodes.Add(payload.EmployeeCode);
            return Task.FromResult(new QiwaApiResult(true, null, null, "{\"status\":\"synced\"}"));
        }
        public Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
            => Task.FromResult(new QiwaApiResult(true, null, null, "{\"status\":\"active\"}"));
    }

    private sealed class SingleDbScopeFactory(ZayraDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DbScope(db);
        private sealed class DbScope(ZayraDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new DbProvider(db);
            public void Dispose() { }
        }
        private sealed class DbProvider(ZayraDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(ZayraDbContext) ? db : null;
        }
    }
}
