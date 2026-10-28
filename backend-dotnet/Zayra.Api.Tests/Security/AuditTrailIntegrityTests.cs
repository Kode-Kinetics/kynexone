using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Audit-trail integrity (InMemory/unit). Real-Postgres guarantees (concurrency, trigger refusal,
/// erasure path, fork-guard index) live in <c>AuditTrailIntegrityPostgresTests</c>.
/// </summary>
public class AuditTrailIntegrityTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RequestContext Ctx(Guid tenant) => new("10.0.0.1", "agent", Guid.NewGuid(), tenant);

    [Fact]
    public async Task Stage_DoesNotSave_AndCommitsWithTheBusinessChange()
    {
        await using var db = CreateDb();
        var svc = new AuditService(db);

        svc.Stage("thing.changed", "Thing", "1", Ctx(Guid.NewGuid()), null);
        (await db.AuditLogs.CountAsync()).Should().Be(0, "Stage must not write by itself");
        db.ChangeTracker.Entries<AuditLog>().Should().ContainSingle();

        await db.SaveChangesAsync(); // the business SaveChanges
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Stage_AbandonedUnitOfWork_LeavesNoAuditRow()
    {
        await using var db = CreateDb();
        var svc = new AuditService(db);
        svc.Stage("thing.changed", "Thing", "1", Ctx(Guid.NewGuid()), null);
        db.ChangeTracker.Clear(); // the unit of work was abandoned before commit
        await db.SaveChangesAsync();
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ManyStagedRows_InOneSave_FormOneLinearChain()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var svc = new AuditService(db);
        for (var i = 0; i < 25; i++) svc.Stage("a." + i, "Thing", i.ToString(), Ctx(tenant), null);
        await db.SaveChangesAsync();

        var chain = await db.AuditLogs.Where(x => x.TenantId == tenant).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
        chain.Should().HaveCount(25);
        AuditService.VerifyChain(chain).IsValid.Should().BeTrue();
        chain.Select(x => x.PreviousHash).Distinct().Should().HaveCount(25, "no two rows may share a previous_hash (fork)");
    }

    [Fact]
    public async Task WriteAsync_InterleavedAcrossTenants_KeepsEachChainLinear()
    {
        await using var db = CreateDb();
        var svc = new AuditService(db);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        for (var i = 0; i < 6; i++)
        {
            await svc.WriteAsync("x", "T", i.ToString(), Ctx(a), null, CancellationToken.None);
            await svc.WriteAsync("x", "T", i.ToString(), Ctx(b), null, CancellationToken.None);
        }
        foreach (var t in new[] { a, b })
        {
            var chain = await db.AuditLogs.Where(x => x.TenantId == t).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
            AuditService.VerifyChain(chain).IsValid.Should().BeTrue();
        }
    }

    [Fact]
    public void VerifyChain_ReportsFirstBreak_InChainOrder()
    {
        var tenant = Guid.NewGuid();
        var rows = new List<AuditLog>();
        var prev = string.Empty;
        for (var i = 0; i < 4; i++)
        {
            var r = new AuditLog { TenantId = tenant, Action = "a" + i, EntityName = "T", PreviousHash = prev, CreatedAtUtc = DateTime.UtcNow.AddSeconds(i) };
            r.EntryHash = AuditService.ComputeHash(r);
            prev = r.EntryHash;
            rows.Add(r);
        }
        AuditService.VerifyChain(rows).FirstBreak.Should().BeNull();

        rows[2].Action = "tampered";
        var report = AuditService.VerifyChain(rows);
        report.IsValid.Should().BeFalse();
        report.FirstBreak!.AuditLogId.Should().Be(rows[2].Id);
        report.FirstBreak.Reason.Should().Be("entry_hash_mismatch");
    }

    [Fact]
    public async Task PayrollIntegrityEndpoint_WalksTheTenantChain()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            db.PayrollAuditLogs.Add(new PayrollAuditLog { TenantId = tenant, Action = "run." + i, EntityName = "PayrollRun", EntityId = "r", MetadataJson = "{}" });
        db.PayrollAuditLogs.Add(new PayrollAuditLog { TenantId = other, Action = "run.x", EntityName = "PayrollRun", EntityId = "r", MetadataJson = "{}" });
        await db.SaveChangesAsync();

        var report = await CallPayrollIntegrity(db, tenant);
        report.IsValid.Should().BeTrue();
        report.CheckedEntries.Should().Be(3, "another tenant's chain is never walked");
        report.FirstBreak.Should().BeNull();
    }

    [Fact]
    public void VerifyPayrollChain_ReportsTamperedRowAsFirstBreak()
    {
        var tampered = new List<PayrollAuditLog>
        {
            new() { TenantId = Guid.NewGuid(), Seq = 1, Action = "x", EntityName = "PayrollRun", EntityId = "r", MetadataJson = "{}", EntryHash = "deadbeef", PreviousHash = "" }
        };
        AuditService.VerifyPayrollChain(tampered).FirstBreak!.Reason.Should().Be("entry_hash_mismatch");
    }

    // ── H1: bounded, windowed, single-flight verification ───────────────────────────────────────

    private static AuditLogsController Controller(ZayraDbContext db, Guid tenant) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("tenant_id", tenant.ToString())], "test"))
            }
        }
    };

    [Fact]
    public async Task Integrity_StreamsMoreRowsThanOnePage_AndVerifiesTheWholeChain()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var svc = new AuditService(db);
        for (var i = 0; i < 2500; i++) svc.Stage("bulk." + i, "Thing", i.ToString(), Ctx(tenant), null);
        await db.SaveChangesAsync();

        var result = (OkObjectResult)await Controller(db, tenant).Integrity();
        var report = (AuditIntegrityReport)result.Value!;
        report.CheckedEntries.Should().Be(2500, "three keyset pages of up to 1000 rows");
        report.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Integrity_MaxRows_CapsTheWalk()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var svc = new AuditService(db);
        for (var i = 0; i < 30; i++) svc.Stage("a." + i, "Thing", i.ToString(), Ctx(tenant), null);
        await db.SaveChangesAsync();
        var report = (AuditIntegrityReport)((OkObjectResult)await Controller(db, tenant).Integrity(maxRows: 12)).Value!;
        report.CheckedEntries.Should().Be(12);
    }

    [Fact]
    public async Task Integrity_ASecondCallWhileOneIsRunning_Gets409_AndRightAfterOneFinishes_Gets429()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        AuditIntegrityGate.TryBegin(tenant, "central", DateTime.UtcNow).Should().Be(AuditIntegrityGate.Outcome.Started);
        (await Controller(db, tenant).Integrity()).Should().BeOfType<ConflictObjectResult>();

        AuditIntegrityGate.End(tenant, "central", DateTime.UtcNow);
        var tooSoon = await Controller(db, tenant).Integrity();
        tooSoon.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);

        AuditIntegrityGate.TryBegin(tenant, "central", DateTime.UtcNow.AddSeconds(30)).Should().Be(AuditIntegrityGate.Outcome.Started);
    }

    [Fact]
    public void Walker_WindowStartingMidChain_SkipsOnlyTheFirstLink()
    {
        var tenant = Guid.NewGuid();
        var rows = new List<AuditLog>();
        var prev = string.Empty;
        for (var i = 0; i < 5; i++)
        {
            var r = new AuditLog { TenantId = tenant, Action = "a" + i, EntityName = "T", PreviousHash = prev, CreatedAtUtc = DateTime.UtcNow.AddSeconds(i) };
            r.EntryHash = AuditService.ComputeHash(r);
            prev = r.EntryHash;
            rows.Add(r);
        }
        var window = new CentralChainWalker(null);
        foreach (var r in rows.Skip(2)) window.Add(r);
        window.Report().IsValid.Should().BeTrue();

        var wrongStart = new CentralChainWalker(string.Empty);
        foreach (var r in rows.Skip(2)) wrongStart.Add(r);
        wrongStart.Report().FirstBreak!.Reason.Should().Be("previous_hash_mismatch");
    }

    [Fact]
    public void PayrollIntegrity_RequiresAPayrollPermission()
    {
        var attr = typeof(AuditLogsController).GetMethod(nameof(AuditLogsController.PayrollIntegrity))!
            .GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute), false);
        attr.Should().NotBeEmpty();
    }

    // ── M2: a failed save must not leave staged audit rows behind ────────────────────────────────

    private sealed class FailOnceInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Armed = true;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return base.SavingChangesAsync(eventData, result, cancellationToken);
            Armed = false;
            throw new DbUpdateConcurrencyException("injected lost compare-and-swap");
        }
    }

    [Fact]
    public async Task FailedSave_DoesNotLeakItsStagedAuditRow_IntoTheNextSave()
    {
        var interceptor = new FailOnceInterceptor();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(interceptor).Options);
        var tenant = Guid.NewGuid();
        var svc = new AuditService(db);

        svc.Stage("approval.request_decided", "ApprovalRequest", "1", Ctx(tenant), null);
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();

        await db.SaveChangesAsync(); // a later, unrelated save on the same context
        (await db.AuditLogs.CountAsync()).Should().Be(0, "the rolled-back decision must leave no phantom audit record");
        db.ChangeTracker.Entries<AuditLog>().Should().BeEmpty();
    }

    [Fact]
    public async Task DiscardStaged_DropsOnlyUnsavedRows()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var svc = new AuditService(db);
        await svc.WriteAsync("saved", "T", "1", Ctx(tenant), null, CancellationToken.None);
        svc.Stage("staged", "T", "2", Ctx(tenant), null);
        svc.DiscardStaged();
        await db.SaveChangesAsync();
        (await db.AuditLogs.Select(x => x.Action).ToListAsync()).Should().Equal("saved");
    }

    private static async Task<AuditIntegrityReport> CallPayrollIntegrity(ZayraDbContext db, Guid tenant)
    {
        var controller = new AuditLogsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim("tenant_id", tenant.ToString())], "test"))
                }
            }
        };
        var result = (OkObjectResult)await controller.PayrollIntegrity();
        return (AuditIntegrityReport)result.Value!;
    }

    // ── Guard SQL / migration coverage ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryAuditTableInTheModel_IsCoveredByTheAppendOnlyGuard()
    {
        using var db = CreateDb();
        var covered = AuditTrailGuardSql.AuditTables.Append("payroll_audit_logs").ToHashSet();
        var auditTables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => n is not null && (n.EndsWith("_audit_logs") || n == "audit_logs" || n.EndsWith("_audits") || n.EndsWith("_payslip_access_logs")))
            .ToList();
        auditTables.Should().NotBeEmpty();
        auditTables.Should().OnlyContain(t => covered.Contains(t!), "a new audit table must be added to AuditTrailGuardSql.AuditTables");
        AuditTrailGuardSql.AuditTables.Should().OnlyContain(t => db.Model.GetEntityTypes().Any(e => e.GetTableName() == t));
    }

    [Fact]
    public void Migration_InstallsATriggerOnEveryGuardedTable_AndHasADownPath()
    {
        var up = Zayra.Api.Migrations.HardenAuditTrailIntegrity.CreateTriggersSql;
        var down = Zayra.Api.Migrations.HardenAuditTrailIntegrity.DropTriggersSql;
        foreach (var t in AuditTrailGuardSql.AuditTables)
        {
            up.Should().Contain($"'{t}'", $"{t} needs its append-only trigger in the migration");
            down.Should().Contain($"'{t}'");
        }
        var src = File.ReadAllText(FindFile("Migrations", "20261026000100_HardenAuditTrailIntegrity.cs"));
        src.Should().Contain("protected override void Down");
        AuditTrailGuardSql.ForkPrecheckSql.Should().Contain("RAISE EXCEPTION").And.Contain("nothing was modified");
        AuditTrailGuardSql.ForkPrecheckSql.ToUpperInvariant().Should().NotContain("DELETE");
        AuditTrailGuardSql.CreateForkGuardIndexSql.Should().Contain("CONCURRENTLY");
        File.ReadAllText(FindFile("Migrations", "20261026000200_AddAuditChainForkGuardIndex.cs"))
            .Should().Contain("suppressTransaction: true", "a concurrent index build cannot run inside a transaction");
        up.Should().Contain("no_wipe", "a table wipe must be refused too");
        Zayra.Api.Migrations.HardenAuditTrailIntegrity.GenericFunctionSql
            .Should().Contain("= coalesce(to_jsonb(OLD)->>'tenant_id'", "the erasure flag must be scoped to the row's tenant");
    }

    [Fact]
    public void AuditService_StageIsOverridden_SoTheInterfaceNoOpDefaultIsNeverUsedInProduction()
    {
        using var db = CreateDb();
        IAuditService svc = new AuditService(db);
        svc.Stage("a", "T", "1", Ctx(Guid.NewGuid()), null);
        db.ChangeTracker.Entries<AuditLog>().Should().ContainSingle();
    }

    /// <summary>
    /// RATCHET. <c>WriteAsync</c> saves on its own, so a call placed after the business SaveChanges is a
    /// separate transaction (the change can commit without its audit row). Convert such sites to
    /// <c>Stage(...)</c> placed before the business save. The ceiling may only go DOWN; lowering it is
    /// the expected edit when a caller is converted. A site that must stay (database-generated key
    /// needed, or the save sits in a catch-and-continue loop) is part of the remaining count.
    /// </summary>
    private const int MaxUnstagedAuditWriteCallers = 96;

    [Fact]
    public void UnstagedAuditWriteCallers_NeverGrow()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(FindFile("Migrations", "20261026000100_HardenAuditTrailIntegrity.cs")))!;
        var byName = new System.Text.RegularExpressions.Regex(@"\b[\w\.\?]*[aA]udit\w*\.WriteAsync\(");
        var declared = new System.Text.RegularExpressions.Regex(@"IAuditService\??\s+(\w+)");
        var count = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (f.Contains($"{sep}Migrations{sep}") || f.Contains($"{sep}obj{sep}") || f.Contains($"{sep}bin{sep}")) continue;
            var text = File.ReadAllText(f);
            count += byName.Matches(text).Count;
            // an IAuditService held under a name that does not contain "audit" is still counted
            foreach (var name in declared.Matches(text).Select(m => m.Groups[1].Value)
                         .Where(n => !n.Contains("audit", StringComparison.OrdinalIgnoreCase)).Distinct())
                count += System.Text.RegularExpressions.Regex.Matches(text, $@"\b{name}\.WriteAsync\(").Count;
        }
        count.Should().BeLessThanOrEqualTo(MaxUnstagedAuditWriteCallers,
            "new audit writes must use IAuditService.Stage before the business save; see the ratchet note");
    }

    private static string FindFile(string dir, string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var p = Path.Combine(d.FullName, "Zayra.Api", dir, name);
            if (File.Exists(p)) return p;
            d = d.Parent;
        }
        throw new FileNotFoundException(name);
    }
}
