using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// L3.1 on real PostgreSQL: the scheduler puts one job per tenant per TENANT-LOCAL month on the durable queue (a Saudi tenant's month
/// turns at Riyadh midnight), only one instance enqueues, and the real runner drives the job to the accrual and the rollover.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LeaveAccrualSchedulerPostgresTests(PostgresFixture fx)
{
    private sealed class Clock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    private ServiceProvider BuildProvider(DateTime nowUtc)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => fx.CreateDb());
        services.AddSingleton<TimeProvider>(new Clock(nowUtc));
        services.AddSingleton(new LeaveAccrualOptions());
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(2), HeartbeatInterval = TimeSpan.FromSeconds(30) });
        services.AddScoped<ILeaveService>(sp => new LeaveService(sp.GetRequiredService<ZayraDbContext>(), new ApprovalRouter(sp.GetRequiredService<ZayraDbContext>())));
        services.AddSingleton(LeaveAccrualJobHandler.Descriptor);
        services.AddScoped<LeaveAccrualJobHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        services.AddSingleton<LeaveAccrualScheduler>();
        return services.BuildServiceProvider();
    }

    private async Task<Guid> SeedTenantAsync(string country, string? zone, decimal annualDays = 24m)
    {
        await using var db = fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "Accrual Co", CountryCode = country, RegistrationNumber = $"CR-{Guid.NewGuid():N}", IsActive = true };
        var type = new LeaveType { TenantId = tenantId, CompanyId = company.Id, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E1", FullName = "Accrual Employee", Status = "Active",
            JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var policy = new LeavePolicy
        {
            TenantId = tenantId, CompanyId = company.Id, LeaveTypeId = type.Id, Name = "Monthly", Status = "Active", AccrualMethod = "Monthly",
            AnnualEntitlementDays = annualDays, ProratePartialMonths = true, AppliesOnProbation = true,
        };
        db.AddRange(company, type, employee, policy);
        if (zone is not null)
            db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, DefaultTimezone = zone, CountryCode = country });
        await db.SaveChangesAsync();
        return tenantId;
    }

    private async Task<List<string>> KeysAsync(Guid tenantId)
    {
        await using var db = fx.CreateDb();
        return await db.BackgroundJobs.IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId && j.JobType == LeaveAccrualJobHandler.JobType).Select(j => j.IdempotencyKey).ToListAsync();
    }

    /// <summary>The scheduler enqueues for every active tenant of the shared test database; leave none of those jobs behind.</summary>
    private async Task ClearJobsAsync()
    {
        await using var db = fx.CreateDb();
        await db.BackgroundJobs.IgnoreQueryFilters().Where(j => j.JobType == LeaveAccrualJobHandler.JobType).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Scheduler_SkipsWhileAnotherInstanceHoldsTheLease_ThenEnqueuesOneJobPerTenantPerMonth()
    {
        var tenant = await SeedTenantAsync("AE", null);
        await using var sp = BuildProvider(new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc));
        var scheduler = sp.GetRequiredService<LeaveAccrualScheduler>();
        try
        {
            await using (var holder = fx.CreateDb())
            await using (var held = await SingletonWorkerLease.TryAcquireAsync(holder, LeaveAccrualScheduler.LeaseName, default))
            {
                held.Should().NotBeNull();
                (await scheduler.EnqueueDueAsync(default)).Should().Be((SweepOutcome.Skipped, 0));
                (await KeysAsync(tenant)).Should().BeEmpty();
            }

            (await scheduler.EnqueueDueAsync(default)).Outcome.Should().Be(SweepOutcome.Completed);
            await scheduler.EnqueueDueAsync(default);
            await scheduler.EnqueueDueAsync(new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), default);

            (await KeysAsync(tenant)).Should().ContainSingle().Which.Should().Be("leave.accrual:2026-11", "the key is the tenant-local month: one job per tenant per month");
            await scheduler.EnqueueDueAsync(new DateTime(2026, 12, 1, 6, 0, 0, DateTimeKind.Utc), default);
            (await KeysAsync(tenant)).Should().BeEquivalentTo(["leave.accrual:2026-11", "leave.accrual:2026-12"]);
        }
        finally { await ClearJobsAsync(); }
    }

    [Fact]
    public async Task Scheduler_PagesThroughEveryActiveTenant_NotJustTheFirstPage()
    {
        var tenants = new[] { await SeedTenantAsync("AE", null), await SeedTenantAsync("AE", null), await SeedTenantAsync("AE", null) };
        await using var sp = BuildProvider(new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc));
        var scheduler = sp.GetRequiredService<LeaveAccrualScheduler>();
        scheduler.TenantPageSize = 1;
        try
        {
            await scheduler.EnqueueDueAsync(default);

            foreach (var tenant in tenants)
                (await KeysAsync(tenant)).Should().ContainSingle().Which.Should().Be("leave.accrual:2026-11", "a tenant past the first page is still enqueued");
        }
        finally { await ClearJobsAsync(); }
    }

    [Fact]
    public async Task ASaudiTenantsMonthTurnsAtRiyadhMidnight_NotAtUtcMidnight()
    {
        var riyadh = await SeedTenantAsync("SA", "UTC");
        var utc = await SeedTenantAsync("AE", "UTC");
        // 31 Oct 22:00 UTC is already 1 Nov, 01:00 in Riyadh.
        await using var sp = BuildProvider(new DateTime(2026, 10, 31, 22, 0, 0, DateTimeKind.Utc));
        try
        {
            await sp.GetRequiredService<LeaveAccrualScheduler>().EnqueueDueAsync(default);

            (await KeysAsync(riyadh)).Should().ContainSingle().Which.Should().Be("leave.accrual:2026-11");
            (await KeysAsync(utc)).Should().ContainSingle().Which.Should().Be("leave.accrual:2026-10");
        }
        finally { await ClearJobsAsync(); }
    }

    [Fact]
    public async Task TheRealRunner_DrivesTheJob_ToTheAccrualAndTheRollover()
    {
        var tenant = await SeedTenantAsync("AE", null);
        await using var sp = BuildProvider(new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc));
        try
        {
            await RunAsync(sp, tenant, new DateOnly(2026, 11, 3));
            await using (var db = fx.CreateDb())
                (await db.EmployeeLeaveBalances.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant && b.Year == 2026)).Accrued
                    .Should().Be(20m, "January to October");

            await RunAsync(sp, tenant, new DateOnly(2027, 1, 4));
            await using var after = fx.CreateDb();
            var rows = await after.EmployeeLeaveBalances.IgnoreQueryFilters().Where(b => b.TenantId == tenant).ToListAsync();
            rows.Single(b => b.Year == 2026).Available.Should().Be(0m);
            rows.Single(b => b.Year == 2027).CarriedForward.Should().Be(24m, "November and December were accrued, then 2026's 24 unused days were carried");
        }
        finally { await ClearJobsAsync(); }
    }

    private async Task RunAsync(ServiceProvider sp, Guid tenant, DateOnly localToday)
    {
        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>().EnqueueAsync(tenant, LeaveAccrualJobHandler.JobType,
                LeaveAccrualJobHandler.IdempotencyKey(localToday), new LeaveAccrualPayload(localToday), null, default);
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 200; i++)
        {
            await using (var db = fx.CreateDb())
            {
                var jobs = await db.BackgroundJobs.IgnoreQueryFilters()
                    .Where(j => j.TenantId == tenant && j.JobType == LeaveAccrualJobHandler.JobType && j.IdempotencyKey == LeaveAccrualJobHandler.IdempotencyKey(localToday))
                    .Select(j => new { j.Status, j.LastError }).ToListAsync();
                if (jobs.All(j => BackgroundJobStatuses.IsTerminal(j.Status)))
                {
                    jobs.Should().OnlyContain(j => j.Status == BackgroundJobStatuses.Succeeded, string.Join(" | ", jobs.Select(j => j.LastError)));
                    return;
                }
            }
            if (!await runner.RunNextAsync("lacc-test", default, default, [LeaveAccrualJobHandler.JobType]))
                await Task.Delay(50);
        }
        throw new TimeoutException($"Leave accrual job for tenant {tenant} did not finish.");
    }
}
