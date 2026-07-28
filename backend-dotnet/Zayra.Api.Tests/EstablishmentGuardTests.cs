using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Establishment matrix guard unit tests (design test plan §8 — EstablishmentGuardTests):
/// fail-open semantics (absent row / unmapped / no dept), frozen-at-zero, block payload,
/// occupancy statuses, legacy string-department fallback, self-exclusion, grandfathering,
/// Advisory/Off modes, tenant isolation, and the audit-survives-block invariant.
/// </summary>
public class EstablishmentGuardTests
{
    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "xunit", Guid.NewGuid(), tenantId);

    private sealed record Org(Guid TenantId, Department Dept, StaffingLevel Level, Designation Designation);

    private static async Task<Org> SeedOrg(ZayraDbContext db, int? budget = null, string levelCode = "MGR",
        string levelNameEn = "Manager", string levelNameAr = "مدير")
    {
        var tenantId = Guid.NewGuid();
        var dept = new Department { TenantId = tenantId, Code = "OPS", NameEn = "Operations", IsActive = true };
        var level = new StaffingLevel { TenantId = tenantId, Code = levelCode, NameEn = levelNameEn, NameAr = levelNameAr, Rank = 2 };
        var desig = new Designation { TenantId = tenantId, Code = "OPS-MGR", TitleEn = "Operations Manager", IsActive = true, StaffingLevelId = level.Id };
        db.Departments.Add(dept);
        db.StaffingLevels.Add(level);
        db.Designations.Add(desig);
        if (budget is not null)
            db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget
            {
                TenantId = tenantId, DepartmentId = dept.Id, StaffingLevelId = level.Id, BudgetedHeadcount = budget.Value
            });
        await db.SaveChangesAsync();
        return new Org(tenantId, dept, level, desig);
    }

    private static Employee MakeEmployee(Org org, string status, bool legacyStringDept = false, Guid? designationId = null) => new()
    {
        TenantId = org.TenantId,
        EmployeeCode = $"E-{Guid.NewGuid():N}"[..12],
        FullName = "Test Employee",
        Status = status,
        JoiningDate = DateTime.UtcNow,
        Department = org.Dept.NameEn,
        DepartmentId = legacyStringDept ? null : org.Dept.Id,
        Designation = org.Designation.TitleEn,
        DesignationId = designationId ?? org.Designation.Id
    };

    // ── Fail-open semantics ──────────────────────────────────────────────────

    [Fact]
    public async Task AbsentBudgetRow_IsUncontrolled_AlwaysAllowed()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: null);
        db.Employees.AddRange(Enumerable.Range(0, 5).Select(_ => MakeEmployee(org, EmployeeStatuses.Active)));
        await db.SaveChangesAsync();

        var check = await new EstablishmentGuardService(db).CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null);
        check.Allowed.Should().BeTrue("no budget row = level uncontrolled, exactly pre-matrix behaviour");
        check.Block.Should().BeNull();
    }

    [Fact]
    public async Task ZeroBudget_FreezesTheLevel()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 0);

        var check = await new EstablishmentGuardService(db).CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null);
        check.Allowed.Should().BeFalse("explicit 0 = frozen: no additions sanctioned");
        check.Block!.Budgeted.Should().Be(0);
        check.Block.Current.Should().Be(0);
    }

    [Fact]
    public async Task NoDesignation_OrUnmappedDesignation_IsUnclassified_NeverBlocked()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 0); // frozen level would block anything countable
        var unmapped = new Designation { TenantId = org.TenantId, Code = "UNMAPPED", TitleEn = "Consultant", IsActive = true };
        db.Designations.Add(unmapped);
        await db.SaveChangesAsync();
        var guard = new EstablishmentGuardService(db);

        var noDesignation = await guard.CheckAsync(org.TenantId, org.Dept.Id, null, null);
        noDesignation.Allowed.Should().BeTrue();
        noDesignation.Unclassified.Should().BeTrue();

        var unmappedCheck = await guard.CheckAsync(org.TenantId, org.Dept.Id, unmapped.Id, null);
        unmappedCheck.Allowed.Should().BeTrue("fail-open, loudly — unclassified is surfaced, never blocked");
        unmappedCheck.Unclassified.Should().BeTrue();
    }

    [Fact]
    public async Task StringOnlyDepartment_NullDeptId_NeverBlocks()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 0);

        var check = await new EstablishmentGuardService(db).CheckAsync(org.TenantId, null, org.Designation.Id, null);
        check.Allowed.Should().BeTrue("a string-only department is surfaced via the reconciliation count, never blocked");
    }

    // ── Blocking + payload ───────────────────────────────────────────────────

    [Fact]
    public async Task FullCell_Blocks_WithCompletePayload_IncludingExitingIncumbents()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 2);
        db.Employees.Add(MakeEmployee(org, EmployeeStatuses.Active));
        db.Employees.Add(MakeEmployee(org, EmployeeStatuses.Offboarded)); // serving notice — occupies
        await db.SaveChangesAsync();

        var check = await new EstablishmentGuardService(db).CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null);
        check.Allowed.Should().BeFalse();
        var block = check.Block!;
        block.DepartmentId.Should().Be(org.Dept.Id);
        block.DepartmentName.Should().Be("Operations");
        block.StaffingLevelId.Should().Be(org.Level.Id);
        block.LevelCode.Should().Be("MGR");
        block.LevelNameEn.Should().Be("Manager");
        block.LevelNameAr.Should().Be("مدير", "Arabic name ships in the block payload from day one (AC11)");
        block.Budgeted.Should().Be(2);
        block.Current.Should().Be(2);
        block.Attempted.Should().Be(1);
        block.ExitingIncumbents.Should().Be(1, "the notice-period mitigation line needs the Offboarded subset");
    }

    [Fact]
    public async Task OccupancyStatuses_ActiveOffboardedSuspendedCount_TerminatedDraftDoNot()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 3);
        db.Employees.Add(MakeEmployee(org, EmployeeStatuses.Active));
        db.Employees.Add(MakeEmployee(org, EmployeeStatuses.Offboarded));
        db.Employees.Add(MakeEmployee(org, EmployeeStatuses.Suspended)); // KSA: suspension = continuing employment
        db.Employees.Add(MakeEmployee(org, EmployeeStatuses.Terminated));
        db.Employees.Add(MakeEmployee(org, EmployeeStatuses.Draft));
        var deleted = MakeEmployee(org, EmployeeStatuses.Active); deleted.IsDeleted = true;
        db.Employees.Add(deleted);
        await db.SaveChangesAsync();

        var check = await new EstablishmentGuardService(db).CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null);
        check.Allowed.Should().BeFalse("3 occupying (Active+Offboarded+Suspended) fill the 3 budgeted seats");
        check.Block!.Current.Should().Be(3, "Terminated/Draft/soft-deleted must not occupy");
    }

    [Fact]
    public async Task PredicateParity_OccupyingStatusHelper_MatchesQueryLiterals()
    {
        // Occupying() uses inline literals for provider translatability; IsOccupyingStatus and
        // OccupyingStatuses must stay in sync with them (predicate doc requires this test).
        await using var db = CreateDb();
        var org = await SeedOrg(db);
        foreach (var status in new[] { "Draft", "Invited", "Active", "Suspended", "Offboarded", "Terminated", "Exited", "Inactive" })
            db.Employees.Add(MakeEmployee(org, status));
        await db.SaveChangesAsync();

        var viaQuery = await EstablishmentOccupancy.Occupying(db.Employees.AsNoTracking(), org.TenantId)
            .Select(e => e.Status).ToListAsync();
        var viaHelper = (await db.Employees.AsNoTracking().Where(e => e.TenantId == org.TenantId).Select(e => e.Status).ToListAsync())
            .Where(EstablishmentOccupancy.IsOccupyingStatus).ToList();
        viaQuery.Should().BeEquivalentTo(viaHelper);
        viaQuery.Should().BeEquivalentTo(new[] { "Active", "Suspended", "Offboarded" });
    }

    [Fact]
    public async Task LegacyStringDepartment_CountsViaNameFallback()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 1);
        db.Employees.Add(MakeEmployee(org, EmployeeStatuses.Active, legacyStringDept: true));
        await db.SaveChangesAsync();

        var check = await new EstablishmentGuardService(db).CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null);
        check.Allowed.Should().BeFalse("an employee with DepartmentId=null but Department == NameEn still occupies the seat (panel parity)");
        check.Block!.Current.Should().Be(1);
    }

    [Fact]
    public async Task SelfExclusion_IdempotentReSaveOfSoleIncumbent_Passes()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 1);
        var incumbent = MakeEmployee(org, EmployeeStatuses.Active);
        db.Employees.Add(incumbent);
        await db.SaveChangesAsync();
        var guard = new EstablishmentGuardService(db);

        (await guard.CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, excludeEmployeeId: incumbent.Id))
            .Allowed.Should().BeTrue("the employee being mutated never counts against their own target cell");
        (await guard.CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, excludeEmployeeId: null))
            .Allowed.Should().BeFalse("a different (new) employee competing for the same seat is blocked");
    }

    [Fact]
    public async Task Grandfathering_OverBudgetCell_BlocksOnlyNetAdditions()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 2);
        var existing = Enumerable.Range(0, 3).Select(_ => MakeEmployee(org, EmployeeStatuses.Active)).ToList();
        db.Employees.AddRange(existing); // 3 of 2 — grandfathered over-establishment
        await db.SaveChangesAsync();
        var guard = new EstablishmentGuardService(db);

        (await guard.CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null))
            .Allowed.Should().BeFalse("net additions are blocked");
        (await guard.CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, excludeEmployeeId: existing[0].Id))
            .Allowed.Should().BeFalse("even an existing member re-assigned counts 2 others; 2+1 > 2");
        // An unrelated-field edit never invokes the guard at all (call sites fire only on
        // department/designation change) — enforced by call-site tests; here we assert the
        // over-budget state never throws by itself.
        (await guard.CheckAsync(org.TenantId, org.Dept.Id, null, null)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task BatchAttempted_CountsCumulatively()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 2);
        var guard = new EstablishmentGuardService(db);

        (await guard.CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null, attempted: 2)).Allowed.Should().BeTrue();
        var third = await guard.CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null, attempted: 3);
        third.Allowed.Should().BeFalse("CSV import asks 'do N more fit' — file order wins");
        third.Block!.Attempted.Should().Be(3);
    }

    // ── Modes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OffMode_ShortCircuits_EverythingAllowed()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 0);
        db.TenantHrConfigs.Add(new TenantHrConfig { TenantId = org.TenantId, EstablishmentEnforcementMode = "Off" });
        await db.SaveChangesAsync();

        var check = await new EstablishmentGuardService(db).CheckAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null);
        check.Allowed.Should().BeTrue("mode Off disables the guard entirely (audited, permission-gated setting)");
    }

    [Fact]
    public async Task AdvisoryMode_ReturnsBlockPayload_WithoutThrowing_AndAudits()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 0);
        db.TenantHrConfigs.Add(new TenantHrConfig { TenantId = org.TenantId, EstablishmentEnforcementMode = "Advisory" });
        await db.SaveChangesAsync();
        var guard = new EstablishmentGuardService(db);

        var check = await guard.EnforceAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null, "create", Ctx(org.TenantId));
        check.Allowed.Should().BeFalse();
        check.Advisory.Should().BeTrue("advisory over-budget returns the payload for a warning banner, never throws");
        check.Block.Should().NotBeNull();

        var audit = await db.AuditLogs.SingleAsync(a => a.TenantId == org.TenantId && a.Action == "establishment.assignment_blocked");
        audit.Metadata.Should().Contain("\"advisory\":true").And.Contain("Operations");
        using var doc = System.Text.Json.JsonDocument.Parse(audit.Metadata!);
        doc.RootElement.GetProperty("levelNameAr").GetString().Should().Be("مدير", "Arabic ships in every audit record");
    }

    [Fact]
    public async Task UnknownOrAbsentMode_DefaultsToEnforced()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 0);
        var guard = new EstablishmentGuardService(db);
        (await guard.GetEnforcementModeAsync(org.TenantId)).Should().Be("Enforced", "absent config row defaults to Enforced");

        db.TenantHrConfigs.Add(new TenantHrConfig { TenantId = org.TenantId, EstablishmentEnforcementMode = "garbage" });
        await db.SaveChangesAsync();
        (await guard.GetEnforcementModeAsync(org.TenantId)).Should().Be("Enforced", "unknown values fall back defensively to Enforced");
    }

    // ── EnforceAsync: throw + audit survives ─────────────────────────────────

    [Fact]
    public async Task EnforcedBlock_Throws_AndWritesBlockAudit_WithDenormalizedNames()
    {
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 1);
        var incumbent = MakeEmployee(org, EmployeeStatuses.Active);
        db.Employees.Add(incumbent);
        await db.SaveChangesAsync();
        var guard = new EstablishmentGuardService(db);

        var ex = await Assert.ThrowsAsync<EstablishmentBudgetExceededException>(() =>
            guard.EnforceAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null, "create", Ctx(org.TenantId)));
        ex.Block.Budgeted.Should().Be(1);
        ex.Block.Current.Should().Be(1);

        // The block audit is written through a FRESH context so it survives the caller's rollback.
        // It must be denormalized (readable in 2028 after renames) and carry the path.
        var audit = await db.AuditLogs.SingleAsync(a => a.TenantId == org.TenantId && a.Action == "establishment.assignment_blocked");
        audit.Metadata.Should().Contain("\"path\":\"create\"")
            .And.Contain("\"advisory\":false")
            .And.Contain("\"departmentName\":\"Operations\"")
            .And.Contain("\"levelNameEn\":\"Manager\"")
            .And.Contain("\"levelCode\":\"MGR\"");
    }

    [Fact]
    public async Task BlockAudit_DoesNotFlushCallersPendingChanges()
    {
        // The guard writes its block audit on a FRESH context. If it wrote on the ambient one,
        // AuditService's SaveChangesAsync would flush the caller's half-applied employee — the
        // exact state the block is preventing.
        await using var db = CreateDb();
        var org = await SeedOrg(db, budget: 0);
        var pendingEmployee = MakeEmployee(org, EmployeeStatuses.Active);
        db.Employees.Add(pendingEmployee); // caller's uncommitted intent

        var guard = new EstablishmentGuardService(db);
        await Assert.ThrowsAsync<EstablishmentBudgetExceededException>(() =>
            guard.EnforceAsync(org.TenantId, org.Dept.Id, org.Designation.Id, null, "create", Ctx(org.TenantId)));

        (await db.Employees.AsNoTracking().CountAsync(e => e.TenantId == org.TenantId))
            .Should().Be(0, "the blocked employee must not be persisted by the audit write");
        (await db.AuditLogs.CountAsync(a => a.TenantId == org.TenantId && a.Action == "establishment.assignment_blocked"))
            .Should().Be(1);
    }

    // ── Tenant isolation (360°-audit house rule) ─────────────────────────────

    [Fact]
    public async Task TenantIsolation_SameDepartmentNameInOtherTenant_NeverCrossCounts()
    {
        await using var db = CreateDb();
        var orgA = await SeedOrg(db, budget: 1);
        var orgB = await SeedOrg(db, budget: 1); // different tenant, same "Operations"/"Manager" naming
        db.Employees.Add(MakeEmployee(orgB, EmployeeStatuses.Active)); // occupies tenant B's seat only
        await db.SaveChangesAsync();
        var guard = new EstablishmentGuardService(db);

        (await guard.CheckAsync(orgA.TenantId, orgA.Dept.Id, orgA.Designation.Id, null))
            .Allowed.Should().BeTrue("tenant A's cell is empty — tenant B's occupant must never count");
        (await guard.CheckAsync(orgB.TenantId, orgB.Dept.Id, orgB.Designation.Id, null))
            .Allowed.Should().BeFalse();
    }
}

/// <summary>
/// Staffing-level seeding + designation→level mapping workflow tests (design plan
/// EstablishmentSeederAndMappingTests): seeded once as editable data, idempotent, deliberate
/// deletion respected; suggestions are read-only and heuristic-only (no band-name literals);
/// apply writes only approved pairs, requires reason, audits with impact.
/// </summary>
public class EstablishmentSeederAndMappingTests
{
    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Seeder_SeedsFiveRankedBilingualDefaults_Idempotently()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var seeder = new EstablishmentSeeder(db);
        await seeder.EnsureStaffingLevelsAsync(tenantId);
        await seeder.EnsureStaffingLevelsAsync(tenantId); // idempotent

        var levels = await db.StaffingLevels.Where(l => l.TenantId == tenantId).OrderBy(l => l.Rank).ToListAsync();
        levels.Should().HaveCount(5);
        levels.Select(l => l.Code).Should().ContainInOrder("DEPT_HEAD", "MANAGER", "ASST_MANAGER", "SUPERVISOR", "STAFF");
        levels.Should().OnlyContain(l => l.NameAr.Length > 0, "Arabic names ship from day one (AC11)");
        levels.Select(l => l.Rank).Should().ContainInOrder(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task Seeder_RespectsDeliberateDeletion_NoReseed()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var seeder = new EstablishmentSeeder(db);
        await seeder.EnsureStaffingLevelsAsync(tenantId);
        foreach (var level in await db.StaffingLevels.Where(l => l.TenantId == tenantId).ToListAsync())
            level.IsDeleted = true;
        await db.SaveChangesAsync();

        await seeder.EnsureStaffingLevelsAsync(tenantId);
        (await db.StaffingLevels.CountAsync(l => l.TenantId == tenantId && !l.IsDeleted))
            .Should().Be(0, "a tenant that deliberately deleted its levels is NOT re-seeded — their data, their choice");
    }

    [Fact]
    public async Task Suggestions_UseOnlyStructuralHeuristics_AgainstRenamedLevels_AndNeverWrite()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        // Fully renamed catalog: nothing matches the seeded default names, so any band-name
        // literal hiding in code would surface as a bogus suggestion here.
        var custom = new StaffingLevel { TenantId = tenantId, Code = "BAND_X", NameEn = "Squad Lead", NameAr = "قائد", Rank = 4 };
        db.StaffingLevels.Add(custom);
        db.Designations.AddRange(
            new Designation { TenantId = tenantId, Code = "D1", TitleEn = "Alpha", JobLevel = "Squad Lead", IsActive = true },   // jobLevel name match
            new Designation { TenantId = tenantId, Code = "D2", TitleEn = "Beta", JobLevel = "", LevelRank = 4, IsActive = true }, // rank match
            new Designation { TenantId = tenantId, Code = "D3", TitleEn = "Gamma", JobLevel = "Manager", LevelRank = 99, IsActive = true }); // seeded-default name — must NOT match
        await db.SaveChangesAsync();

        var controller = EstablishmentApiTests.CreateController(db, tenantId);
        var result = await controller.MappingSuggestions(CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(((Microsoft.AspNetCore.Mvc.OkObjectResult)result).Value);

        json.Should().Contain("\"titleEn\":\"Alpha\"").And.Contain("\"basis\":\"jobLevel\"");
        json.Should().Contain("\"titleEn\":\"Beta\"").And.Contain("\"basis\":\"levelRank\"");
        // Gamma's JobLevel "Manager" matches a SEEDED-DEFAULT band name that this tenant renamed
        // away — it must get NO suggestion, proving the heuristics compare only against the
        // tenant's own (editable) rows and no band-name literal exists in code.
        json.Should().Contain("\"jobLevel\":\"Manager\",\"levelRank\":99,\"isManagerRole\":false,\"currentLevelId\":null,\"suggestedLevelId\":null");
        json.Should().NotContain("\"suggestedLevelName\":\"Manager\"", "no band-name literal may exist outside tenant data");
        (await db.Designations.CountAsync(d => d.TenantId == tenantId && d.StaffingLevelId != null))
            .Should().Be(0, "suggestions never write — apply is a separate, approved, audited step");
    }

    [Fact]
    public async Task Apply_WritesOnlyApprovedPairs_RequiresReason_AuditsWithImpact()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var level = new StaffingLevel { TenantId = tenantId, Code = "MGR", NameEn = "Manager", NameAr = "مدير", Rank = 2 };
        var dept = new Department { TenantId = tenantId, Code = "SLS", NameEn = "Sales", IsActive = true };
        var mapMe = new Designation { TenantId = tenantId, Code = "SM", TitleEn = "Sales Manager", IsActive = true };
        var leaveMe = new Designation { TenantId = tenantId, Code = "SR", TitleEn = "Sales Rep", IsActive = true };
        db.AddRange(level, dept, mapMe, leaveMe);
        // Budget 1 with 2 future-counting occupants → apply must report the over-budget impact.
        db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = tenantId, DepartmentId = dept.Id, StaffingLevelId = level.Id, BudgetedHeadcount = 1 });
        db.Employees.AddRange(
            new Employee { TenantId = tenantId, EmployeeCode = "S1", FullName = "S One", Status = "Active", JoiningDate = DateTime.UtcNow, DepartmentId = dept.Id, Department = "Sales", DesignationId = mapMe.Id },
            new Employee { TenantId = tenantId, EmployeeCode = "S2", FullName = "S Two", Status = "Active", JoiningDate = DateTime.UtcNow, DepartmentId = dept.Id, Department = "Sales", DesignationId = mapMe.Id });
        await db.SaveChangesAsync();
        var controller = EstablishmentApiTests.CreateController(db, tenantId);

        var missingReason = await controller.ApplyMapping(new LevelMappingApplyRequest(
            new List<LevelMappingPair> { new(mapMe.Id, level.Id) }, Reason: null), CancellationToken.None);
        missingReason.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>("mapping is the second override lever — reason is mandatory");

        var result = await controller.ApplyMapping(new LevelMappingApplyRequest(
            new List<LevelMappingPair> { new(mapMe.Id, level.Id) }, "Annual banding review"), CancellationToken.None);
        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();

        (await db.Designations.SingleAsync(d => d.Id == mapMe.Id)).StaffingLevelId.Should().Be(level.Id);
        (await db.Designations.SingleAsync(d => d.Id == leaveMe.Id)).StaffingLevelId.Should().BeNull("only admin-approved pairs are applied");
        var audit = await db.AuditLogs.SingleAsync(a => a.TenantId == tenantId && a.Action == "establishment.level_mapping_changed");
        audit.Metadata.Should().Contain("Annual banding review").And.Contain("Sales").And.Contain("\"projectedCurrent\":2");
    }
}
