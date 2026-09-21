using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// D3 — the data-retention sweep, end to end on real PostgreSQL.
///
/// <para>WHAT THESE TESTS ARE FOR. The mechanism deletes personal data, so the tests have to prove three
/// things that are easy to assert vacuously and worthless if they are:</para>
/// <list type="number">
///   <item><b>The dry run is honest.</b> Not "the run succeeded" — the same seeded row is REPORTED as a
///     candidate and is still byte-for-byte present afterwards.</item>
///   <item><b>Enabling it works.</b> The same row, the same rule, deletions on: gone (or anonymised),
///     with an audit row naming the rule.</item>
///   <item><b>It refuses when statute says keep.</b> A record under the payroll retention floor survives
///     a run with deletions FULLY enabled, and the refusal is recorded with its reason.</item>
/// </list>
///
/// <para>NON-VACUITY. Every assertion about "nothing was purged" is paired with an assertion that the
/// run actually considered a non-zero number of candidates and named the specific seeded id. A purge
/// test that passes because the query matched nothing proves nothing at all.</para>
///
/// <para>Real PostgreSQL, not InMemory: the sweep's behaviour depends on <c>ExecuteDeleteAsync</c>,
/// savepoints inside the runner's transaction, <c>jsonb</c> columns and the queue's
/// <c>FOR UPDATE SKIP LOCKED</c> claim — none of which an in-memory provider models.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class DataRetentionSweepPostgresTests
{
    private readonly PostgresFixture _fx;
    public DataRetentionSweepPostgresTests(PostgresFixture fx) => _fx = fx;

    private static DataRetentionOptions DryRun() => new();

    private static DataRetentionOptions Enabled(bool tenantErasure = false) => new()
    {
        ScheduleEnabled = true,
        ApplyDeletions = true,
        AllowTenantErasure = tenantErasure,
    };

    // ─────────────────────── 1. the dry run is honest ───────────────────────

    [Fact]
    public async Task DryRun_ReportsTheExpiredEmployee_AndLeavesEveryColumnUntouched()
    {
        var (tenant, employeeId) = await SeedExpiredEmployeeAsync();

        var result = await SweepAsync(tenant, DryRun());

        // Non-vacuity first: the run must actually have found this record.
        var audit = await SingleAuditAsync(tenant, RetentionRuleKeys.EmployeeRetentionExpired);
        audit.EntityId.Should().Be(employeeId.ToString());
        audit.Disposition.Should().Be(RetentionDispositions.Anonymise);
        audit.Outcome.Should().Be(RetentionOutcomes.Reported);
        audit.DryRun.Should().BeTrue();
        audit.Reason.Should().Contain("Erasure deadline");
        result.Should().Be(BackgroundJobStatuses.Succeeded);

        // …and then: the record is STILL THERE, unchanged.
        await using var db = _fx.CreateDb();
        var employee = await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == employeeId);
        employee.FullName.Should().Be("Expired Person");
        employee.IqamaNumber.Should().Be("2123456789");
        employee.BankIban.Should().Be("SA0380000000608010167519");
        employee.DateOfBirth.Should().NotBeNull();
        employee.RedactedAtUtc.Should().BeNull();
        employee.PrivacyStatus.Should().Be("RetainedForStatutoryAudit");
    }

    // ─────────────────────── 2. enabling it works ───────────────────────

    [Fact]
    public async Task Enabled_AnonymisesTheExpiredEmployee_KeepsTheRow_AndWritesTheAuditRow()
    {
        var (tenant, employeeId) = await SeedExpiredEmployeeAsync();

        await SweepAsync(tenant, Enabled());

        await using var db = _fx.CreateDb();
        var employee = await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == employeeId);
        employee.FullName.Should().Be(ExpiredEmployeeRecordRule.Cleared);
        employee.IqamaNumber.Should().BeEmpty();
        employee.PassportNumber.Should().BeEmpty();
        employee.BankIban.Should().BeEmpty();
        employee.PersonalEmail.Should().BeEmpty();
        employee.MedicalInformation.Should().BeEmpty();
        employee.DateOfBirth.Should().BeNull();
        employee.EmployeeCode.Should().Be($"ANON-{employeeId}");
        employee.PrivacyStatus.Should().Be(ExpiredEmployeeRecordRule.AnonymisedStatus);
        employee.RedactedAtUtc.Should().NotBeNull();
        // Anonymise, NOT delete: ~130 dependent columns point here with no foreign key to stop a delete.
        employee.JoiningDate.Should().NotBe(default);

        var audit = await SingleAuditAsync(tenant, RetentionRuleKeys.EmployeeRetentionExpired);
        audit.Outcome.Should().Be(RetentionOutcomes.Applied);
        audit.Disposition.Should().Be(RetentionDispositions.Anonymise);
        audit.DryRun.Should().BeFalse();
        audit.DetailsJson.Should().Contain("IqamaNumber").And.Contain("BankIban");
    }

    [Fact]
    public async Task Enabled_RunTwice_IsIdempotent_AndDoesNotWriteASecondDecision()
    {
        var (tenant, employeeId) = await SeedExpiredEmployeeAsync();

        await SweepAsync(tenant, Enabled());
        var firstRedactedAt = await RedactedAtAsync(employeeId);
        firstRedactedAt.Should().NotBeNull();

        // A second sweep the same day dedupes on the idempotency key; a sweep the NEXT day re-evaluates
        // and must find nothing left to do, because the rule skips already-redacted rows.
        await SweepAsync(tenant, Enabled(), asOf: DateTime.UtcNow.AddDays(1));

        (await RedactedAtAsync(employeeId)).Should().Be(firstRedactedAt);
        await using var db = _fx.CreateDb();
        (await db.RetentionPurgeAudits
                .CountAsync(a => a.TenantId == tenant && a.RuleKey == RetentionRuleKeys.EmployeeRetentionExpired))
            .Should().Be(1);
    }

    // ─────────────────────── 3. it refuses when statute says keep ───────────────────────

    [Fact]
    public async Task Enabled_RetainsAnEmployeeWhosePayrollRecordIsStillInsideTheStatutoryFloor()
    {
        var (tenant, employeeId) = await SeedExpiredEmployeeAsync();

        // One payslip dated last month. The employee's own erasure deadline has passed, but the payroll
        // retention floor (payslip date + 7 years) runs until ~2033.
        await using (var seed = _fx.CreateDb())
        {
            seed.Payslips.Add(new Payslip
            {
                TenantId = tenant,
                EmployeeId = employeeId,
                PayrollRunId = Guid.NewGuid(),
                PayslipNumber = "PS-STATUTORY-1",
                CreatedAtUtc = DateTime.UtcNow.AddMonths(-1),
            });
            await seed.SaveChangesAsync();
        }

        // Deletions FULLY enabled — the refusal must come from the rule, not from a switch.
        await SweepAsync(tenant, Enabled(tenantErasure: true));

        await using var db = _fx.CreateDb();
        var employee = await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == employeeId);
        employee.FullName.Should().Be("Expired Person");
        employee.IqamaNumber.Should().Be("2123456789");
        employee.RedactedAtUtc.Should().BeNull();

        var audit = await SingleAuditAsync(tenant, RetentionRuleKeys.EmployeeRetentionExpired);
        audit.Disposition.Should().Be(RetentionDispositions.Retain);
        audit.Outcome.Should().Be(RetentionOutcomes.Retained);
        audit.DryRun.Should().BeFalse();
        audit.Reason.Should().Contain("RETAINED under statute")
            .And.Contain("payroll retention floor");
        audit.DetailsJson.Should().Contain("statutoryFloorUntilUtc");
    }

    // ─────────────────────── refresh tokens: the one true hard delete ───────────────────────

    [Fact]
    public async Task RefreshTokens_AreReportedInADryRunAndDeletedOnlyWhenEnabled()
    {
        var (tenant, userId) = await SeedUserWithDeadTokensAsync();

        await SweepAsync(tenant, DryRun());
        (await TokenCountAsync(userId)).Should().Be(4, "a dry run must not delete a single row");
        var reported = await SingleAuditAsync(tenant, RetentionRuleKeys.RefreshTokenExpired);
        reported.Disposition.Should().Be(RetentionDispositions.HardDelete);
        reported.Outcome.Should().Be(RetentionOutcomes.Reported);
        reported.Reason.Should().Contain("3 refresh token(s)");

        await SweepAsync(tenant, Enabled(), asOf: DateTime.UtcNow.AddDays(1));
        // The live token survives; the three dead ones are gone.
        (await TokenCountAsync(userId)).Should().Be(1);
        await using var db = _fx.CreateDb();
        var applied = await db.RetentionPurgeAudits
            .Where(a => a.TenantId == tenant && a.RuleKey == RetentionRuleKeys.RefreshTokenExpired
                        && a.Outcome == RetentionOutcomes.Applied)
            .SingleAsync();
        applied.DetailsJson.Should().MatchRegex("\"deletedTokens\"\\s*:\\s*3");
    }

    // ─────────────────────── soft-deleted tenants ───────────────────────

    [Fact]
    public async Task SoftDeletedTenantWithNoRecordedDeletionDate_IsRetained_AndItsClockIsStarted()
    {
        var (tenant, employeeId) = await SeedExpiredEmployeeAsync();
        await SoftDeleteTenantAsync(tenant, deletedAtUtc: null);

        // Every switch on. The refusal is because the deletion DATE is unknown, not because of a flag.
        await SweepAsync(tenant, Enabled(tenantErasure: true));

        var audit = await SingleAuditAsync(tenant, RetentionRuleKeys.SoftDeletedTenantExpired);
        audit.Disposition.Should().Be(RetentionDispositions.Retain);
        audit.Outcome.Should().Be(RetentionOutcomes.Retained);
        audit.Reason.Should().Contain("no deletion date was ever recorded");

        await using var db = _fx.CreateDb();
        // Nothing erased…
        (await db.Employees.IgnoreQueryFilters().CountAsync(e => e.Id == employeeId)).Should().Be(1);
        var row = await db.Tenants.SingleAsync(t => t.Id == tenant);
        row.PurgedAtUtc.Should().BeNull();
        // …but the clock now has a provable start, so erasure is at least a full window away.
        row.SoftDeletedAtUtc.Should().NotBeNull();
        row.SoftDeletedAtUtc!.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(-10));
    }

    [Fact]
    public async Task SoftDeletedTenantPastItsWindow_IsErasedOnlyWithAllowTenantErasure_AndKeepsItsEvidence()
    {
        var (tenant, employeeId) = await SeedExpiredEmployeeAsync();
        await SoftDeleteTenantAsync(tenant, deletedAtUtc: DateTime.UtcNow.AddDays(-200));

        // Deletions on, tenant erasure OFF: eligible, and still retained.
        await SweepAsync(tenant, Enabled(tenantErasure: false));
        var blocked = await SingleAuditAsync(tenant, RetentionRuleKeys.SoftDeletedTenantExpired);
        blocked.Disposition.Should().Be(RetentionDispositions.Retain);
        blocked.Reason.Should().Contain("ELIGIBLE but RETAINED").And.Contain("AllowTenantErasure");
        await using (var stillThere = _fx.CreateDb())
            (await stillThere.Employees.IgnoreQueryFilters().CountAsync(e => e.Id == employeeId)).Should().Be(1);

        // Third switch on: erased.
        await SweepAsync(tenant, Enabled(tenantErasure: true), asOf: DateTime.UtcNow.AddDays(1));

        await using var db = _fx.CreateDb();
        (await db.Employees.IgnoreQueryFilters().CountAsync(e => e.Id == employeeId)).Should().Be(0);
        var shell = await db.Tenants.SingleAsync(t => t.Id == tenant);
        shell.PurgedAtUtc.Should().NotBeNull("the tenant shell is kept as a tombstone anchoring the audit trail");
        // The evidence of the erasure survived the erasure — the audit rows are tenant-owned too.
        var evidence = await db.RetentionPurgeAudits
            .Where(a => a.TenantId == tenant && a.RuleKey == RetentionRuleKeys.SoftDeletedTenantExpired)
            .ToListAsync();
        evidence.Should().HaveCount(2);
        evidence.Should().ContainSingle(a => a.Outcome == RetentionOutcomes.Applied
                                             && a.Disposition == RetentionDispositions.HardDelete);
    }

    [Fact]
    public async Task AnInactiveTenantWithoutTheDeletedSlugMarker_IsNeverATenantErasureCandidate()
    {
        var (tenant, employeeId) = await SeedExpiredEmployeeAsync();
        // Suspended, not deleted: inactive but the slug was never renamed. Production has one of these.
        await using (var seed = _fx.CreateDb())
        {
            var row = await seed.Tenants.SingleAsync(t => t.Id == tenant);
            row.IsActive = false;
            row.SoftDeletedAtUtc = DateTime.UtcNow.AddYears(-3);
            await seed.SaveChangesAsync();
        }

        await SweepAsync(tenant, Enabled(tenantErasure: true));

        await using var db = _fx.CreateDb();
        (await db.RetentionPurgeAudits.CountAsync(a => a.TenantId == tenant
                                                       && a.RuleKey == RetentionRuleKeys.SoftDeletedTenantExpired))
            .Should().Be(0, "a suspended tenant must never be mistaken for a deleted one");
        (await db.Tenants.SingleAsync(t => t.Id == tenant)).PurgedAtUtc.Should().BeNull();
        // Non-vacuity: the sweep DID run and DID act on this tenant through another rule.
        (await db.RetentionPurgeAudits.CountAsync(a => a.TenantId == tenant
                                                       && a.RuleKey == RetentionRuleKeys.EmployeeRetentionExpired))
            .Should().Be(1);
        (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == employeeId)).RedactedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ATenantWithNothingExpired_ProducesNoDecisionsAtAll()
    {
        // The control for every "nothing was purged" assertion above: when there is genuinely nothing to
        // do, the sweep succeeds and writes zero rows — so a zero elsewhere means something.
        await using var seed = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        seed.Employees.Add(new Employee
        {
            TenantId = tenant, EmployeeCode = "LIVE-1", FullName = "Live Person", Status = "Active",
            JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await seed.SaveChangesAsync();

        await SweepAsync(tenant, Enabled(tenantErasure: true));

        await using var db = _fx.CreateDb();
        (await db.RetentionPurgeAudits.CountAsync(a => a.TenantId == tenant)).Should().Be(0);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>Enqueues one sweep for the tenant and drains the real runner until it is terminal.</summary>
    private async Task<string> SweepAsync(Guid tenantId, DataRetentionOptions options, DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.UtcNow;
        await using var sp = BuildInstance(options);

        Guid jobId;
        await using (var db = _fx.CreateDb())
        {
            var store = new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>());
            var enqueued = await store.EnqueueAsync(
                tenantId, DataRetentionSweepJobHandler.JobType,
                DataRetentionSweepJobHandler.DefaultIdempotencyKey(now),
                new DataRetentionSweepPayload(now), null, default);
            jobId = enqueued.Job.Id;
        }

        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 40; i++)
        {
            await using (var db = _fx.CreateDb())
            {
                var job = await db.BackgroundJobs.IgnoreQueryFilters()
                    .Where(j => j.Id == jobId).Select(j => new { j.Status, j.LastError }).SingleAsync();
                if (BackgroundJobStatuses.IsTerminal(job.Status))
                {
                    job.Status.Should().Be(BackgroundJobStatuses.Succeeded, because: job.LastError ?? "no error recorded");
                    return job.Status;
                }
            }
            // The job type is shared across tests in this collection; keep draining until OURS finishes.
            if (!await runner.RunNextAsync("w", default, default, [DataRetentionSweepJobHandler.JobType]))
                await Task.Delay(50);
        }
        throw new TimeoutException($"Retention sweep {jobId} did not finish.");
    }

    private ServiceProvider BuildInstance(DataRetentionOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _fx.CreateDb());
        services.AddSingleton(options);
        services.AddSingleton(new BackgroundJobOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(2),
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        });
        services.AddScoped<IRetentionRule, ExpiredEmployeeRecordRule>();
        services.AddScoped<IRetentionRule, ExpiredRefreshTokenRule>();
        services.AddScoped<IRetentionRule, SoftDeletedTenantRule>();
        services.AddSingleton(DataRetentionSweepJobHandler.Descriptor);
        services.AddScoped<DataRetentionSweepJobHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// An employee soft-deleted long enough ago that the 7-year deadline the product itself stamps has
    /// already elapsed — exactly the state <c>SoftDeleteEmployeeAsync</c> leaves behind, aged.
    /// </summary>
    private async Task<(Guid TenantId, int EmployeeId)> SeedExpiredEmployeeAsync()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var deletedAt = DateTime.UtcNow.AddYears(-8);
        var employee = new Employee
        {
            TenantId = tenant,
            EmployeeCode = "EXP-1",
            FullName = "Expired Person",
            ArabicName = "شخص منتهي",
            PersonalEmail = "expired.person@example.test",
            Phone = "+966500000001",
            DateOfBirth = new DateOnly(1985, 4, 12),
            IqamaNumber = "2123456789",
            PassportNumber = "P1234567",
            BankName = "Test Bank",
            BankIban = "SA0380000000608010167519",
            MedicalInformation = "Confidential medical note",
            Status = "Inactive",
            JoiningDate = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDeleted = true,
            DeletedAtUtc = deletedAt,
            PrivacyStatus = "RetainedForStatutoryAudit",
            RetentionUntilUtc = deletedAt.AddYears(7),   // i.e. one year ago — expired
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        employee.RetentionUntilUtc.Should().BeBefore(DateTime.UtcNow, "the fixture must really be expired");
        return (tenant, employee.Id);
    }

    private async Task<(Guid TenantId, Guid UserId)> SeedUserWithDeadTokensAsync()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var user = new User
        {
            TenantId = tenant,
            Email = $"tokens-{Guid.NewGuid():N}@example.test",
            NormalizedEmail = $"TOKENS-{Guid.NewGuid():N}@EXAMPLE.TEST",
            FullName = "Token Holder",
            PasswordHash = "x",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Three well past expiry + grace, one still live.
        for (var i = 1; i <= 3; i++)
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = $"dead-{i}-{Guid.NewGuid():N}",
                ExpiresAtUtc = DateTime.UtcNow.AddDays(-60 - i),
                CreatedAtUtc = DateTime.UtcNow.AddDays(-90),
            });
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = $"live-{Guid.NewGuid():N}",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();
        return (tenant, user.Id);
    }

    /// <summary>Reproduces exactly what <c>PlatformController.DeleteTenant</c> leaves behind.</summary>
    private async Task SoftDeleteTenantAsync(Guid tenantId, DateTime? deletedAtUtc)
    {
        await using var db = _fx.CreateDb();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.IsActive = false;
        tenant.Slug = $"{tenant.Slug}{SoftDeletedTenantRule.DeletedSlugMarker}{tenantId.ToString("N")[..8]}";
        tenant.SoftDeletedAtUtc = deletedAtUtc;
        await db.SaveChangesAsync();
    }

    private async Task<RetentionPurgeAudit> SingleAuditAsync(Guid tenantId, string ruleKey)
    {
        await using var db = _fx.CreateDb();
        var rows = await db.RetentionPurgeAudits
            .Where(a => a.TenantId == tenantId && a.RuleKey == ruleKey)
            .OrderBy(a => a.CreatedAtUtc)
            .ToListAsync();
        rows.Should().ContainSingle($"exactly one decision was expected for {ruleKey} — a zero here would "
                                    + "make every other assertion in this test vacuous");
        return rows[0];
    }

    private async Task<DateTime?> RedactedAtAsync(int employeeId)
    {
        await using var db = _fx.CreateDb();
        return await db.Employees.IgnoreQueryFilters()
            .Where(e => e.Id == employeeId).Select(e => e.RedactedAtUtc).SingleAsync();
    }

    private async Task<int> TokenCountAsync(Guid userId)
    {
        await using var db = _fx.CreateDb();
        return await db.RefreshTokens.CountAsync(t => t.UserId == userId);
    }
}
