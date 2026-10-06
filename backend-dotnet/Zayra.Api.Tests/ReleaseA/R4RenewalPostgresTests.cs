using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Release A slice R4 on real PostgreSQL (the fixture applies the Release A DDL EF cannot model): the case opener runs
/// exactly once under a race and only in its own tenant; the daily job opens, reminds, re-baselines and reconciles;
/// reminders are never sent twice, even concurrently; the scheduler's singleton lease holds; and the database itself
/// refuses a non-Saudi case that offers conversion to indefinite.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class R4RenewalPostgresTests(PostgresFixture fx)
{
    /// <summary>The day the offer for a term ending 31 Dec 2026 is due (end − 60 − 14).</summary>
    private static readonly DateOnly Today = new(2026, 10, 18);

    [Fact]
    public async Task Opener_RunsExactlyOnce_UnderARace_AndOnlyInItsOwnTenant()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await using var one = BuildProvider();
        await using var two = BuildProvider();

        using var go = new ManualResetEventSlim(false);
        Task<RenewalOpenOutcome> Open(ServiceProvider sp) => Task.Run(async () =>
        {
            go.Wait();
            await using var scope = sp.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<RenewalCaseOpener>()
                .OpenOneAsync(a.Tenant, a.Contract, Today, null, "test", default);
        });
        var first = Open(one);
        var second = Open(two);
        go.Set();
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Select(o => o.Result).Should().BeEquivalentTo([RenewalOpenOutcome.Opened, RenewalOpenOutcome.AlreadyOpen]);
        await using (var scope = one.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<RenewalCaseOpener>().OpenDueAsync(a.Tenant, null, Today, null, "test", default))
                .Should().OnlyContain(o => o.Result == RenewalOpenOutcome.AlreadyOpen);
        await RunJobAsync(one, a.Tenant, "again-1");
        await RunJobAsync(one, a.Tenant, "again-2");

        await using var verify = fx.CreateDb();
        (await verify.ContractRenewalCases.IgnoreQueryFilters().CountAsync(c => c.TenantId == a.Tenant)).Should().Be(1);
        (await verify.ContractRenewalCases.IgnoreQueryFilters().CountAsync(c => c.TenantId == b.Tenant))
            .Should().Be(0, "opening tenant A never touches tenant B's contracts");
        (await verify.ComplianceAuditLogs.IgnoreQueryFilters()
            .CountAsync(l => l.TenantId == a.Tenant && l.EntityType == RenewalCaseOpener.AuditEntity && l.Action == "Opened")).Should().Be(1);
    }

    [Fact]
    public async Task DailyJob_OpensTheCase_RemindsHrOnce_RebaselinesAChangedEndDate_AndCancelsWhenTheContractEnds()
    {
        var t = await SeedTenantAsync();
        await using var sp = BuildProvider();

        await RunJobAsync(sp, t.Tenant, "day-1");
        await using (var db = fx.CreateDb())
        {
            var c = await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(x => x.TenantId == t.Tenant);
            c.State.Should().Be(RenewalStates.Open);
            c.AllowedActions.Should().BeEquivalentTo([ContractActions.RenewAsIs, ContractActions.RenewWithChanges, ContractActions.NonRenew]);
            (c.OfferDueOn, c.NoticeDueOn, c.NextHardDeadline).Should().Be(((DateOnly?)Today, (DateOnly?)new DateOnly(2026, 11, 1), (DateOnly?)Today));
            (await ReminderDeliveriesAsync(db, t.Tenant, t.HrManager)).Should().BeGreaterThan(0, "the offer is due today: HR is told");
            (await ReminderDeliveriesAsync(db, t.Tenant, t.HrDirector)).Should().Be(0, "escalation waits until the day after");
        }

        var before = await AllReminderDeliveriesAsync(t.Tenant);
        await RunJobAsync(sp, t.Tenant, "day-1-rerun");
        (await AllReminderDeliveriesAsync(t.Tenant)).Should().Be(before, "a second run on the same day sends nothing new");

        // The contract's end date moves: deadlines are re-baselined, with an audit row.
        await using (var db = fx.CreateDb())
            await db.Database.ExecuteSqlRawAsync("UPDATE employee_contracts SET end_date = '2027-01-31' WHERE id = {0}", t.Contract);
        await RunJobAsync(sp, t.Tenant, "after-end-date-change");
        await using (var db = fx.CreateDb())
        {
            var c = await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(x => x.TenantId == t.Tenant);
            (c.ExpiringEndDate, c.NoticeDueOn).Should().Be((new DateOnly(2027, 1, 31), (DateOnly?)new DateOnly(2026, 12, 2)));
            (await AuditAsync(db, t.Tenant, "Rebaselined")).Should().Be(1);
        }

        // The contract is terminated: the review has nothing left to decide (T21).
        await using (var db = fx.CreateDb())
            await db.Database.ExecuteSqlRawAsync("UPDATE employee_contracts SET status = 'Terminated' WHERE id = {0}", t.Contract);
        await RunJobAsync(sp, t.Tenant, "after-termination");
        await using (var db = fx.CreateDb())
        {
            var c = await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(x => x.TenantId == t.Tenant);
            c.State.Should().Be(RenewalStates.Cancelled);
            c.ClosedAt.Should().NotBeNull();
            (await AuditAsync(db, t.Tenant, "Cancelled")).Should().Be(1);
        }
    }

    [Fact]
    public async Task Reminders_AreNeverDuplicated_EvenWhenTwoInstancesSendAtOnce()
    {
        var t = await SeedTenantAsync();
        await using var one = BuildProvider();
        await using var two = BuildProvider();
        await using (var scope = one.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RenewalCaseOpener>().OpenOneAsync(t.Tenant, t.Contract, Today, null, "test", default);
        RenewalReminder reminder;
        await using (var db = fx.CreateDb())
            reminder = RenewalReminderService.Due(await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(c => c.TenantId == t.Tenant), Today)
                .Single(r => r.Kind == RenewalDeadlineKinds.Offer);

        using var go = new ManualResetEventSlim(false);
        Task Send(ServiceProvider sp) => Task.Run(async () =>
        {
            go.Wait();
            await using var scope = sp.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<RenewalReminderService>().SendAsync(t.Tenant, reminder, default);
        });
        var sends = new[] { Send(one), Send(two), Send(one) };
        go.Set();
        await Task.WhenAll(sends);
        await using (var scope = two.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<RenewalReminderService>().SendAsync(t.Tenant, reminder, default))
                .Enqueued.Should().Be(0);

        await using var verify = fx.CreateDb();
        var perChannel = await verify.NotificationDeliveries.IgnoreQueryFilters()
            .Where(d => d.TenantId == t.Tenant && d.EntityId == reminder.Key)
            .GroupBy(d => new { d.UserId, d.Channel }).Select(g => g.Count()).ToListAsync();
        perChannel.Should().NotBeEmpty().And.OnlyContain(n => n == 1, "each person gets each reminder once per channel");
        (await verify.Notifications.IgnoreQueryFilters().CountAsync(n => n.TenantId == t.Tenant && n.UserId == t.HrManager)).Should().Be(1);
    }

    [Fact]
    public async Task Scheduler_SkipsWhileAnotherInstanceHoldsTheLease_ThenEnqueuesOneJobPerTenantPerDay()
    {
        var t = await SeedTenantAsync();
        await using var sp = BuildProvider();
        var scheduler = sp.GetRequiredService<RenewalCaseScheduler>();

        await using (var holder = fx.CreateDb())
        await using (var held = await SingletonWorkerLease.TryAcquireAsync(holder, RenewalCaseScheduler.LeaseName, default))
        {
            held.Should().NotBeNull();
            (await scheduler.EnqueueDueAsync(default)).Should().Be((SweepOutcome.Skipped, 0));
            (await JobCountAsync(t.Tenant)).Should().Be(0);
        }

        (await scheduler.EnqueueDueAsync(default)).Outcome.Should().Be(SweepOutcome.Completed);
        (await JobCountAsync(t.Tenant)).Should().Be(1);
        await scheduler.EnqueueDueAsync(default);
        (await JobCountAsync(t.Tenant)).Should().Be(1, "the key is the tenant-local date: one job per tenant per day");
    }

    [Fact]
    public async Task Database_RefusesANonSaudiCaseThatOffersConversionToIndefinite()
    {
        var t = await SeedTenantAsync();
        await using var db = fx.CreateDb();
        var contract = await db.EmployeeContracts.IgnoreQueryFilters().SingleAsync(c => c.Id == t.Contract);
        db.ContractRenewalCases.Add(new ContractRenewalCase
        {
            TenantId = t.Tenant, CompanyId = contract.CompanyId, EmployeeId = contract.EmployeeId, ExpiringContractId = contract.Id,
            ExpiringEndDate = contract.EndDate!.Value, WorkerNationalityClass = WorkerNationalityClasses.NonSaudi,
            AllowedActions = [ContractActions.RenewAsIs, ContractActions.ConvertIndefinite], State = RenewalStates.Open,
            NoticeDueOn = new DateOnly(2026, 11, 1), OfferDueOn = Today,
        });
        var act = () => db.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("ck_contract_renewal_cases__non_saudi_never_converts");
    }

    [Fact]
    public async Task HistoryConfirmedWhileHeld_KeepsTheHoldFrozen_AndTheReleaseTakesT2_UnderTheRealTransitionGuard()
    {
        // Joined in 2019, first contract on file starts 2026: the chain is unconfirmed, so the case opens NeedsConfirmation.
        var t = await SeedTenantAsync(new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var sp = BuildProvider();
        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RenewalCaseOpener>().OpenOneAsync(t.Tenant, t.Contract, Today, null, "test", default);
        Guid caseId;
        await using (var db = fx.CreateDb())
        {
            var c = await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(x => x.TenantId == t.Tenant);
            c.State.Should().Be(RenewalStates.NeedsConfirmation);
            caseId = c.Id;
        }

        await using (var db = fx.CreateDb())
        {
            var controller = Controller(db, t.Tenant);
            (await controller.Hold(caseId, new Controllers.Contracts.RenewalHoldRequest(RenewalHoldReasons.Abroad, null), default))
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        }
        await using (var db = fx.CreateDb())
        {
            var result = await Controller(db, t.Tenant).ConfirmChain(t.Contract,
                new Controllers.Contracts.ChainConfirmRequest(null, new DateOnly(2019, 5, 1), WorkerNationalityClasses.NonSaudi, true, null, 6),
                new RenewalCaseOpener(db, new ContractChainCensus(db)), default);
            result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        }
        await using (var db = fx.CreateDb())
        {
            var held = await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(x => x.Id == caseId);
            (held.State, held.HeldFromState).Should().Be((RenewalStates.OnHold, RenewalStates.NeedsConfirmation), "a hold stays frozen while held");
            held.AllowedActions.Should().Contain(ContractActions.RenewAsIs);
            (await Controller(db, t.Tenant).Release(caseId, default)).Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        }
        await using (var db = fx.CreateDb())
            (await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(x => x.Id == caseId)).State.Should().Be(RenewalStates.Open);
    }

    [Fact]
    public async Task SupersedeCarriesTheReview_UnderTheRealTransitionGuard_AndTheJobFollowsTheTerm()
    {
        var t = await SeedTenantAsync();
        await using var sp = BuildProvider();
        await RunJobAsync(sp, t.Tenant, "carry-day-1");
        Guid employee;
        await using (var db = fx.CreateDb())
            employee = (await db.EmployeeContracts.IgnoreQueryFilters().SingleAsync(c => c.Id == t.Contract)).EmployeeId;

        EmployeeContract newVersion;
        await using (var db = fx.CreateDb())
        {
            var contracts = new Controllers.Compliance.ContractsController(db) { ControllerContext = ControllerCtx(t.Tenant) };
            var result = await contracts.Supersede(t.Contract, new Controllers.Compliance.CreateContractRequest(employee, null, null, null,
                new DateOnly(2026, 7, 1), new DateOnly(2026, 12, 31), 4500m, null, null, null, null), default);
            newVersion = (EmployeeContract)result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject.Value!;
        }
        await using (var db = fx.CreateDb())
        {
            var review = await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(c => c.TenantId == t.Tenant);
            (review.State, review.ClosedAt, review.ExpiringContractId).Should().Be((RenewalStates.Open, (DateTime?)null, t.Contract));
        }
        // The next daily run neither cancels the review (its first version is Superseded) nor opens a second one.
        await using (var db = fx.CreateDb())
            await db.Database.ExecuteSqlRawAsync("UPDATE employee_contracts SET status = 'Active' WHERE id = {0}", newVersion.Id);
        await RunJobAsync(sp, t.Tenant, "carry-day-2");
        await using (var db = fx.CreateDb())
        {
            var reviews = await db.ContractRenewalCases.IgnoreQueryFilters().Where(c => c.TenantId == t.Tenant).ToListAsync();
            reviews.Should().ContainSingle().Which.State.Should().Be(RenewalStates.Open);
        }
    }

    [Fact]
    public async Task TheDailyJob_OpensAWaitingReview_OnceTheCensusConfirmsItsHistory()
    {
        // Joined 2019 on file, contract 2026: unconfirmed, the review waits in NeedsConfirmation.
        var t = await SeedTenantAsync(new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var sp = BuildProvider();
        await RunJobAsync(sp, t.Tenant, "wait-1");
        await using (var db = fx.CreateDb())
            (await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(c => c.TenantId == t.Tenant)).State.Should().Be(RenewalStates.NeedsConfirmation);

        // The joining date is corrected to the contract start: the next census links the original term, the job takes T2.
        await using (var db = fx.CreateDb())
            await db.Database.ExecuteSqlRawAsync("UPDATE employees SET joining_date = '2026-01-01' WHERE tenant_id = {0}", t.Tenant);
        await RunJobAsync(sp, t.Tenant, "wait-2");
        await using (var db = fx.CreateDb())
        {
            var review = await db.ContractRenewalCases.IgnoreQueryFilters().SingleAsync(c => c.TenantId == t.Tenant);
            review.State.Should().Be(RenewalStates.Open);
            review.AllowedActions.Should().Contain(ContractActions.RenewAsIs);
            (await AuditAsync(db, t.Tenant, "Rebaselined")).Should().Be(1);
        }
    }

    private static Microsoft.AspNetCore.Mvc.ControllerContext ControllerCtx(Guid tenantId) => new()
    {
        HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "HR Manager"),
            ], "Test")),
        },
    };

    private static Controllers.Contracts.ContractRenewalsController Controller(ZayraDbContext db, Guid tenantId) => new(db, new FixedClock(Today))
    {
        ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                ], "Test")),
            },
        },
    };

    // ── harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record Seeded(Guid Tenant, Guid Contract, Guid HrManager, Guid HrDirector);

    /// <summary>A release_a tenant with one company, an HR Manager and an HR Director (group scope), and one non-Saudi
    /// employee whose first fixed-term contract runs 1 Jan – 31 Dec 2026.</summary>
    private async Task<Seeded> SeedTenantAsync(DateTime? joining = null)
    {
        await using var db = fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "Masar Facility Services", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"CR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true };
        var employee = new Employee { TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "R-1", FullName = "Ramon Dela Cruz",
            Nationality = "Filipino", Status = "Active", JoiningDate = joining ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.AddRange(company, employee, new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        var hrManager = User(tenantId, "hr-manager");
        var hrDirector = User(tenantId, "hr-director");
        var managerRole = new Role { TenantId = tenantId, Name = "HR Manager", NormalizedName = "HR MANAGER", Description = "HR Manager" };
        var directorRole = new Role { TenantId = tenantId, Name = "HR Director", NormalizedName = "HR DIRECTOR", Description = "HR Director" };
        db.AddRange(hrManager, hrDirector, managerRole, directorRole);
        await db.SaveChangesAsync();
        db.UserRoles.AddRange(new UserRole { UserId = hrManager.Id, RoleId = managerRole.Id }, new UserRole { UserId = hrDirector.Id, RoleId = directorRole.Id });
        var contract = new EmployeeContract
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.PublicId, EmployeeName = employee.FullName, ContractNumber = "CON-R4",
            Status = "Active", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), BasicSalary = 4000m, CurrencyCode = "SAR",
        };
        db.EmployeeContracts.Add(contract);
        await db.SaveChangesAsync();
        return new Seeded(tenantId, contract.Id, hrManager.Id, hrDirector.Id);
    }

    private static User User(Guid tenantId, string name) => new()
    {
        TenantId = tenantId, Email = $"{name}-{Guid.NewGuid():N}@example.test", NormalizedEmail = $"{name}-{Guid.NewGuid():N}@EXAMPLE.TEST".ToUpperInvariant(),
        FullName = name, PasswordHash = "x", IsGroupScope = true,
    };

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddScoped(_ => fx.CreateDb());
        services.AddSingleton<ITenantClock>(new FixedClock(Today));
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(2), HeartbeatInterval = TimeSpan.FromSeconds(30) });
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ITenantModuleService, TenantModuleService>();
        services.AddScoped<ContractChainCensus>();
        services.AddScoped<RenewalCaseOpener>();
        services.AddScoped<RenewalReminderService>();
        services.AddSingleton(RenewalCaseJobHandler.Descriptor);
        services.AddScoped<RenewalCaseJobHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        services.AddSingleton<RenewalCaseScheduler>();
        return services.BuildServiceProvider();
    }

    /// <summary>Enqueues one run for <see cref="Today"/> under <paramref name="key"/> and drives the real runner until it succeeds.</summary>
    private async Task RunJobAsync(ServiceProvider sp, Guid tenant, string key)
    {
        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenant, RenewalCaseJobHandler.JobType, key, new RenewalCasePayload(Today), null, default);
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 100; i++)
        {
            await using (var db = fx.CreateDb())
            {
                var jobs = await db.BackgroundJobs.IgnoreQueryFilters()
                    .Where(j => j.TenantId == tenant && j.JobType == RenewalCaseJobHandler.JobType)
                    .Select(j => new { j.Status, j.LastError }).ToListAsync();
                if (jobs.All(j => BackgroundJobStatuses.IsTerminal(j.Status)))
                {
                    jobs.Should().OnlyContain(j => j.Status == BackgroundJobStatuses.Succeeded, string.Join(" | ", jobs.Select(j => j.LastError)));
                    return;
                }
            }
            if (!await runner.RunNextAsync("r4-test", default, default, [RenewalCaseJobHandler.JobType]))
                await Task.Delay(50);
        }
        await using var last = fx.CreateDb();
        var errors = await last.BackgroundJobs.IgnoreQueryFilters()
            .Where(j => j.TenantId == tenant && j.JobType == RenewalCaseJobHandler.JobType).Select(j => j.Status + ": " + j.LastError).ToListAsync();
        throw new TimeoutException($"Renewal job for tenant {tenant} did not finish: {string.Join(" | ", errors)}");
    }

    private async Task<int> JobCountAsync(Guid tenant)
    {
        await using var db = fx.CreateDb();
        return await db.BackgroundJobs.IgnoreQueryFilters().CountAsync(j => j.TenantId == tenant && j.JobType == RenewalCaseJobHandler.JobType);
    }

    private static Task<int> ReminderDeliveriesAsync(ZayraDbContext db, Guid tenant, Guid userId) =>
        db.NotificationDeliveries.IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == tenant && d.EntityName == RenewalReminderService.EntityName && d.UserId == userId);

    private async Task<int> AllReminderDeliveriesAsync(Guid tenant)
    {
        await using var db = fx.CreateDb();
        return await db.NotificationDeliveries.IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == tenant && d.EntityName == RenewalReminderService.EntityName);
    }

    private static Task<int> AuditAsync(ZayraDbContext db, Guid tenant, string action) =>
        db.ComplianceAuditLogs.IgnoreQueryFilters()
            .CountAsync(l => l.TenantId == tenant && l.EntityType == RenewalCaseOpener.AuditEntity && l.Action == action);

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }
}
