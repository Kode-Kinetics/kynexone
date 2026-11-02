using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>Review fixes for the pay-approval dead-end notice: scope, failure containment, recipients, batching, debounce, bell.</summary>
public sealed partial class EmployeeAccessTests
{
    private sealed class FixedHttp : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class CountingMonitor : IPayApprovalDeadEndMonitor
    {
        public readonly List<(Guid Company, string Trigger)> Calls = [];
        public Task SyncCompanyAsync(Guid tenantId, Guid companyId, string trigger, CancellationToken ct)
        { Calls.Add((companyId, trigger)); return Task.CompletedTask; }
    }

    private sealed class FailOnSave : SaveChangesInterceptor
    {
        public bool Fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("forced save failure");
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private static ClaimsPrincipal CompanyScopedPrincipal(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "security.manage"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "Test"));

    /// <summary>A draft hire whose only pending evidence is the open pay gap, entered by the only reviewer: a caller that cannot see this
    /// company's gaps would see no pending package at all.</summary>
    private async Task<int> AddGapOnlyHireAsync(World w, Guid companyId)
    {
        var hireId = await AddEmployeeAsync(w, $"gap.{Guid.NewGuid():N}@{w.Domain}", EmployeeStatuses.Draft, companyId);
        await using var edit = _fx.CreateDb();
        (await edit.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == hireId)).CreatedBy = w.AdminId;
        edit.EmployeeImportGaps.Add(new EmployeeImportGap
        {
            TenantId = w.TenantId, CompanyId = companyId, ImportBatchId = Guid.Empty, EmployeeId = hireId, RowNumber = 0,
            GapType = CompensationApprovalGate.GapType, GapCategory = "pay", Detail = "awaiting approval",
        });
        await edit.SaveChangesAsync();
        return hireId;
    }

    [Fact]
    public async Task Scope_ACompanyScopedCaller_StillOpensAnotherCompanysNotice()
    {
        var w = await SeedAsync();
        var hireId = await AddGapOnlyHireAsync(w, w.OtherCompanyId);
        var accessor = new FixedHttp { HttpContext = new DefaultHttpContext { User = CompanyScopedPrincipal(w.TenantId, w.CompanyId) } };
        await using var db = _fx.CreateDbWithAccessor(accessor);
        (await db.EmployeeImportGaps.CountAsync(g => g.TenantId == w.TenantId && g.EmployeeId == hireId)).Should().Be(0, "the caller's scope hides the other company's gap");
        var hooks = new PayApprovalDeadEndHooks(db, new PayApprovalDeadEndMonitor(db, accessor, null, NullLogger<PayApprovalDeadEndMonitor>.Instance));
        await hooks.AfterUserChangedAsync(w.TenantId, w.AdminId, "access_change", true);
        (await OpenNoticesAsync(w, w.OtherCompanyId)).Should().ContainSingle(n => n.UserId == w.AdminId);
        (await CompanyAuditAsync(w, w.OtherCompanyId)).Count(a => a == PayApprovalDeadEnds.OpenedAction).Should().Be(1);
    }

    [Fact]
    public async Task Scope_ACompanyScopedCaller_NeverResolvesAnotherCompanysNotice_NorDuplicatesIt()
    {
        var w = await SeedAsync();
        var hireId = await AddGapOnlyHireAsync(w, w.OtherCompanyId);
        await SyncAsync(w, w.OtherCompanyId);
        (await OpenNoticesAsync(w, w.OtherCompanyId)).Should().ContainSingle(n => n.UserId == w.AdminId);

        // The admin's grants reach both companies; the person changing them can see only the first one.
        var accessor = new FixedHttp { HttpContext = new DefaultHttpContext { User = CompanyScopedPrincipal(w.TenantId, w.CompanyId) } };
        await using var db = _fx.CreateDbWithAccessor(accessor);
        (await db.EmployeeImportGaps.CountAsync(g => g.TenantId == w.TenantId && g.EmployeeId == hireId)).Should().Be(0, "the caller's scope hides the other company's gap");
        var monitor = new PayApprovalDeadEndMonitor(db, accessor, null, NullLogger<PayApprovalDeadEndMonitor>.Instance);
        var hooks = new PayApprovalDeadEndHooks(db, monitor);
        await hooks.AfterUserChangedAsync(w.TenantId, w.AdminId, "access_change", true);
        await hooks.AfterUserChangedAsync(w.TenantId, w.AdminId, "access_change", true);

        var notices = await DeadEndNoticesAsync(w, w.OtherCompanyId);
        notices.Should().ContainSingle("no duplicate notice").Which.Status.Should().NotBe(PayApprovalDeadEnds.ResolvedStatus);
        (await CompanyAuditAsync(w, w.OtherCompanyId)).Should().NotContain(PayApprovalDeadEnds.ResolvedAction);

        await using var seen = _fx.CreateDbWithAccessor(accessor);
        (await PayApprovalDeadEnds.EvaluateCompanyAsync(seen, w.TenantId, w.OtherCompanyId, default)).StrandedCount.Should().Be(1);
    }

    [Fact]
    public async Task Sync_AFailingSave_LeavesNothingForTheCallersNextSave_NewOrChanged()
    {
        var w = await SeedAsync();
        await HireWithPayAsync(w, w.AdminId, 42);
        var interceptor = new FailOnSave { Fail = true };
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions).AddInterceptors(interceptor).Options);

        // A new notice that cannot be saved is dropped, not left staged.
        await PayApprovalDeadEnds.SyncTaskAsync(db, w.TenantId, w.CompanyId, w.AdminId, "test", Ctx(w.AdminId, w.TenantId), null, default);
        db.ChangeTracker.HasChanges().Should().BeFalse();
        (await DeadEndNoticesAsync(w)).Should().BeEmpty();

        // An existing notice whose change cannot be saved is put back, so a later save does not carry it.
        interceptor.Fail = false;
        await PayApprovalDeadEnds.SyncTaskAsync(db, w.TenantId, w.CompanyId, w.AdminId, "test", Ctx(w.AdminId, w.TenantId), null, default);
        var original = (await DeadEndNoticesAsync(w)).Single().Message;
        await HireWithPayAsync(w, w.AdminId, 43);
        interceptor.Fail = true;
        await PayApprovalDeadEnds.SyncTaskAsync(db, w.TenantId, w.CompanyId, w.AdminId, "test", Ctx(w.AdminId, w.TenantId), null, default);
        db.ChangeTracker.HasChanges().Should().BeFalse("the changed notice was reverted");
        interceptor.Fail = false;
        await db.SaveChangesAsync();
        (await DeadEndNoticesAsync(w)).Single().Message.Should().Be(original);
    }

    [Fact]
    public async Task Sync_WithNoRecipient_WritesNoOpenedAudit_AndNeverAddressesAnOutsider()
    {
        var w = await SeedAsync();
        await HireWithPayAsync(w, w.AdminId, 44);
        await using (var db = _fx.CreateDb())
        {
            var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == w.AdminId);
            admin.IsActive = false;
            await db.SaveChangesAsync();
        }
        var outsider = await AddUserAsync(w, $"out.{Guid.NewGuid():N}@{w.Domain}", ["Employee"], true, staff: false);

        foreach (var actor in new[] { Guid.NewGuid(), outsider })
        {
            await SyncAsync(w, actor: actor);
            await SyncAsync(w, actor: actor);
        }
        (await DeadEndNoticesAsync(w)).Should().BeEmpty("a platform id or a login that cannot reach the company is never addressed");
        (await CompanyAuditAsync(w)).Should().NotContain(PayApprovalDeadEnds.OpenedAction);

        var insider = await AddUserAsync(w, $"in.{Guid.NewGuid():N}@{w.Domain}", ["Employee"], true, staff: true);
        await SyncAsync(w, actor: insider);
        (await DeadEndNoticesAsync(w)).Should().ContainSingle(n => n.UserId == insider);
        (await CompanyAuditAsync(w)).Count(a => a == PayApprovalDeadEnds.OpenedAction).Should().Be(1);
    }

    [Fact]
    public async Task Bell_HidesAClosedDeadEndNotice_AndReadingItCannotReopenIt()
    {
        var w = await SeedAsync();
        Guid openId, closedId;
        await using (var db = _fx.CreateDb())
        {
            var open = new Notification { TenantId = w.TenantId, UserId = w.AdminId, Title = "t", Message = "m", EntityName = PayApprovalDeadEnds.EntityName, EntityId = w.CompanyId.ToString(), Status = "Unread" };
            var closed = new Notification { TenantId = w.TenantId, UserId = w.AdminId, Title = "t", Message = "m", EntityName = PayApprovalDeadEnds.EntityName, EntityId = w.OtherCompanyId.ToString(), Status = PayApprovalDeadEnds.ResolvedStatus };
            db.Notifications.AddRange(open, closed);
            await db.SaveChangesAsync();
            openId = open.Id; closedId = closed.Id;
        }
        var claims = new[] { new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, w.AdminId.ToString()) };
        await using var ctl = _fx.CreateDb();
        var controller = new NotificationsController(ctl)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
        var items = (IEnumerable<Notification>)((OkObjectResult)await controller.Recent(default)).Value!;
        items.Select(n => n.Id).Should().Contain(openId).And.NotContain(closedId);
        (await controller.MarkRead(closedId, default)).Should().BeOfType<NotFoundResult>();
        await using var verify = _fx.CreateDb();
        (await verify.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == closedId)).Status.Should().Be(PayApprovalDeadEnds.ResolvedStatus);
    }

    [Fact]
    public async Task Batch_ManyRowsOfOneCompany_CheckTheCompanyOnce_AfterTheLoop()
    {
        var w = await SeedAsync();
        var ids = new List<int>();
        for (var i = 0; i < 3; i++) ids.Add(await HireWithPayAsync(w, w.AdminId, 50 + i));
        var monitor = new CountingMonitor();
        await using var db = _fx.CreateDb();
        var hooks = new PayApprovalDeadEndHooks(db, monitor);
        using (var batch = PayApprovalDeadEndHooks.BeginBatch())
        {
            foreach (var id in ids) await hooks.AfterEmployeeChangedAsync(w.TenantId, id, "employee_status");
            monitor.Calls.Should().BeEmpty("nothing is evaluated inside the loop");
            await batch.FlushAsync();
        }
        monitor.Calls.Should().ContainSingle(c => c.Company == w.CompanyId);
        await hooks.AfterEmployeeChangedAsync(w.TenantId, ids[0], "employee_status");
        monitor.Calls.Count.Should().BeGreaterThan(1, "outside a batch each change is checked as before");
    }

    [Fact]
    public async Task Monitor_Debounce_SkipsAHookRepeat_ButNeverTheDecisionOrTheSweep()
    {
        var w = await SeedAsync();
        await HireWithPayAsync(w, w.AdminId, 60);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PayApprovalDeadEndMonitor.DebounceSecondsKey] = "60" }).Build();
        await using var db = _fx.CreateDb();
        var monitor = new PayApprovalDeadEndMonitor(db, new HttpContextAccessor(), config, NullLogger<PayApprovalDeadEndMonitor>.Instance);
        await monitor.SyncCompanyAsync(w.TenantId, w.CompanyId, "access_change", default);
        (await OpenNoticesAsync(w)).Should().HaveCount(1);

        await AddReviewerAsync(w);
        await monitor.SyncCompanyAsync(w.TenantId, w.CompanyId, "access_change", default);
        (await OpenNoticesAsync(w)).Should().HaveCount(1, "a repeat inside the window is skipped");
        await monitor.SyncCompanyAsync(w.TenantId, w.CompanyId, "decision", default);
        (await OpenNoticesAsync(w)).Should().BeEmpty("the decision path always evaluates");
    }

    [Fact]
    public void Rotation_ServesTheLongestUncheckedFirst_AndDebouncesOnlyInsideTheWindow()
    {
        var tenant = Guid.NewGuid();
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;
        PayApprovalDeadEndRotation.Touch(tenant, a, now.AddMinutes(-1));
        PayApprovalDeadEndRotation.Touch(tenant, b, now.AddMinutes(-10));
        PayApprovalDeadEndRotation.OldestFirst(tenant, [a, b, c]).Should().Equal(c, b, a);
        PayApprovalDeadEndRotation.CheckedWithin(tenant, a, TimeSpan.FromSeconds(30), now).Should().BeFalse();
        PayApprovalDeadEndRotation.CheckedWithin(tenant, a, TimeSpan.FromMinutes(5), now).Should().BeTrue();
        PayApprovalDeadEndRotation.CheckedWithin(tenant, a, TimeSpan.Zero, now).Should().BeFalse();
    }
}
