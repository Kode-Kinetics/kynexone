using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Payroll run processing on the durable queue, end to end on real PostgreSQL: enqueue through the real
/// controller endpoint, execute on the real runner with the real handler and processor, and prove the lease
/// guarantees — a crash after the commit does not apply twice, a worker whose lease was taken over cannot commit,
/// a failure leaves the run unchanged and a retry applies once.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PayrollRunProcessJobPostgresTests
{
    private readonly PostgresFixture _fx;
    public PayrollRunProcessJobPostgresTests(PostgresFixture fx) => _fx = fx;

    private sealed record Seeded(Guid Tenant, Guid RunId, Guid LoanId, Guid CompanyId, Guid Actor);

    private async Task<Seeded> SeedAsync()
    {
        await using var seed = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        var company = new Company
        {
            TenantId = tenant, LegalNameEn = "Job PG Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"JOB-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        seed.Companies.Add(company);
        var employees = new[] { 10_000m, 12_000m }.Select((basic, i) => new Employee
        {
            TenantId = tenant, EmployeeCode = $"JOB-{i}-{Guid.NewGuid():N}"[..14], FullName = $"Job Employee {i}", Nationality = "Saudi",
            Status = "Active", CompanyId = company.Id, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }).ToList();
        seed.Employees.AddRange(employees);
        await seed.SaveChangesAsync();
        var basics = new[] { 10_000m, 12_000m };
        for (var i = 0; i < employees.Count; i++)
            seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenant, EmployeeId = employees[i].Id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = basics[i], HousingAllowance = 2_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
        var loan = new EmployeeLoan
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = employees[0].Id,
            LoanNumber = $"LN-{Guid.NewGuid():N}", Status = "Active", ApprovedAmount = 5_000m, OutstandingBalance = 5_000m,
            InstallmentAmount = 1_000m, ApprovedInstallments = 5, TotalRepaid = 0m,
        };
        seed.EmployeeLoans.Add(loan);
        var run = new PayrollRun
        {
            TenantId = tenant, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        seed.PayrollRuns.Add(run);
        // The user who queues runs: the job re-checks that they are active and still hold payroll.write.
        var actor = new Zayra.Api.Domain.Entities.User
        {
            TenantId = tenant, Email = $"maker-{Guid.NewGuid():N}@job.test", NormalizedEmail = $"MAKER-{Guid.NewGuid():N}@JOB.TEST",
            FullName = "Payroll Maker", PasswordHash = "x", Status = "Active", AccessMode = "FullPortal", IsActive = true,
            IsEmailConfirmed = true, IsGroupScope = true,
        };
        seed.Users.Add(actor);
        await seed.SaveChangesAsync();
        seed.Set<UserPermissionOverride>().Add(new UserPermissionOverride
        {
            TenantId = tenant, UserId = actor.Id, PermissionKey = "payroll.write", Effect = "Allow", IsActive = true, Reason = "test",
        });
        await seed.SaveChangesAsync();
        return new Seeded(tenant, run.Id, loan.Id, company.Id, actor.Id);
    }

    private ServiceProvider BuildInstance(ThrowOnCommandInterceptor? fault = null)
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m)
            .Set("gosi.saned_rate", 0.0075m).Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar", 45_000m)
            .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => fault is null
            ? _fx.CreateDb()
            : new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_fx.ConnectionString).AddInterceptors(fault).Options));
        services.AddSingleton<IStatutoryRuleReader>(rules);
        services.AddSingleton<ICountryPackResolver>(new KsaPacks(rules));
        services.AddScoped<Zayra.Api.Infrastructure.Governance.ICompanyTaxPolicyResolver, Zayra.Api.Infrastructure.Governance.CompanyTaxPolicyResolver>();
        services.AddScoped<Zayra.Api.Infrastructure.Employees.IEmployeeActivationGuard, Zayra.Api.Infrastructure.Employees.EmployeeActivationGuard>();
        services.AddScoped<ICompanyRatePolicyResolver, CompanyRatePolicyResolver>();
        services.AddScoped<IProrationPolicyResolver, ProrationPolicyResolver>();
        services.AddScoped<Zayra.Api.Application.WorkWeek.IWorkWeekService, Zayra.Api.Infrastructure.WorkWeek.WorkWeekService>();
        services.AddScoped<PayrollRunProcessor>();
        services.AddScoped<PayrollRunProcessJobHandler>();
        services.AddScoped<PayrollRunProcessingService>();
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(1), HeartbeatInterval = TimeSpan.FromSeconds(20) });
        services.AddSingleton(PayrollRunProcessJobHandler.Descriptor);
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        return services.BuildServiceProvider();
    }

    private PayrollController Controller(ZayraDbContext db, Guid tenant, Guid? actor, params string[] permissions)
    {
        var rules = new StubRuleReader();
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, (actor ?? Guid.NewGuid()).ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(db, new Unrestricted(), new HttpContextAccessor { HttpContext = http }, null!,
            new KsaPacks(rules), rules, null!, null!, null!);
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    private async Task<Guid> EnqueueAsync(ServiceProvider sp, Seeded s)
    {
        await using var db = _fx.CreateDb();
        var accepted = Assert.IsType<AcceptedResult>(await Controller(db, s.Tenant, s.Actor, "payroll.write")
            .EnqueueProcess(s.RunId, sp.GetRequiredService<PayrollRunProcessingService>(), default));
        return Assert.IsType<PayrollProcessingAccepted>(accepted.Value).JobId;
    }

    private async Task RunUntilTerminalAsync(ServiceProvider sp, Guid jobId)
    {
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 25; i++)
        {
            await using (var db = _fx.CreateDb())
            {
                var status = await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
                if (BackgroundJobStatuses.IsTerminal(status)) return;
            }
            if (!await runner.RunNextAsync("w", default, default, [PayrollRunProcessJobHandler.JobType]))
                await Task.Delay(100);
        }
        throw new TimeoutException($"Job {jobId} did not finish.");
    }

    private sealed record Totals(int Slips, int Audits, decimal LoanOutstanding, decimal LoanRepaid, string RunStatus, int Checkpoints);

    private async Task<Totals> TotalsAsync(Seeded s, Guid? jobId = null)
    {
        await using var db = _fx.CreateDb();
        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == s.LoanId);
        return new Totals(
            await db.PayrollSlips.CountAsync(x => x.RunId == s.RunId),
            await db.PayrollAuditLogs.CountAsync(a => a.TenantId == s.Tenant && a.Action == "payroll.run.processed"),
            loan.OutstandingBalance, loan.TotalRepaid,
            (await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == s.RunId)).Status,
            jobId is null ? 0 : await db.BackgroundJobItems.CountAsync(i => i.JobId == jobId));
    }

    private async Task<List<string>> SlipsAsync(Seeded s)
    {
        await using var db = _fx.CreateDb();
        return (await db.PayrollSlips.AsNoTracking().Where(x => x.RunId == s.RunId).ToListAsync())
            .Select(x => $"{x.BasicSalary:0.00}|{x.GrossSalary:0.00}|{x.Deductions:0.00}|{x.NetSalary:0.00}|{x.EmployeeStatutoryTotal:0.00}|{x.LoanDeductions:0.00}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    // ── The queued job gives the same result as the in-process path ───────────────────────────────

    [Fact]
    public async Task The_queued_job_produces_the_same_payslips_as_the_in_process_path_and_reports_progress()
    {
        var asyncRun = await SeedAsync();
        var syncRun = await SeedAsync();
        await using var sp = BuildInstance();

        var jobId = await EnqueueAsync(sp, asyncRun);
        Assert.Equal("Queued", (await Status(sp, asyncRun, "payroll.read")).Status);
        await RunUntilTerminalAsync(sp, jobId);

        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await Controller(db, syncRun.Tenant, syncRun.Actor, "payroll.write").Process(syncRun.RunId, default));

        var status = await Status(sp, asyncRun, "payroll.read");
        Assert.Equal("Succeeded", status.Status);
        Assert.Equal((2, 2), (status.ProcessedEmployees, status.TotalEmployees));
        Assert.NotNull(status.FinishedAtUtc);

        Assert.Equal(await SlipsAsync(syncRun), await SlipsAsync(asyncRun));
        var asyncTotals = await TotalsAsync(asyncRun, jobId);
        var syncTotals = await TotalsAsync(syncRun);
        Assert.Equal(syncTotals with { Checkpoints = 1 }, asyncTotals);
        Assert.Equal("Processed", asyncTotals.RunStatus);
        Assert.Equal(4_000m, asyncTotals.LoanOutstanding);

        // Attributed to the user who clicked, in the pass's own audit row.
        await using var verify = _fx.CreateDb();
        var job = await verify.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        var audit = await verify.PayrollAuditLogs.AsNoTracking().SingleAsync(a => a.TenantId == asyncRun.Tenant && a.Action == "payroll.run.processed");
        Assert.Equal(job.CreatedByUserId, audit.UserId);
        var run = await verify.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == asyncRun.RunId);
        Assert.Equal(job.CreatedByUserId, run.ProcessedByUserId);
    }

    private async Task<PayrollProcessingStatus> Status(ServiceProvider sp, Seeded s, string permission)
    {
        await using var db = _fx.CreateDb();
        var ok = Assert.IsType<OkObjectResult>(await Controller(db, s.Tenant, s.Actor, permission)
            .ProcessingStatus(s.RunId, sp.GetRequiredService<PayrollRunProcessingService>(), default));
        return Assert.IsType<PayrollProcessingStatus>(ok.Value);
    }

    [Fact]
    public async Task A_double_post_returns_one_job_and_the_work_is_applied_once()
    {
        var s = await SeedAsync();
        await using var sp = BuildInstance();

        var first = await EnqueueAsync(sp, s);
        var second = await EnqueueAsync(sp, s);
        Assert.Equal(first, second);
        await RunUntilTerminalAsync(sp, first);

        await using var verify = _fx.CreateDb();
        Assert.Equal(1, await verify.BackgroundJobs.CountAsync(j => j.TenantId == s.Tenant));
        var totals = await TotalsAsync(s, first);
        Assert.Equal((2, 1, 4_000m), (totals.Slips, totals.Audits, totals.LoanOutstanding));
    }

    [Fact]
    public async Task Concurrent_posts_for_one_run_create_exactly_one_job()
    {
        var s = await SeedAsync();
        await using var sp = BuildInstance();

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = _fx.CreateDb();
            var service = new PayrollRunProcessingService(db, new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>()));
            var result = await service.EnqueueAsync(s.RunId, new PayrollProcessCaller(s.Tenant, s.Actor, null, true, []), default);
            return result.Accepted!.JobId;
        }));

        Assert.Single(ids.Distinct());
        await using var verify = _fx.CreateDb();
        Assert.Equal(1, await verify.BackgroundJobs.CountAsync(j => j.TenantId == s.Tenant && j.JobType == PayrollRunProcessJobHandler.JobType));
    }

    // ── Failure: the run is left exactly as it was, and a retry applies once ───────────────────────

    [Fact]
    public async Task A_failed_pass_leaves_the_run_unchanged_with_a_safe_message_and_a_retry_applies_exactly_once()
    {
        var s = await SeedAsync();
        var fault = new ThrowOnCommandInterceptor("employee_loans", "UPDATE");
        Guid failedJob;
        await using (var sp = BuildInstance(fault))
        {
            failedJob = await EnqueueAsync(sp, s);
            await RunUntilTerminalAsync(sp, failedJob);

            Assert.True(fault.Fired);
            var status = await Status(sp, s, "payroll.read");
            Assert.Equal("Failed", status.Status);
            Assert.Equal("processing_failed", status.ErrorCode);
            Assert.DoesNotContain("Injected fault", status.ErrorMessage);
            Assert.DoesNotContain("employee_loans", status.ErrorMessage);
            // Progress was written on a separate connection, so it survived the rollback of the pass itself.
            Assert.Equal((2, 2), (status.ProcessedEmployees, status.TotalEmployees));
        }
        var afterFailure = await TotalsAsync(s, failedJob);
        Assert.Equal(new Totals(0, 0, 5_000m, 0m, "Draft", 0), afterFailure);

        fault.Disarm();
        await using var retrySp = BuildInstance();
        var retryJob = await EnqueueAsync(retrySp, s);
        Assert.NotEqual(failedJob, retryJob);
        await RunUntilTerminalAsync(retrySp, retryJob);

        var done = await TotalsAsync(s, retryJob);
        Assert.Equal(new Totals(2, 1, 4_000m, 1_000m, "Processed", 1), done);
    }

    // ── Lease expiry ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_worker_that_committed_and_died_before_completing_is_reclaimed_without_applying_twice()
    {
        var s = await SeedAsync();
        await using var sp = BuildInstance();
        var jobId = await EnqueueAsync(sp, s);
        await RunUntilTerminalAsync(sp, jobId);
        var before = await TotalsAsync(s, jobId);
        var slipsBefore = await SlipsAsync(s);
        Assert.Equal(new Totals(2, 1, 4_000m, 1_000m, "Processed", 1), before);

        // The crash: the pass and its checkpoint committed, but the worker never wrote "Succeeded". Its lease expires.
        await using (var db = _fx.CreateDb())
            await db.BackgroundJobs.IgnoreQueryFilters().Where(j => j.Id == jobId).ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, BackgroundJobStatuses.Running)
                .SetProperty(j => j.LeaseToken, Guid.NewGuid())
                .SetProperty(j => j.LeaseOwner, "dead-worker")
                .SetProperty(j => j.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(-5))
                .SetProperty(j => j.CompletedAtUtc, (DateTime?)null));

        await RunUntilTerminalAsync(sp, jobId);

        await using var verify = _fx.CreateDb();
        var job = await verify.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(BackgroundJobStatuses.Succeeded, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(before, await TotalsAsync(s, jobId));
        Assert.Equal(slipsBefore, await SlipsAsync(s));
    }

    [Fact]
    public async Task A_stalled_worker_whose_lease_was_taken_over_cannot_commit_and_the_new_owner_applies_once()
    {
        var s = await SeedAsync();
        await using var sp = BuildInstance();
        var jobId = await EnqueueAsync(sp, s);
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        var types = new[] { PayrollRunProcessJobHandler.JobType };

        ClaimedBackgroundJob? staleClaim, newClaim;
        await using (var db = _fx.CreateDb())
            staleClaim = await BackgroundJobStore.TryClaimAsync(db, types, "worker-A", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(staleClaim);
        // Worker A stalls past its lease; worker B reclaims, which mints a new lease token.
        await using (var db = _fx.CreateDb())
            await db.BackgroundJobs.IgnoreQueryFilters().Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(-5)));
        await using (var db = _fx.CreateDb())
            newClaim = await BackgroundJobStore.TryClaimAsync(db, types, "worker-B", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(newClaim);
        Assert.NotEqual(staleClaim!.LeaseToken, newClaim!.LeaseToken);

        // A wakes up and runs the pass. It does all the work — and is refused at the fence, so nothing commits.
        await runner.ExecuteClaimedAsync(staleClaim, default, default);

        Assert.Equal(new Totals(0, 0, 5_000m, 0m, "Draft", 0), await TotalsAsync(s, jobId));
        await using (var db = _fx.CreateDb())
        {
            var job = await db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            Assert.Equal(BackgroundJobStatuses.Running, job.Status);
            Assert.Equal(newClaim.LeaseToken, job.LeaseToken);   // A wrote nothing, not even an outcome
        }

        // B, the lease holder, applies it exactly once.
        await runner.ExecuteClaimedAsync(newClaim, default, default);

        Assert.Equal(new Totals(2, 1, 4_000m, 1_000m, "Processed", 1), await TotalsAsync(s, jobId));
        await using var verify = _fx.CreateDb();
        Assert.Equal(BackgroundJobStatuses.Succeeded, (await verify.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status);
    }

    // ── Scope ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_tenant_cannot_read_the_status_and_a_missing_permission_is_visible_on_the_attribute()
    {
        var s = await SeedAsync();
        var other = await SeedAsync();
        await using var sp = BuildInstance();
        await EnqueueAsync(sp, s);

        await using var db = _fx.CreateDb();
        // other's user asking about s's run: the run is not theirs, so there is no status.
        Assert.IsType<NotFoundResult>(await Controller(db, other.Tenant, other.Actor, "payroll.read")
            .ProcessingStatus(s.RunId, sp.GetRequiredService<PayrollRunProcessingService>(), default));
        Assert.IsType<NotFoundResult>(await Controller(db, other.Tenant, other.Actor, "payroll.write")
            .EnqueueProcess(s.RunId, sp.GetRequiredService<PayrollRunProcessingService>(), default));
    }

    // ── doubles ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class Unrestricted : Zayra.Api.Application.Common.IDataScopeService
    {
        public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new Zayra.Api.Application.Common.DataScope
            {
                Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null,
            });
    }

    private sealed class KsaPacks(IStatutoryRuleReader rules) : ICountryPackResolver
    {
        private readonly KsaDeductionCalculator _calc = new(rules);
        public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
            cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
        public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
        public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
        public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
        public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
        public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
    }

    /// <summary>Throws when a command's text contains both needles — a crash after the payslips were written.</summary>
    private sealed class ThrowOnCommandInterceptor(string a, string b) : DbCommandInterceptor
    {
        private volatile bool _armed = true;
        public bool Fired { get; private set; }
        public void Disarm() => _armed = false;

        private void Check(DbCommand command)
        {
            var t = command.CommandText;
            // An UPDATE *statement* on the table. (A bare "UPDATE" substring also matches every SELECT that lists
            // an updated_at column, which would fire before any payslip is built.)
            if (_armed && System.Text.RegularExpressions.Regex.IsMatch(t, $@"(^|;)\s*{b}\s+""?{a}""?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                Fired = true;
                throw new InvalidOperationException("Injected fault: simulated crash during loan-ledger decrement.");
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData e, InterceptionResult<DbDataReader> r)
        { Check(command); return base.ReaderExecuting(command, e, r); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { Check(command); return base.ReaderExecutingAsync(command, e, r, ct); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData e, InterceptionResult<int> r)
        { Check(command); return base.NonQueryExecuting(command, e, r); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        { Check(command); return base.NonQueryExecutingAsync(command, e, r, ct); }
    }
}
