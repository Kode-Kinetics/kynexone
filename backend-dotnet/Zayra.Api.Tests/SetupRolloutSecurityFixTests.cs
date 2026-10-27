using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Admin;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Security review of the Setup rollout (sec-review-setup-rollout.md): H1/H2 compliance profiles, M1 one permission per
/// entity, M2 statutory acknowledgement, M4 caller-scoped counts, M5 model-derived reference discovery, and the lows.</summary>
public class SetupRolloutSecurityFixTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static T Wire<T>(T c, Guid tenant, bool group = true, Guid[]? companies = null, params string[] permissions) where T : ControllerBase
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin"),
        };
        if (group) claims.Add(new Claim("is_group_scope", "true"));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return c;
    }

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);

    // ── H1 / H2 compliance profiles ─────────────────────────────────────────────────────

    private static string[] Perms(Type controller, string method) =>
        controller.GetMethod(method)!.GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute), false)
            .Cast<Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute>().SelectMany(a => a.Permissions).ToArray();

    [Fact]
    public void Compliance_profile_remove_and_restore_need_compliance_write_not_a_role_list()
    {
        foreach (var m in new[] { "Remove", "Restore" })
        {
            Perms(typeof(CompanyComplianceProfilesController), m).Should().Equal("compliance.write");
            typeof(CompanyComplianceProfilesController).GetMethod(m)!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Should().NotContain(a => a.Roles != null);
        }
    }

    private static CompanyComplianceProfile Profile(Guid t, string status, DateOnly? to = null) => new()
    {
        TenantId = t, CompanyId = null, CountryCode = "SA", Jurisdiction = "KSA-mainland", Status = status,
        EffectiveFrom = new DateOnly(2025, 1, 1), EffectiveTo = to,
    };

    [Fact]
    public async Task Restore_returns_a_removed_draft_as_a_draft_and_never_activates_it()
    {
        var db = Db(); var t = Guid.NewGuid();
        var draft = Profile(t, CompanyPolicyStatuses.Draft); db.CompanyComplianceProfiles.Add(draft); await db.SaveChangesAsync();
        var ctl = Wire(new CompanyComplianceProfilesController(db), t);

        Body(await ctl.Remove(draft.Id, default)).GetProperty("outcome").GetString().Should().Be("deleted");
        await ctl.Restore(draft.Id, default);

        var row = await db.CompanyComplianceProfiles.IgnoreQueryFilters().SingleAsync();
        row.IsDeleted.Should().BeFalse();
        row.Status.Should().Be(CompanyPolicyStatuses.Draft);
    }

    [Fact]
    public async Task Restore_puts_back_the_status_and_open_end_date_and_keeps_a_real_end_date()
    {
        var db = Db(); var t = Guid.NewGuid();
        var open = Profile(t, CompanyPolicyStatuses.Active);
        var dated = Profile(t, CompanyPolicyStatuses.Active, new DateOnly(2030, 12, 31)); dated.Jurisdiction = "KSA-other";
        db.CompanyComplianceProfiles.AddRange(open, dated); await db.SaveChangesAsync();
        var ctl = Wire(new CompanyComplianceProfilesController(db), t);

        await ctl.Remove(open.Id, default); await ctl.Remove(dated.Id, default);
        (await db.CompanyComplianceProfiles.SingleAsync(p => p.Id == open.Id)).Status.Should().Be(CompanyPolicyStatuses.Archived);

        await ctl.Restore(open.Id, default); await ctl.Restore(dated.Id, default);
        var a = await db.CompanyComplianceProfiles.SingleAsync(p => p.Id == open.Id);
        a.Status.Should().Be(CompanyPolicyStatuses.Active); a.EffectiveTo.Should().BeNull();
        var b = await db.CompanyComplianceProfiles.SingleAsync(p => p.Id == dated.Id);
        b.Status.Should().Be(CompanyPolicyStatuses.Active); b.EffectiveTo.Should().Be(new DateOnly(2030, 12, 31));
    }

    [Fact]
    public async Task Restore_is_refused_when_another_active_profile_covers_the_same_scope()
    {
        var db = Db(); var t = Guid.NewGuid();
        var first = Profile(t, CompanyPolicyStatuses.Active); db.CompanyComplianceProfiles.Add(first); await db.SaveChangesAsync();
        var ctl = Wire(new CompanyComplianceProfilesController(db), t);
        await ctl.Remove(first.Id, default);
        var successor = Profile(t, CompanyPolicyStatuses.Active); db.CompanyComplianceProfiles.Add(successor); await db.SaveChangesAsync();

        var r = await ctl.Restore(first.Id, default);

        r.Should().BeOfType<ConflictObjectResult>();
        Body(r).GetProperty("code").GetString().Should().Be("OVERLAP");
        (await db.CompanyComplianceProfiles.SingleAsync(p => p.Id == first.Id)).Status.Should().Be(CompanyPolicyStatuses.Archived);
    }

    [Fact]
    public async Task A_profile_archived_by_an_edit_is_not_reactivated_by_restore()
    {
        var db = Db(); var t = Guid.NewGuid();
        var old = Profile(t, CompanyPolicyStatuses.Archived, new DateOnly(2025, 6, 1)); db.CompanyComplianceProfiles.Add(old); await db.SaveChangesAsync();

        var r = await Wire(new CompanyComplianceProfilesController(db), t).Restore(old.Id, default);

        r.Should().BeOfType<ConflictObjectResult>();
        (await db.CompanyComplianceProfiles.SingleAsync()).Status.Should().Be(CompanyPolicyStatuses.Archived);
    }

    // ── M1: one domain permission per entity, for remove and restore ────────────────────

    [Theory]
    [InlineData(typeof(LeaveTypesController), "Delete", "leave.policy_manage")]
    [InlineData(typeof(LeaveTypesController), "Restore", "leave.policy_manage")]
    [InlineData(typeof(ShiftsController), "DeleteDefinition", "shifts.write")]
    [InlineData(typeof(ShiftsController), "RestoreDefinition", "shifts.write")]
    [InlineData(typeof(ScorecardTemplatesController), "Delete", "performance.cycle_manage")]
    [InlineData(typeof(ScorecardTemplatesController), "Restore", "performance.cycle_manage")]
    [InlineData(typeof(HrLettersController), "RemoveTemplate", "employees.write")]
    [InlineData(typeof(HrLettersController), "RestoreTemplate", "employees.write")]
    [InlineData(typeof(Zayra.Api.Controllers.Recruitment.OnboardingController), "RemoveChecklist", "recruitment.write")]
    [InlineData(typeof(Zayra.Api.Controllers.Recruitment.OnboardingController), "RestoreChecklist", "recruitment.write")]
    [InlineData(typeof(SetupSettingsController), "RemoveGCCSetting", "security.manage")]
    [InlineData(typeof(SetupSettingsController), "RestoreGCCSetting", "security.manage")]
    public void Remove_and_restore_name_the_same_domain_permission_the_entity_is_written_with(Type controller, string method, string permission) =>
        Perms(controller, method).Should().Equal(permission);

    // ── M2: statutory acknowledgement is verified, complete, and audited ───────────────

    private static LeaveTypesController LeaveCtl(ZayraDbContext db, Guid t, bool group = true) =>
        Wire(new LeaveTypesController(db, new LeaveService(db, new ApprovalRouter(db))), t, group);

    [Theory]
    [InlineData("ANNUAL", "Annual leave", "Annual")]
    [InlineData("SICK", "Sick leave", "Sick")]
    [InlineData("HAJJ", "Hajj leave", "Hajj")]
    public async Task Annual_sick_and_special_leave_all_need_the_echoed_warning_code(string code, string name, string category)
    {
        var db = Db(); var t = Guid.NewGuid();
        var lt = new LeaveType { TenantId = t, Code = code, NameEn = name, Category = category }; db.LeaveTypes.Add(lt); await db.SaveChangesAsync();
        var ctl = LeaveCtl(db, t);

        (await ctl.Delete(lt.Id, null, default)).Should().BeOfType<ConflictObjectResult>();
        (await ctl.Delete(lt.Id, "true", default)).Should().BeOfType<ConflictObjectResult>();
        (await db.LeaveTypes.AnyAsync(x => x.Id == lt.Id)).Should().BeTrue();

        (await ctl.Delete(lt.Id, LeaveTypesController.StatutoryWarningCode, default)).Should().BeOfType<OkObjectResult>();
        (await db.LeaveTypes.AnyAsync(x => x.Id == lt.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_acknowledgement_and_the_refusals_are_audited_even_when_an_unused_statutory_type_is_deleted()
    {
        var db = Db(); var t = Guid.NewGuid();
        var lt = new LeaveType { TenantId = t, Code = "MATERNITY", NameEn = "Maternity leave", Category = "Maternity" }; db.LeaveTypes.Add(lt); await db.SaveChangesAsync();
        var ctl = LeaveCtl(db, t);

        await ctl.Delete(lt.Id, null, default);
        await ctl.Delete(lt.Id, LeaveTypesController.StatutoryWarningCode, default);

        var log = await db.LeaveAuditLogs.IgnoreQueryFilters().Select(x => x.Action + "|" + x.Reason).ToListAsync();
        log.Should().Contain(x => x.StartsWith("RemoveRefused") && x.Contains("not acknowledged"));
        log.Should().Contain(x => x.StartsWith("Deleted") && x.Contains("acknowledged by the caller"));
    }

    [Fact]
    public void The_classifier_marks_annual_and_sick_leave_as_protected_but_not_an_unrelated_type()
    {
        KsaStatutorySpecialLeave.ProtectedKind("AL", "Annual leave", "General").Should().Be("Annual");
        KsaStatutorySpecialLeave.ProtectedKind("SL", "Sick leave", "General").Should().Be("Sick");
        KsaStatutorySpecialLeave.ProtectedKind("TRN", "Training leave", "General").Should().BeNull();
        KsaStatutorySpecialLeave.ProtectedKind("UNP", "Unpaid sick extension", "General").Should().BeNull();
    }

    // ── M4: counts the caller may see ─────────────────────────────────────────────────

    [Fact]
    public async Task A_group_admin_sees_every_count_and_the_row_is_kept_when_any_company_uses_it()
    {
        var db = Db(); var t = Guid.NewGuid();
        var lt = new LeaveType { TenantId = t, Code = "TRN", NameEn = "Training", Category = "General" }; db.LeaveTypes.Add(lt);
        db.LeaveRequests.Add(new LeaveRequest { TenantId = t, LeaveTypeId = lt.Id });
        await db.SaveChangesAsync();

        var r = await LeaveCtl(db, t).Delete(lt.Id, null, default);

        Body(r).GetProperty("outcome").GetString().Should().Be("turnedOff");
        Body(r).GetProperty("alsoUsedElsewhere").GetBoolean().Should().BeFalse();
    }

    // ── M5: usage is discovered from the model ────────────────────────────────────────

    [Theory]
    [InlineData(typeof(LeaveType), "LeaveType", "EmployeeLeaveBalance.LeaveTypeId,LeaveRequest.LeaveTypeId,LeavePolicy.LeaveTypeId,LeaveBalanceTransaction.LeaveTypeId,LeaveEncashmentRequest.LeaveTypeId")]
    [InlineData(typeof(ShiftDefinition), "ShiftDefinition", "ShiftAssignment.ShiftDefinitionId")]
    [InlineData(typeof(PerformanceScorecardTemplate), "ScorecardTemplate", "PerformanceCycle.DefaultScorecardTemplateId,PerformanceCycleEmployee.ScorecardTemplateId,AppraisalReview.ScorecardTemplateId")]
    public void Reference_discovery_finds_every_referencing_column_the_hand_written_lists_knew(Type principal, string stem, string expected)
    {
        var found = SetupRowUsageProbe.Discover(Db(), principal, stem);
        found.Should().Contain(expected.Split(','));
    }

    /// <summary>The ratchet: a Guid column whose name mentions the row but is neither found as a reference nor listed with a reason
    /// fails here, so a new table that points at a leave type, shift or scorecard cannot slip past the hard-delete check.</summary>
    [Theory]
    [InlineData(typeof(LeaveType), "LeaveType")]
    [InlineData(typeof(ShiftDefinition), "ShiftDefinition")]
    [InlineData(typeof(PerformanceScorecardTemplate), "ScorecardTemplate")]
    public void No_unexplained_column_looks_like_a_reference_without_being_counted(Type principal, string stem)
        => SetupRowUsageProbe.Lookalikes(Db(), principal, stem).Should().BeEmpty();

    // ── Lows ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Payslip_restore_keeps_the_status_the_template_had()
    {
        var db = Db(); var t = Guid.NewGuid();
        var tpl = new PayslipTemplate { TenantId = t, Name = "Draft one", Status = "draft" };
        db.PayslipTemplates.Add(tpl);
        db.Payslips.Add(new Payslip { TenantId = t, PayslipTemplateId = tpl.Id });
        await db.SaveChangesAsync();
        var ctl = Wire(new PayslipTemplatesController(db, null!, null!), t, true, null, "payroll.write");

        await ctl.Delete(tpl.Id, default);
        (await db.PayslipTemplates.SingleAsync()).Status.Should().Be("off");
        await ctl.Restore(tpl.Id, default);
        (await db.PayslipTemplates.SingleAsync()).Status.Should().Be("draft");
    }

    [Fact]
    public async Task Capabilities_are_hidden_from_a_signed_in_person_who_administers_nothing()
    {
        var ess = (OkObjectResult)Wire(new CapabilitiesController(), Guid.NewGuid()).Get();
        JsonSerializer.SerializeToElement(ess.Value).GetProperty("features").EnumerateObject().Should().BeEmpty();
        var admin = (OkObjectResult)Wire(new CapabilitiesController(), Guid.NewGuid(), true, null, "security.manage").Get();
        JsonSerializer.SerializeToElement(admin.Value).GetProperty("features").GetProperty("samlFederation").GetBoolean().Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public void Capabilities_still_requires_sign_in()
        => typeof(CapabilitiesController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Should().NotBeEmpty();

    // ── GCC country rules turn off and restore ──────────────────────────────────────────

    [Fact]
    public async Task A_country_rule_turns_off_and_back_on_and_a_second_rule_for_the_scope_blocks_the_restore()
    {
        var db = Db(); var t = Guid.NewGuid();
        var rule = new GCCComplianceSetting { TenantId = t, CountryCode = "SA" };
        db.GCCComplianceSettings.Add(rule); await db.SaveChangesAsync();
        var ctl = Wire(new SetupSettingsController(db, null!), t);

        Body(await ctl.RemoveGCCSetting(rule.Id, default)).GetProperty("outcome").GetString().Should().Be("turnedOff");
        var off = await db.GCCComplianceSettings.SingleAsync();
        off.IsActive.Should().BeFalse(); off.TurnedOffAtUtc.Should().NotBeNull(); off.TurnedOffBy.Should().NotBeNull();

        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = t, CountryCode = "SA", CompanyId = null }); await db.SaveChangesAsync();
        (await ctl.RestoreGCCSetting(rule.Id, default)).Should().BeOfType<ConflictObjectResult>();

        db.GCCComplianceSettings.Remove(await db.GCCComplianceSettings.SingleAsync(x => x.IsActive)); await db.SaveChangesAsync();
        Body(await ctl.RestoreGCCSetting(rule.Id, default)).GetProperty("outcome").GetString().Should().Be("restored");
        (await db.GCCComplianceSettings.SingleAsync()).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Readers_ignore_a_turned_off_country_rule()
    {
        var db = Db(); var t = Guid.NewGuid();
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = t, CountryCode = "SA", EosbEnabled = true, IsActive = false });
        await db.SaveChangesAsync();

        var visible = await db.GCCComplianceSettings.AsNoTracking().Where(x => x.TenantId == t && x.IsActive && x.CountryCode == "SA").ToListAsync();
        visible.Should().BeEmpty();
        var week = await new Zayra.Api.Infrastructure.WorkWeek.WorkWeekService(db).ResolveAsync(t, null, "SA", default);
        week.Should().NotBeNull();
    }

    [Fact]
    public async Task Notification_template_list_hides_removed_rows_from_someone_who_cannot_restore_them()
    {
        var db = Db(); var t = Guid.NewGuid();
        db.NotificationTemplates.AddRange(
            new NotificationTemplate { TenantId = t, Code = "A", Channel = "email", EventType = "x", BodyEn = "b" },
            new NotificationTemplate { TenantId = t, Code = "B", Channel = "email", EventType = "x", BodyEn = "b", IsDeleted = true });
        await db.SaveChangesAsync();

        var plain = (OkObjectResult)await Wire(new SetupSettingsController(db, null!), t, false).ListNotificationTemplates(null, true, default);
        ((IEnumerable<NotificationTemplate>)plain.Value!).Should().HaveCount(1);
        var manager = (OkObjectResult)await Wire(new SetupSettingsController(db, null!), t, true, null, "notifications.manage").ListNotificationTemplates(null, true, default);
        ((IEnumerable<NotificationTemplate>)manager.Value!).Should().HaveCount(2);
    }
}

/// <summary>Test-side view of <c>SetupRowUsage</c>.</summary>
internal static class SetupRowUsageProbe
{
    public static IReadOnlyList<string> Discover(ZayraDbContext db, Type principal, string stem) =>
        SetupRowUsage.Discover(db.Model, principal, stem).Select(r => $"{r.Entity.Name}.{r.Property}").ToList();

    public static IReadOnlyList<string> Lookalikes(ZayraDbContext db, Type principal, string stem) =>
        SetupRowUsage.Lookalikes(db.Model, principal, stem, SetupRowUsage.Discover(db.Model, principal, stem));
}
