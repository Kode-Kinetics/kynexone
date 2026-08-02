using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// EMPLOYEE-LIST BULK MULTI-SELECT + BULK ACTIONS (POST /api/employees/bulk).
/// Proves the four list-level actions and every hard requirement from the SME review:
///  - FLOOR-AWARE activate — a blocked row is SKIPPED, never force-activated (BLOCKER of the whole feature).
///  - BLOCKER 1: a guard-rejected row's half-applied mutation is discarded and never flushed by a later row
///    (change-tracker isolation) — establishment-blocked row leaves Status unchanged + no orphan history.
///  - BLOCKER 2: both selection modes resolve SERVER-SIDE through the same active-population + scope query;
///    an out-of-scope / other-company / former-employee id posted in id-set mode is skipped:forbidden.
///  - BLOCKER 3: explicit selectionMode discriminator; destructive select-all-matching requires ExpectedCount
///    and 409s on drift; batch cap 400s.
///  - Per-action permission gating, per-row failure isolation, idempotent re-run, export respects selection,
///    per-row + batch audit.
/// </summary>
public class EmployeeBulkActionTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Org(
        Guid TenantId, Department Ops, Department Hr, StaffingLevel Mgr,
        Designation OpsMgr, Designation HrStaff, Guid CompanyA, Guid CompanyB);

    /// <summary>Tenant with a CONTROLLED cell (Operations × Manager, budget-limited) and an UNCONTROLLED
    /// cell (HR × HR Staff, no staffing level) so a batch can mix establishment-blocked and clean rows.</summary>
    private static async Task<Org> SeedOrg(ZayraDbContext db, int? managerBudget = null)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee" });
        var ops = new Department { TenantId = tenant.Id, Code = "OPS", NameEn = "Operations", IsActive = true };
        var hr = new Department { TenantId = tenant.Id, Code = "HR", NameEn = "Human Resources", IsActive = true };
        var mgr = new StaffingLevel { TenantId = tenant.Id, Code = "MGR", NameEn = "Manager", NameAr = "مدير", Rank = 2 };
        var opsMgr = new Designation { TenantId = tenant.Id, Code = "OPS-MGR", TitleEn = "Operations Manager", IsActive = true, StaffingLevelId = mgr.Id };
        var hrStaff = new Designation { TenantId = tenant.Id, Code = "HR-STF", TitleEn = "HR Staff", IsActive = true };
        db.AddRange(ops, hr, mgr, opsMgr, hrStaff);
        if (managerBudget is not null)
            db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget
            {
                TenantId = tenant.Id, DepartmentId = ops.Id, StaffingLevelId = mgr.Id, BudgetedHeadcount = managerBudget.Value
            });
        await db.SaveChangesAsync();
        return new Org(tenant.Id, ops, hr, mgr, opsMgr, hrStaff, Guid.NewGuid(), Guid.NewGuid());
    }

    /// <summary>KSA expat. complete=true adds GOSI + Iqama (the code-floor blockers) so the activation gate
    /// passes; complete=false is readiness-blocked. Status defaults Draft (non-occupying).</summary>
    private static Employee Emp(ZayraDbContext db, Org o, bool complete, string status = "Draft",
        Department? dept = null, Designation? desig = null, Guid? companyId = null, string? code = null)
    {
        dept ??= o.Hr; desig ??= o.HrStaff;
        var e = new Employee
        {
            TenantId = o.TenantId, EmployeeCode = code ?? $"E-{Guid.NewGuid():N}"[..12],
            FullName = "Bulk Person", EnglishName = "Bulk Person", CountryCode = "SA", Nationality = "Indian",
            Status = status, JoiningDate = DateTime.UtcNow,
            DepartmentId = dept.Id, Department = dept.NameEn, DesignationId = desig.Id, Designation = desig.TitleEn,
            CompanyId = companyId,
        };
        if (complete) { e.GosiReference = "GOSI-1"; e.IqamaNumber = "2000000001"; }
        db.Employees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new FakeDocs(), new FakeNotifications(), new EstablishmentGuardService(db));

    private static readonly string[] AllBulkPerms = { "employees.approve", "employees.write", "employees.delete", "organization.establishment.write" };

    private static EmployeesController Controller(ZayraDbContext db, Guid tenantId,
        string[]? permissions = null, string role = "Admin", Guid? scopedCompany = null)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), audit, new FakeDocs(),
            new FakeNotifications(), new FakeHijri(), new DataScopeService(db), new FakeLetters(),
            new ApprovalWorkflowService(db, audit), NullLogger<EmployeesController>.Instance, new EstablishmentGuardService(db));

        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
        };
        foreach (var p in permissions ?? AllBulkPerms) claims.Add(new Claim("permission", p));
        if (scopedCompany is Guid c)
            // Company-scoped token (entity_scope v2, companies mode) — no group grant.
            claims.Add(new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { c.ToString() } })));
        else
            claims.Add(new Claim("is_group_scope", "true"));

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static EmployeesController.BulkActionRequest Ids(string action, IEnumerable<int> ids,
        string? reason = null, string? targetStatus = null) =>
        new(action, "ids", ids.ToArray(), null, reason, targetStatus, null);

    private static async Task<EmployeesController.BulkActionResult> RunOk(EmployeesController c, EmployeesController.BulkActionRequest req, ZayraDbContext db)
    {
        var res = await c.BulkAction(req, Service(db), CancellationToken.None);
        return (EmployeesController.BulkActionResult)res.Should().BeOfType<OkObjectResult>().Subject.Value!;
    }

    // ── FLOOR-AWARE activate — the law ────────────────────────────────────────

    [Fact]
    public async Task Activate_MixedBatch_SkipsIncomplete_NeverForceActivates()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var ready = Emp(db, o, complete: true);            // uncontrolled cell, activatable
        var blocked = Emp(db, o, complete: false);         // missing GOSI/Iqama → readiness-blocked

        var result = await RunOk(Controller(db, o.TenantId), Ids("activate", new[] { ready.Id, blocked.Id }), db);

        result.Succeeded.Should().Be(1);
        result.Skipped.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Summary.Should().Contain("1 activated").And.Contain("skipped (incomplete:");

        var blockedItem = result.Items.Single(i => i.EmployeeId == blocked.Id);
        blockedItem.Outcome.Should().Be("skipped");
        blockedItem.Reason.Should().Be("incomplete");
        blockedItem.Blocking.Should().NotBeNullOrEmpty("the checklist UI is driven by the per-row blocking items");

        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == ready.Id)).Status.Should().Be("Active");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == blocked.Id)).Status
            .Should().Be("Draft", "a floor-blocked row is skipped, NEVER force-activated");
    }

    // ── BLOCKER 1: change-tracker isolation across rows ───────────────────────

    [Fact]
    public async Task Activate_EstablishmentBlockedRow_IsSkipped_LeavesStatusUnchanged_NoOrphanHistory()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db, managerBudget: 1);
        Emp(db, o, complete: true, status: "Active", dept: o.Ops, desig: o.OpsMgr); // incumbent fills the only Manager seat

        // Over-budget row is readiness-COMPLETE (so it clears the floor and reaches the establishment guard,
        // which mutates Status IN-TRACKER before throwing) — the exact BLOCKER-1 contamination shape.
        var overBudget = Emp(db, o, complete: true, status: "Draft", dept: o.Ops, desig: o.OpsMgr);
        var clean = Emp(db, o, complete: true, status: "Draft"); // uncontrolled cell, activatable

        // Blocked row FIRST so its dirty mutation would poison the clean row's SaveChanges if not isolated.
        var result = await RunOk(Controller(db, o.TenantId), Ids("activate", new[] { overBudget.Id, clean.Id }), db);

        result.Succeeded.Should().Be(1);
        result.Skipped.Should().Be(1);
        result.Items.Single(i => i.EmployeeId == overBudget.Id).Reason.Should().Be("establishment_budget_exceeded");

        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == clean.Id)).Status.Should().Be("Active");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == overBudget.Id)).Status
            .Should().Be("Draft", "the establishment-rejected row's half-applied Status=Active must never be flushed by a later row");
        (await db.EmployeeStatusHistories.AsNoTracking().AnyAsync(h => h.EmployeeId == overBudget.Id))
            .Should().BeFalse("the rejected row must leave no orphan status-history rows");
    }

    [Fact]
    public async Task PerRow_Failure_Isolation_OthersStillCommit()
    {
        // A failing row (establishment) between two clean rows must not roll back the others.
        await using var db = CreateDb();
        var o = await SeedOrg(db, managerBudget: 1);
        Emp(db, o, complete: true, status: "Active", dept: o.Ops, desig: o.OpsMgr);
        var a = Emp(db, o, complete: true);
        var fail = Emp(db, o, complete: true, status: "Draft", dept: o.Ops, desig: o.OpsMgr);
        var b = Emp(db, o, complete: true);

        var result = await RunOk(Controller(db, o.TenantId), Ids("activate", new[] { a.Id, fail.Id, b.Id }), db);

        result.Succeeded.Should().Be(2);
        result.Skipped.Should().Be(1);
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == a.Id)).Status.Should().Be("Active");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == b.Id)).Status.Should().Be("Active");
    }

    // ── BLOCKER 2: server-side scope resolution ───────────────────────────────

    [Fact]
    public async Task IdSet_OtherCompanyId_IsSkippedForbidden_NeverActedOn()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var mine = Emp(db, o, complete: true, companyId: o.CompanyA);
        var sibling = Emp(db, o, complete: true, companyId: o.CompanyB);

        // Caller is scoped to company A only.
        var controller = Controller(db, o.TenantId, scopedCompany: o.CompanyA);
        var result = await RunOk(controller, Ids("activate", new[] { mine.Id, sibling.Id }), db);

        result.Items.Single(i => i.EmployeeId == sibling.Id).Reason.Should().Be("forbidden");
        result.Succeeded.Should().Be(1);
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == mine.Id)).Status.Should().Be("Active");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == sibling.Id)).Status
            .Should().Be("Draft", "an id outside the caller's company boundary must never be acted on");
    }

    [Fact]
    public async Task IdSet_FormerEmployeeId_IsSkippedForbidden_NeverResurrected()
    {
        // A Terminated (non-deleted) employee is outside the active People population — a crafted/stale id in
        // id-set mode must not let bulk deactivate rehire them into an active-list status.
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var former = Emp(db, o, complete: true, status: "Terminated");

        var result = await RunOk(Controller(db, o.TenantId),
            Ids("deactivate", new[] { former.Id }, reason: "cleanup", targetStatus: "Suspended"), db);

        result.Items.Single(i => i.EmployeeId == former.Id).Reason.Should().Be("forbidden");
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == former.Id)).Status
            .Should().Be("Terminated", "a former employee must not be resurrected by a bulk action");
    }

    [Fact]
    public async Task AllMatching_ResolvesWholeFilteredSet_AcrossPages_ServerSide()
    {
        // 30 activatable rows (> one FE page of 25): select-all-matching must resolve them ALL server-side.
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        for (var i = 0; i < 30; i++) Emp(db, o, complete: true);

        var req = new EmployeesController.BulkActionRequest("activate", "allMatching", null,
            new EmployeesController.BulkSelectAllFilter(null, null, null, null, null), null, null, null);
        var result = await RunOk(Controller(db, o.TenantId), req, db);

        result.Requested.Should().Be(30, "the filtered set spans all pages, not just the 25 visible rows");
        result.Succeeded.Should().Be(30);
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().CountAsync(e => e.TenantId == o.TenantId && e.Status == "Active")).Should().Be(30);
    }

    [Fact]
    public async Task AllMatching_Export_RespectsStatusFilter_NotUnfilteredDump()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var draft = Emp(db, o, complete: true, status: "Draft", code: "DRAFT-1");
        var active = Emp(db, o, complete: true, status: "Active", code: "ACTIVE-1");

        var req = new EmployeesController.BulkActionRequest("export", "allMatching", null,
            new EmployeesController.BulkSelectAllFilter(null, "Draft", null, null, null), null, null, null);
        var res = await Controller(db, o.TenantId).BulkAction(req, Service(db), CancellationToken.None);
        var csv = Encoding.UTF8.GetString(res.Should().BeOfType<FileContentResult>().Subject.FileContents);

        csv.Should().Contain("DRAFT-1");
        csv.Should().NotContain("ACTIVE-1", "export must respect the active filter, not dump the whole roster");
    }

    // ── BLOCKER 3: explicit selection + destructive reconciliation ────────────

    [Fact]
    public async Task SelectionMode_MustBeExplicit()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var e = Emp(db, o, complete: true);

        // Missing/invalid mode → 400.
        (await Controller(db, o.TenantId).BulkAction(
            new EmployeesController.BulkActionRequest("activate", null, new[] { e.Id }, null, null, null, null),
            Service(db), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();

        // Both id-set AND filter → 400 (never inferred).
        (await Controller(db, o.TenantId).BulkAction(
            new EmployeesController.BulkActionRequest("activate", "ids", new[] { e.Id },
                new EmployeesController.BulkSelectAllFilter(null, null, null, null, null), null, null, null),
            Service(db), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DestructiveAllMatching_RequiresExpectedCount_And_409sOnDrift()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        Emp(db, o, complete: true, status: "Active");
        Emp(db, o, complete: true, status: "Active");

        // No ExpectedCount on a destructive select-all → 400.
        var noCount = new EmployeesController.BulkActionRequest("delete", "allMatching", null,
            new EmployeesController.BulkSelectAllFilter(null, null, null, null, null), "purge", null, null);
        (await Controller(db, o.TenantId).BulkAction(noCount, Service(db), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();

        // Wrong ExpectedCount (user saw 1, server resolves 2) → 409, nothing deleted.
        var drift = new EmployeesController.BulkActionRequest("delete", "allMatching", null,
            new EmployeesController.BulkSelectAllFilter(null, null, null, null, null), "purge", null, 1);
        (await Controller(db, o.TenantId).BulkAction(drift, Service(db), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().CountAsync(e => e.TenantId == o.TenantId && !e.IsDeleted)).Should().Be(2);

        // Correct ExpectedCount → proceeds.
        var ok = new EmployeesController.BulkActionRequest("delete", "allMatching", null,
            new EmployeesController.BulkSelectAllFilter(null, null, null, null, null), "purge", null, 2);
        var result = (EmployeesController.BulkActionResult)((OkObjectResult)await Controller(db, o.TenantId)
            .BulkAction(ok, Service(db), CancellationToken.None)).Value!;
        result.Succeeded.Should().Be(2);
    }

    [Fact]
    public async Task IdSet_OverCap_Returns400()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var tooMany = Enumerable.Range(1, 5001).ToArray();
        (await Controller(db, o.TenantId).BulkAction(Ids("activate", tooMany), Service(db), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Per-action permission gating ──────────────────────────────────────────

    [Theory]
    [InlineData("activate", "employees.approve")]
    [InlineData("deactivate", "employees.write")]
    [InlineData("delete", "employees.delete")]
    public async Task Action_WithoutItsPermission_Is403(string action, string requiredPerm)
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var e = Emp(db, o, complete: true);
        // Grant every bulk permission EXCEPT the one this action needs.
        var perms = AllBulkPerms.Where(p => p != requiredPerm).ToArray();
        var controller = Controller(db, o.TenantId, permissions: perms);
        var req = Ids(action, new[] { e.Id }, reason: "r", targetStatus: action == "deactivate" ? "Suspended" : null);
        (await controller.BulkAction(req, Service(db), CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Export_WithoutAllowedRole_Is403()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var e = Emp(db, o, complete: true);
        var controller = Controller(db, o.TenantId, role: "Manager"); // Manager may Search but not Export
        (await controller.BulkAction(Ids("export", new[] { e.Id }), Service(db), CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task UnknownAction_Is400()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        (await Controller(db, o.TenantId).BulkAction(Ids("frobnicate", new[] { 1 }), Service(db), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    // ── deactivate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deactivate_RequiresReason_And_ValidTarget()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var e = Emp(db, o, complete: true, status: "Active");

        (await Controller(db, o.TenantId).BulkAction(
            Ids("deactivate", new[] { e.Id }, reason: null, targetStatus: "Suspended"), Service(db), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("a reason is required");

        (await Controller(db, o.TenantId).BulkAction(
            Ids("deactivate", new[] { e.Id }, reason: "r", targetStatus: "Terminated"), Service(db), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("bulk deactivate is never a terminate — target must be Suspended/Inactive");
    }

    [Fact]
    public async Task Deactivate_MovesActiveToSuspended()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var e = Emp(db, o, complete: true, status: "Active");

        var result = await RunOk(Controller(db, o.TenantId),
            Ids("deactivate", new[] { e.Id }, reason: "restructuring", targetStatus: "Suspended"), db);

        result.Succeeded.Should().Be(1);
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == e.Id)).Status.Should().Be("Suspended");
    }

    // ── delete (soft → Ex-Employees archive) ──────────────────────────────────

    [Fact]
    public async Task Delete_SoftDeletes_ToArchive_WithReasonAudited()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var e = Emp(db, o, complete: true, status: "Active");

        var result = await RunOk(Controller(db, o.TenantId),
            Ids("delete", new[] { e.Id }, reason: "GDPR erasure request"), db);

        result.Succeeded.Should().Be(1);
        db.ChangeTracker.Clear();
        var row = await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == e.Id);
        row.IsDeleted.Should().BeTrue();
        row.PrivacyStatus.Should().Be("RetainedForStatutoryAudit");
        var deletedAudit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId == o.TenantId && a.Action == "employees.deleted").ToListAsync();
        deletedAudit.Should().ContainSingle();
        deletedAudit[0].Metadata.Should().Contain("GDPR erasure request", "the delete reason is persisted in the per-row audit");
    }

    // ── idempotency + batch audit ─────────────────────────────────────────────

    [Fact]
    public async Task Activate_Rerun_IsNoOp_AlreadyActiveSkipped()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var e = Emp(db, o, complete: true);

        (await RunOk(Controller(db, o.TenantId), Ids("activate", new[] { e.Id }), db)).Succeeded.Should().Be(1);
        var second = await RunOk(Controller(db, o.TenantId), Ids("activate", new[] { e.Id }), db);

        second.Succeeded.Should().Be(0);
        second.Skipped.Should().Be(1);
        second.Items.Single().Reason.Should().Be("already_active");
    }

    [Fact]
    public async Task BatchAudit_IsWritten_SelfContained_WithResolvedIds()
    {
        await using var db = CreateDb();
        var o = await SeedOrg(db);
        var e = Emp(db, o, complete: true);

        await RunOk(Controller(db, o.TenantId), Ids("activate", new[] { e.Id }), db);

        var batch = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.TenantId == o.TenantId && a.Action == "employees.bulk_action");
        batch.Metadata.Should().Contain("\"action\":\"activate\"")
            .And.Contain("\"resolvedCount\":1")
            .And.Contain(e.Id.ToString(), "the resolved id set is persisted so one audit row is self-sufficient for forensics");
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class FakeDocs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
