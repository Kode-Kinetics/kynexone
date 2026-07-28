using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Establishment matrix API tests (design plan EstablishmentBudgetApiTests): mandatory reason,
/// null-clears-row, below-occupancy + envelope warnings (never blocks), the permission gate on
/// both the matrix PUT and the re-gated envelope PATCH, referenced-level delete protection,
/// matrix↔guard parity (AC4), lazy level seeding, and the enforcement-mode field-level control.
/// </summary>
public class EstablishmentApiTests
{
    internal static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    internal static ClaimsPrincipal User(Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"),
            new("is_group_scope", "true")
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public static EstablishmentController CreateController(ZayraDbContext db, Guid tenantId, params string[] permissions)
    {
        if (permissions.Length == 0)
            permissions = new[] { "organization.read", "organization.write", "organization.establishment.write" };
        return new EstablishmentController(db, new AuditService(db), new EstablishmentGuardService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(tenantId, permissions) } }
        };
    }

    private sealed record Fixture(Guid TenantId, Department Dept, StaffingLevel Level, Designation Designation);

    private static async Task<Fixture> Seed(ZayraDbContext db, int approvedHeadcount = 0)
    {
        var tenantId = Guid.NewGuid();
        var dept = new Department { TenantId = tenantId, Code = "OPS", NameEn = "Operations", IsActive = true, ApprovedHeadcount = approvedHeadcount };
        var level = new StaffingLevel { TenantId = tenantId, Code = "MGR", NameEn = "Manager", NameAr = "مدير", Rank = 2 };
        var desig = new Designation { TenantId = tenantId, Code = "OPS-MGR", TitleEn = "Operations Manager", IsActive = true, StaffingLevelId = level.Id };
        db.AddRange(dept, level, desig);
        await db.SaveChangesAsync();
        return new Fixture(tenantId, dept, level, desig);
    }

    private static Employee Occupant(Fixture fx, string status = "Active") => new()
    {
        TenantId = fx.TenantId,
        EmployeeCode = $"E-{Guid.NewGuid():N}"[..12],
        FullName = "Occupant",
        Status = status,
        JoiningDate = DateTime.UtcNow,
        DepartmentId = fx.Dept.Id,
        Department = fx.Dept.NameEn,
        DesignationId = fx.Designation.Id
    };

    // ── Budget PUT ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PutBudgets_RequiresMandatoryReason()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        var result = await CreateController(db, fx.TenantId).PutBudgets(fx.Dept.Id,
            new DepartmentBudgetsRequest(new List<DepartmentBudgetRow> { new(fx.Level.Id, 2) }, Reason: " "), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>("every budget mutation carries a mandatory reason (spec §7)");
    }

    [Fact]
    public async Task PutBudgets_Upserts_Audits_WithDenormalizedBeforeAfter()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        var controller = CreateController(db, fx.TenantId);

        var result = await controller.PutBudgets(fx.Dept.Id,
            new DepartmentBudgetsRequest(new List<DepartmentBudgetRow> { new(fx.Level.Id, 2) }, "Q3 org plan"), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var row = await db.DepartmentStaffingBudgets.SingleAsync(b => b.TenantId == fx.TenantId && b.DepartmentId == fx.Dept.Id);
        row.BudgetedHeadcount.Should().Be(2);
        var audit = await db.AuditLogs.SingleAsync(a => a.TenantId == fx.TenantId && a.Action == "establishment.budget_updated");
        audit.Metadata.Should().Contain("Q3 org plan")
            .And.Contain("\"departmentName\":\"Operations\"")
            .And.Contain("\"levelNameEn\":\"Manager\"")
            .And.Contain("\"before\":null").And.Contain("\"after\":2");
    }

    [Fact]
    public async Task PutBudgets_NullClearsRow_LevelReturnsToUncontrolled_Audited()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = fx.TenantId, DepartmentId = fx.Dept.Id, StaffingLevelId = fx.Level.Id, BudgetedHeadcount = 1 });
        await db.SaveChangesAsync();
        var controller = CreateController(db, fx.TenantId);

        var result = await controller.PutBudgets(fx.Dept.Id,
            new DepartmentBudgetsRequest(new List<DepartmentBudgetRow> { new(fx.Level.Id, null) }, "Deregulate level"), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        (await db.DepartmentStaffingBudgets.CountAsync(b => b.TenantId == fx.TenantId && !b.IsDeleted)).Should().Be(0);
        (await new EstablishmentGuardService(db).CheckAsync(fx.TenantId, fx.Dept.Id, fx.Designation.Id, null))
            .Allowed.Should().BeTrue("cleared row = uncontrolled again");
        var audit = await db.AuditLogs.SingleAsync(a => a.TenantId == fx.TenantId && a.Action == "establishment.budget_updated");
        audit.Metadata.Should().Contain("\"before\":1").And.Contain("\"after\":null");
    }

    [Fact]
    public async Task PutBudgets_BelowOccupancyAndOverEnvelope_WarnButNeverBlock()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, approvedHeadcount: 1);
        db.Employees.AddRange(Occupant(fx), Occupant(fx), Occupant(fx)); // 3 occupying Managers
        await db.SaveChangesAsync();
        var controller = CreateController(db, fx.TenantId);

        // Budget 2 < occupancy 3 (grandfathering warning) AND allocated 2 > envelope 1.
        var result = await controller.PutBudgets(fx.Dept.Id,
            new DepartmentBudgetsRequest(new List<DepartmentBudgetRow> { new(fx.Level.Id, 2) }, "Downsize plan"), CancellationToken.None);
        var payload = JsonSerializer.Serialize(((OkObjectResult)result).Value);
        payload.Should().Contain("below current occupancy 3", "reducing under occupancy warns, never blocks, never invalidates (AC5)");
        payload.Should().Contain("exceeds the department", "allocation > envelope is a warning, not a block (Conflict R2)");
        (await db.DepartmentStaffingBudgets.SingleAsync(b => b.TenantId == fx.TenantId)).BudgetedHeadcount.Should().Be(2);
    }

    [Fact]
    public async Task PermissionGate_OrganizationWriteAlone_CannotMoveTheWall()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        // organization.write (can hit the wall) but NOT organization.establishment.write (move it).
        var establishment = CreateController(db, fx.TenantId, "organization.read", "organization.write");
        (await establishment.PutBudgets(fx.Dept.Id,
                new DepartmentBudgetsRequest(new List<DepartmentBudgetRow> { new(fx.Level.Id, 5) }, "nope"), CancellationToken.None))
            .Should().BeOfType<ForbidResult>();

        var planning = new PlanningController(db, new AuditService(db), new EstablishmentGuardService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(fx.TenantId, "organization.read", "organization.write") } }
        };
        (await planning.SetEstablishment(fx.Dept.Id, new EstablishmentUpdate(10, 0, null, "grow"), CancellationToken.None))
            .Should().BeOfType<ForbidResult>("the envelope PATCH is re-gated onto organization.establishment.write (AC6)");
    }

    [Fact]
    public async Task SetEstablishment_NowRequiresReason_AndWritesAudit()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        var planning = new PlanningController(db, new AuditService(db), new EstablishmentGuardService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = User(fx.TenantId, "organization.establishment.write") }
            }
        };

        (await planning.SetEstablishment(fx.Dept.Id, new EstablishmentUpdate(10, 50_000m, null, null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("envelope changes now require a reason");

        var ok = await planning.SetEstablishment(fx.Dept.Id, new EstablishmentUpdate(10, 50_000m, null, "Approved FY27 plan"), CancellationToken.None);
        ok.Should().BeOfType<OkObjectResult>();
        var audit = await db.AuditLogs.SingleAsync(a => a.TenantId == fx.TenantId && a.Action == "establishment.budget_updated");
        audit.Metadata.Should().Contain("\"scope\":\"envelope\"").And.Contain("Approved FY27 plan")
            .And.Contain("Operations", "the previously un-audited establishment PATCH is now audited (AC6)");
    }

    // ── Level lifecycle ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteLevel_BlockedWhileReferenced_WithCounts_ThenSucceedsWhenClear()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = fx.TenantId, DepartmentId = fx.Dept.Id, StaffingLevelId = fx.Level.Id, BudgetedHeadcount = 1 });
        await db.SaveChangesAsync();
        var controller = CreateController(db, fx.TenantId);

        var blocked = await controller.DeleteLevel(fx.Level.Id, CancellationToken.None);
        var conflict = blocked.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(conflict.Value).Should().Contain("\"designationRefs\":1").And.Contain("\"budgetRefs\":1");

        var desig = await db.Designations.SingleAsync(d => d.Id == fx.Designation.Id);
        desig.StaffingLevelId = null;
        var budget = await db.DepartmentStaffingBudgets.SingleAsync();
        budget.IsDeleted = true;
        await db.SaveChangesAsync();

        (await controller.DeleteLevel(fx.Level.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await db.StaffingLevels.IgnoreQueryFilters().SingleAsync(l => l.Id == fx.Level.Id)).IsDeleted
            .Should().BeTrue("soft delete, audit history retained");
    }

    [Fact]
    public async Task CreateLevel_DuplicateCode_IncludingSoftDeleted_Returns409Legibly()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        db.StaffingLevels.Add(new StaffingLevel { TenantId = fx.TenantId, Code = "OLD", NameEn = "Old Band", IsDeleted = true });
        await db.SaveChangesAsync();
        var controller = CreateController(db, fx.TenantId);

        var live = await controller.CreateLevel(new StaffingLevelRequest("MGR", "Duplicate", null, 9, null), CancellationToken.None);
        live.Should().BeOfType<ConflictObjectResult>();

        var deleted = await controller.CreateLevel(new StaffingLevelRequest("OLD", "Revived", null, 9, null), CancellationToken.None);
        var msg = JsonSerializer.Serialize(((ConflictObjectResult)deleted).Value);
        msg.Should().Contain("deleted staffing level", "the unique index includes soft-deleted rows — answer legibly, not with a raw constraint violation");
    }

    [Fact]
    public async Task Levels_LazySeedsDefaults_ForPreMatrixTenants()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var controller = CreateController(db, tenantId, "organization.read");

        var result = await controller.Levels(new EstablishmentSeeder(db), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        (await db.StaffingLevels.CountAsync(l => l.TenantId == tenantId)).Should().Be(5, "existing tenants get defaults on first visit, no backfill job");
    }

    // ── Matrix parity (AC4) ──────────────────────────────────────────────────

    [Fact]
    public async Task Matrix_NumbersEqualGuardNumbers_OnMixedFixture()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, approvedHeadcount: 5);
        db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = fx.TenantId, DepartmentId = fx.Dept.Id, StaffingLevelId = fx.Level.Id, BudgetedHeadcount = 2 });
        db.Employees.Add(Occupant(fx));                                  // Active, ID-linked
        db.Employees.Add(Occupant(fx, "Offboarded"));                    // serving notice
        var legacy = Occupant(fx); legacy.DepartmentId = null;           // legacy string-matched
        db.Employees.Add(legacy);
        var unclassified = Occupant(fx); unclassified.DesignationId = null; // surfaced, not counted
        db.Employees.Add(unclassified);
        var terminated = Occupant(fx, "Terminated");                     // does not occupy
        db.Employees.Add(terminated);
        await db.SaveChangesAsync();

        var matrixJson = JsonSerializer.Serialize(
            ((OkObjectResult)await CreateController(db, fx.TenantId).Matrix(CancellationToken.None)).Value);
        var guardBlock = (await new EstablishmentGuardService(db)
            .CheckAsync(fx.TenantId, fx.Dept.Id, fx.Designation.Id, null)).Block!;

        guardBlock.Current.Should().Be(3, "Active + Offboarded + legacy string-matched occupy; unclassified and Terminated do not");
        matrixJson.Should().Contain($"\"current\":{guardBlock.Current}", "popup numbers must equal panel numbers (AC4)");
        matrixJson.Should().Contain($"\"exitingIncumbents\":{guardBlock.ExitingIncumbents}");
        matrixJson.Should().Contain("\"budgeted\":2").And.Contain("\"gap\":-1", "over-establishment renders as a red negative gap");
        matrixJson.Should().Contain("\"unclassifiedCount\":1", "unclassified employees are surfaced loudly");
        matrixJson.Should().Contain("\"enforcementMode\":\"Enforced\"");
    }

    [Fact]
    public async Task Matrix_DistinguishesUncontrolledNull_FromFrozenZero()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        var frozenLevel = new StaffingLevel { TenantId = fx.TenantId, Code = "SUP", NameEn = "Supervisor", NameAr = "مشرف", Rank = 4 };
        db.StaffingLevels.Add(frozenLevel);
        db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = fx.TenantId, DepartmentId = fx.Dept.Id, StaffingLevelId = frozenLevel.Id, BudgetedHeadcount = 0 });
        await db.SaveChangesAsync();

        var json = JsonSerializer.Serialize(((OkObjectResult)await CreateController(db, fx.TenantId).Matrix(CancellationToken.None)).Value);
        json.Should().Contain("\"budgeted\":null", "MGR has no row — uncontrolled renders as null (\"—\")");
        json.Should().Contain("\"budgeted\":0", "SUP is explicitly frozen at 0 — visually distinct from unset");
    }

    // ── Legacy string-department reconciliation (security R1) ────────────────

    [Fact]
    public async Task ReconcileDepartments_StampsIdsForExactMatches_LeavesAmbiguousAndUnmatchedAlone()
    {
        await using var db = CreateDb();
        var fx = await Seed(db);
        db.Departments.Add(new Department { TenantId = fx.TenantId, Code = "OPS2", NameEn = "Duplicated", IsActive = true });
        db.Departments.Add(new Department { TenantId = fx.TenantId, Code = "OPS3", NameEn = "Duplicated", IsActive = true });
        Employee Legacy(string dept) => new()
        {
            TenantId = fx.TenantId, EmployeeCode = $"L-{Guid.NewGuid():N}"[..12], FullName = "Legacy",
            Status = "Active", JoiningDate = DateTime.UtcNow, Department = dept, DepartmentId = null
        };
        var match = Legacy("Operations");
        var ambiguous = Legacy("Duplicated");
        var unmatched = Legacy("Ghost Dept");
        db.Employees.AddRange(match, ambiguous, unmatched);
        await db.SaveChangesAsync();

        var payload = JsonSerializer.Serialize(((OkObjectResult)await CreateController(db, fx.TenantId)
            .ReconcileDepartments(CancellationToken.None)).Value);
        payload.Should().Contain("\"resolvedCount\":1").And.Contain("\"ambiguous\":1").And.Contain("\"unmatched\":1");

        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == match.Id)).DepartmentId
            .Should().Be(fx.Dept.Id, "exact single matches get the ID stamped — shrinking the rename-bypass surface");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == ambiguous.Id)).DepartmentId
            .Should().BeNull("ambiguous names are never guessed");
        (await db.AuditLogs.CountAsync(a => a.TenantId == fx.TenantId && a.Action == "establishment.departments_reconciled"))
            .Should().Be(1);
    }

    // ── Enforcement-mode field-level control (TenantHrConfig PUT) ────────────

    [Fact]
    public async Task EnforcementModeChange_RequiresPermission_Reason_AndIsAudited()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        TenantHrConfigController Controller(params string[] perms) => new(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(tenantId, perms) } }
        };
        static TenantHrConfigRequest Request(string? mode, string? reason) =>
            new(EstablishmentEnforcementMode: mode, EstablishmentModeChangeReason: reason);

        // Without the establishment permission the kill switch is a 403 — never silently ignored.
        (await Controller("organization.write").Upsert(Request("Off", "because"), CancellationToken.None))
            .Should().BeOfType<ForbidResult>("disabling every budget is strictly more powerful than editing one");
        db.ChangeTracker.Clear(); // rejected request: discard the tracked (never-saved) config row

        (await Controller("organization.establishment.write").Upsert(Request("Sideways", "x"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("only Off | Advisory | Enforced are valid");
        db.ChangeTracker.Clear();

        (await Controller("organization.establishment.write").Upsert(Request("Off", null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("a mode change requires a reason");
        db.ChangeTracker.Clear();

        (await Controller("organization.establishment.write").Upsert(Request("Advisory", "Trial period Q3"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await db.TenantHrConfigs.SingleAsync(c => c.TenantId == tenantId)).EstablishmentEnforcementMode.Should().Be("Advisory");
        var audit = await db.AuditLogs.SingleAsync(a => a.TenantId == tenantId && a.Action == "establishment.enforcement_mode_changed");
        audit.Metadata.Should().Contain("\"before\":\"Enforced\"").And.Contain("\"after\":\"Advisory\"").And.Contain("Trial period Q3");

        // Omitting the field (legacy client round-trip) keeps the stored value.
        (await Controller("organization.write").Upsert(Request(null, null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await db.TenantHrConfigs.SingleAsync(c => c.TenantId == tenantId)).EstablishmentEnforcementMode
            .Should().Be("Advisory", "a PUT without the field must not reset the mode");
    }
}
