using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A: the cost-centre master-data rules in <see cref="OrganizationSetupService"/> (contract 1.1, 1.11 b, 2.9).
/// T-010 parent tree, T-011 code format, T-047 in-use guard (M-8), T-048 code/company lock once posted (M-9), AM-10 functional class reason (M-10).
/// </summary>
public class CostCentreMasterServiceTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static OrganizationSetupService Svc(ZayraDbContext db) => new(db, new AuditService(db));
    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    private sealed record World(ZayraDbContext Db, OrganizationSetupService Svc, Guid Tenant, Company A, Company B);

    private static async Task<World> Seed()
    {
        var db = Db();
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = "B-1", IsActive = true, DefaultCurrency = "SAR" };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        return new World(db, Svc(db), tenant, a, b);
    }

    private static CostCenterRequest Req(Company c, string code, string name = "Name", bool active = true, Guid? parent = null, string? cls = null, string? seg = null, string? reason = null) =>
        new(c.Id, code, name, active, null, parent, cls, seg, reason);

    private static async Task<CostCentreRuleException> Refused(Func<Task> act) => (await act.Should().ThrowAsync<CostCentreRuleException>()).Which;

    // ── T-011 code format ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("=1+1")]
    [InlineData("-LEAD")]
    [InlineData("A B")]
    [InlineData("@SUM(A1)")]
    public async Task T011_Create_RefusesAnUnsafeCode(string code)
    {
        var w = await Seed();
        var ex = await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, code), Ctx(w.Tenant), default));
        (ex.Code, ex.Status).Should().Be(("cost_centre_code_invalid", 422));
        (await w.Db.CostCenters.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task T011_Create_RefusesThirtyOneCharacters_AcceptsThirty_AndNormalisesCase()
    {
        var w = await Seed();
        (await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, new string('X', 31)), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_code_invalid");
        var ok = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, new string('x', 30)), Ctx(w.Tenant), default);
        ok.Code.Should().Be(new string('X', 30));
    }

    [Fact]
    public async Task T011_AGlSegmentIsHeldToTheSameFormat_AndIsUniquePerCompany()
    {
        var w = await Seed();
        (await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "S1", seg: "=SEG"), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_gl_segment_invalid");
        await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "S1", seg: "SEG.1"), Ctx(w.Tenant), default);
        var dup = await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "S2", seg: "seg.1"), Ctx(w.Tenant), default));
        (dup.Code, dup.Status).Should().Be(("cost_centre_gl_segment_taken", 409));
        await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.B, "S2", seg: "SEG.1"), Ctx(w.Tenant), default); // another company may reuse it
    }

    // ── T-010 parent tree ──────────────────────────────────────────────────────

    [Fact]
    public async Task T010_ASelfParent_ACycle_AndTooDeepATree_AreRefused()
    {
        var w = await Seed();
        var a = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "A"), Ctx(w.Tenant), default);
        var b = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "B", parent: a.Id), Ctx(w.Tenant), default);

        (await Refused(() => w.Svc.UpdateCostCenterAsync(w.Tenant, a.Id, Req(w.A, "A", parent: a.Id), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_parent_self");
        (await Refused(() => w.Svc.UpdateCostCenterAsync(w.Tenant, a.Id, Req(w.A, "A", parent: b.Id), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_parent_cycle");

        var tip = b;
        for (var i = 0; i < 8; i++) tip = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, $"L{i}", parent: tip.Id), Ctx(w.Tenant), default); // levels 3..10
        (await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "TOO-DEEP", parent: tip.Id), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_parent_too_deep");
    }

    [Fact]
    public async Task T010_AParentMustBeAnActiveCostCentreOfTheSameCompany()
    {
        var w = await Seed();
        var other = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.B, "OTHER"), Ctx(w.Tenant), default);
        var off = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OFF"), Ctx(w.Tenant), default);
        await w.Svc.UpdateCostCenterAsync(w.Tenant, off.Id, Req(w.A, "OFF", active: false), Ctx(w.Tenant), default);

        (await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "C1", parent: other.Id), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_other_company");
        (await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "C2", parent: off.Id), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_inactive");
        (await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "C3", parent: Guid.NewGuid()), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_not_found");
    }

    [Fact]
    public async Task T010_ParentGuidEmptyClearsTheParent_AndNullLeavesItAlone()
    {
        var w = await Seed();
        var p = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "P"), Ctx(w.Tenant), default);
        var c = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "C", parent: p.Id), Ctx(w.Tenant), default);
        (await w.Svc.UpdateCostCenterAsync(w.Tenant, c.Id, Req(w.A, "C", name: "Renamed"), Ctx(w.Tenant), default))!.ParentId.Should().Be(p.Id);
        (await w.Svc.UpdateCostCenterAsync(w.Tenant, c.Id, Req(w.A, "C", parent: Guid.Empty), Ctx(w.Tenant), default))!.ParentId.Should().BeNull();
    }

    // ── AM-10 functional class ─────────────────────────────────────────────────

    [Fact]
    public async Task Am10_AnUnknownClassIsRefused_TheReservedClassIsStored_AndAChangeNeedsAReason()
    {
        var w = await Seed();
        (await Refused(() => w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "X", cls: "BANANA"), Ctx(w.Tenant), default))).Code.Should().Be("cost_centre_functional_class_invalid");

        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "CAP", cls: "CAPITALISABLE"), Ctx(w.Tenant), default);
        cc.FunctionalClass.Should().Be("CAPITALISABLE", "reserved for posting, but storable");

        var noReason = await Refused(() => w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "CAP", cls: "COGS"), Ctx(w.Tenant), default));
        (noReason.Code, noReason.Status, noReason.Message).Should().Be(("cost_centre_reason_required", 422, "Enter a reason for this change."));
        noReason.MessageAr.Should().Be("أدخل سببًا لهذا التغيير.");

        var changed = await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "CAP", cls: "COGS", reason: "Reclassified with finance"), Ctx(w.Tenant), default);
        changed!.FunctionalClass.Should().Be("COGS");

        // Resubmitting the SAME class (a form that sends every field back) needs no reason.
        await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "CAP", name: "Renamed", cls: "COGS"), Ctx(w.Tenant), default);
    }

    // ── T-047 in-use guard (M-8) ───────────────────────────────────────────────

    public static IEnumerable<object[]> InUseCases()
    {
        foreach (var kind in new[] { "department", "employee", "position", "mapping", "companyDefault", "activeChild" })
            foreach (var op in new[] { "delete", "deactivate", "changeCompany" })
                yield return [kind, op];
    }

    private static async Task<Guid> Reference(World w, CostCenterDto cc, string kind)
    {
        switch (kind)
        {
            case "department": w.Db.Departments.Add(new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D", NameEn = "D", CostCenterId = cc.Id }); break;
            case "employee": w.Db.Employees.Add(new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E1", FullName = "E", Status = "Active", JoiningDate = DateTime.UtcNow, CostCenterId = cc.Id }); break;
            case "position": w.Db.Positions.Add(new Position { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "P", Title = "P", CostCenterId = cc.Id }); break;
            case "mapping": w.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = w.Tenant, CompanyId = w.A.Id, DriverKey = "EARN:BASIC", AccountId = Guid.NewGuid(), SegmentCostCenterId = cc.Id }); break;
            case "companyDefault": (await w.Db.Companies.SingleAsync(x => x.Id == w.A.Id)).DefaultCostCenterId = cc.Id; break;
            case "activeChild": w.Db.CostCenters.Add(new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "KID", Name = "Kid", ParentId = cc.Id }); break;
        }
        await w.Db.SaveChangesAsync();
        return cc.Id;
    }

    [Theory]
    [MemberData(nameof(InUseCases))]
    public async Task T047_ACostCentreInLiveUse_CannotBeDeletedDeactivatedOrMovedToAnotherCompany(string kind, string op)
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OPS", "Operations"), Ctx(w.Tenant), default);
        await Reference(w, cc, kind);

        Func<Task> act = op switch
        {
            "delete" => () => w.Svc.DeleteCostCenterAsync(w.Tenant, cc.Id, Ctx(w.Tenant), default),
            "deactivate" => () => w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "OPS", "Operations", active: false), Ctx(w.Tenant), default),
            _ => () => w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.B, "OPS", "Operations"), Ctx(w.Tenant), default),
        };
        var ex = await Refused(act);
        (ex.Code, ex.Status).Should().Be(("cost_centre_in_use", 409));
        ex.Counts.Should().ContainKey(kind).WhoseValue.Should().Be(1);

        var row = await w.Db.CostCenters.AsNoTracking().SingleAsync(x => x.Id == cc.Id);
        (row.IsActive, row.IsDeleted, row.CompanyId).Should().Be((true, false, w.A.Id), "a refused change writes nothing");
    }

    [Fact]
    public async Task T047_TheRefusalCarriesTheVerbatimM8Text_InBothLanguages()
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OPS", "Operations"), Ctx(w.Tenant), default);
        await Reference(w, cc, "department");
        var ex = await Refused(() => w.Svc.DeleteCostCenterAsync(w.Tenant, cc.Id, Ctx(w.Tenant), default));
        ex.Message.Should().Be("Cost centre OPS is used by 1 department(s), 0 employee(s), 0 position(s) and 0 posting rule(s), so it cannot be deleted, deactivated or moved to another company.");
        ex.MessageAr.Should().Be("مركز التكلفة OPS مستخدم في 1 قسم/أقسام و0 موظف/موظفين و0 منصب/مناصب و0 قاعدة ترحيل، لذا لا يمكن حذفه أو إيقافه أو نقله إلى شركة أخرى.");
    }

    [Fact]
    public async Task T047_DeletedInactiveOrHistoricalReferences_DoNotBlock()
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OPS", "Operations"), Ctx(w.Tenant), default);
        w.Db.Departments.Add(new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "GONE", NameEn = "Gone", CostCenterId = cc.Id, IsDeleted = true });
        w.Db.CostCenters.Add(new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "OLDKID", Name = "Old kid", ParentId = cc.Id, IsActive = false });
        w.Db.FinanceGlEntries.Add(new FinanceGlEntry { TenantId = w.Tenant, CompanyId = w.A.Id, CostCenterId = cc.Id, CostCenterCode = "OPS", Amount = 5, Currency = "SAR", Period = "2026-06" });
        await w.Db.SaveChangesAsync();

        var off = await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "OPS", "Operations", active: false), Ctx(w.Tenant), default);
        off!.IsActive.Should().BeFalse("a cost centre referenced only by history may be deactivated; the snapshots protect the history");
        (await w.Svc.DeleteCostCenterAsync(w.Tenant, cc.Id, Ctx(w.Tenant), default)).Should().BeTrue();
    }

    // ── T-048 code and company are frozen once a posted line carries the cost centre (M-9) ─────

    private static async Task PostLine(World w, CostCenterDto cc) =>
        await w.Db.FinanceGlEntries.AddAsync(new FinanceGlEntry { TenantId = w.Tenant, CompanyId = w.A.Id, CostCenterId = cc.Id, CostCenterCode = cc.Code, Amount = 5, Currency = "SAR", Period = "2026-06" });

    [Fact]
    public async Task T048_ACodeChange_AfterAPostedLine_IsRefusedWithM9_ButARenameIsAllowed()
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OPS", "Operations"), Ctx(w.Tenant), default);
        await PostLine(w, cc); await w.Db.SaveChangesAsync();

        var ex = await Refused(() => w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "OPS2", "Operations"), Ctx(w.Tenant), default));
        (ex.Code, ex.Status).Should().Be(("cost_centre_code_locked", 409));
        ex.Message.Should().Be("The code of cost centre OPS cannot change because journal lines already carry it. You can rename it.");
        ex.MessageAr.Should().Be("لا يمكن تغيير رمز مركز التكلفة OPS لأن قيود دفتر اليومية تحمله بالفعل. يمكنك تغيير الاسم.");

        var moved = await Refused(() => w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.B, "OPS", "Operations"), Ctx(w.Tenant), default));
        moved.Code.Should().Be("cost_centre_code_locked");

        var renamed = await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "ops", "Operations and Support"), Ctx(w.Tenant), default);
        (renamed!.Code, renamed.Name).Should().Be(("OPS", "Operations and Support"), "the same code in another case is not a change");
    }

    [Fact]
    public async Task T048_WithNoPostedLine_TheCodeMayStillChange()
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OPS", "Operations"), Ctx(w.Tenant), default);
        (await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "OPS2", "Operations"), Ctx(w.Tenant), default))!.Code.Should().Be("OPS2");
    }

    // ── T-045 (in-memory half): one audit row per change, with before/after and the company ────

    [Fact]
    public async Task T045_EveryChangeWritesOneAuditRow_WithBeforeAfterAndTheCompany()
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OPS", "Operations"), Ctx(w.Tenant), default);
        await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "OPS", "Renamed", cls: "COGS", reason: "finance review"), Ctx(w.Tenant), default);
        await w.Svc.DeleteCostCenterAsync(w.Tenant, cc.Id, Ctx(w.Tenant), default);

        var rows = await w.Db.AuditLogs.Where(x => x.EntityName == "CostCenter").OrderBy(x => x.CreatedAtUtc).ToListAsync();
        rows.Select(r => r.Action).Should().Equal("organization.cost_center_created", "organization.cost_center_updated", "organization.cost_center_deleted");
        rows.Should().OnlyContain(r => r.CompanyId == w.A.Id && r.TenantId == w.Tenant);

        var update = JsonDocument.Parse(rows[1].Metadata!).RootElement;
        update.GetProperty("before").GetProperty("name").GetString().Should().Be("Operations");
        update.GetProperty("after").GetProperty("name").GetString().Should().Be("Renamed");
        update.GetProperty("after").GetProperty("functionalClass").GetString().Should().Be("COGS");
        update.GetProperty("reason").GetString().Should().Be("finance review");
        JsonDocument.Parse(rows[0].Metadata!).RootElement.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        AuditService.VerifyChain(rows).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_RefusedChange_LeavesNoAuditRow()
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OPS", "Operations"), Ctx(w.Tenant), default);
        await Reference(w, cc, "department");
        var before = await w.Db.AuditLogs.CountAsync();
        await Refused(() => w.Svc.DeleteCostCenterAsync(w.Tenant, cc.Id, Ctx(w.Tenant), default));
        (await w.Db.AuditLogs.CountAsync()).Should().Be(before);
    }

    // ── Coordinator: the audit action WP-B's separation-of-duties check reads ──────────────────────

    [Fact]
    public async Task Sod_AClassChange_WritesOrganizationCostcenterClassChanged_WithCompanyBeforeAfterAndReason()
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "OPS", "Operations"), Ctx(w.Tenant), default);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "organization.costcenter.class_changed")).Should().Be(0, "no class was set");

        await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "OPS", cls: "COGS", reason: "finance review"), Ctx(w.Tenant), default);
        var row = await w.Db.AuditLogs.SingleAsync(a => a.Action == "organization.costcenter.class_changed");
        (row.CompanyId, row.EntityName, row.EntityId, row.TenantId).Should().Be((w.A.Id, "CostCenter", cc.Id.ToString(), w.Tenant));
        var json = JsonDocument.Parse(row.Metadata!).RootElement;
        json.GetProperty("before").GetProperty("functionalClass").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("after").GetProperty("functionalClass").GetString().Should().Be("COGS");
        json.GetProperty("reason").GetString().Should().Be("finance review");

        // Resending the same class, or renaming, is not a class change.
        await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, Req(w.A, "OPS", "Renamed", cls: "COGS"), Ctx(w.Tenant), default);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "organization.costcenter.class_changed")).Should().Be(1);
    }

    [Fact]
    public async Task Sod_CreatingACostCentreWithAClass_AlsoWritesTheClassChangedRow()
    {
        var w = await Seed();
        await w.Svc.CreateCostCenterAsync(w.Tenant, Req(w.A, "SEL", "Selling", cls: "SELLING"), Ctx(w.Tenant), default);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "organization.costcenter.class_changed" && a.CompanyId == w.A.Id)).Should().Be(1);
    }
}
