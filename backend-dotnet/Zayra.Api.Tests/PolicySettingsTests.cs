using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Admin;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Policy;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class PolicySettingsTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ZayraDbContext _db;
    private readonly PolicySettingsService _svc;
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static readonly PolicyActor Actor = new(Guid.NewGuid(), "Admin One", "127.0.0.1");

    public PolicySettingsTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _svc = new PolicySettingsService(_db);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task Defaults_match_the_agreed_registry()
    {
        (await _svc.GetEnumAsync(A, "overtime.late_approval_mode")).Should().Be("Refuse");
        (await _svc.GetEnumAsync(A, "overtime.compoff_cap_mode")).Should().Be("WholeDays");
        (await _svc.GetBoolAsync(A, "payroll.allow_skip_review")).Should().BeFalse();
        (await _svc.GetBoolAsync(A, "payroll.allow_same_approver_multiple_levels")).Should().BeFalse();
        (await _svc.GetCsvAsync(A, "employees.profile_change_approval_fields")).Should()
            .Equal("bank_iban", "national_id", "passport_number", "salary", "iqama_number", "sponsor_name", "nationality",
                "preferred_name", "personal_email", "phone", "marital_status", "emergency_contact_name", "emergency_contact_phone");
        PolicyRegistry.All.Select(d => d.Key).Should().BeEquivalentTo(new[]
        {
            "overtime.late_approval_mode", "overtime.compoff_cap_mode", "payroll.allow_skip_review",
            "payroll.allow_same_approver_multiple_levels", "employees.profile_change_approval_fields",
        });
    }

    [Fact]
    public async Task Unknown_key_throws_on_every_reader()
    {
        await FluentActions.Awaiting(() => _svc.GetBoolAsync(A, "nope.nothing")).Should().ThrowAsync<UnknownPolicyKeyException>();
        await FluentActions.Awaiting(() => _svc.GetStringAsync(A, "nope.nothing")).Should().ThrowAsync<UnknownPolicyKeyException>();
        await FluentActions.Awaiting(() => _svc.GetCsvAsync(A, "nope.nothing")).Should().ThrowAsync<UnknownPolicyKeyException>();
    }

    [Fact]
    public async Task Asking_for_the_wrong_type_throws()
    {
        await FluentActions.Awaiting(() => _svc.GetBoolAsync(A, "overtime.late_approval_mode")).Should().ThrowAsync<InvalidOperationException>();
    }

    private enum LateMode { Refuse, BackPay }

    [Fact]
    public async Task Typed_enum_read_returns_the_override()
    {
        await _svc.SetAsync(A, "overtime.late_approval_mode", "backpay", Actor);
        (await _svc.GetEnumAsync<LateMode>(A, "overtime.late_approval_mode")).Should().Be(LateMode.BackPay);
    }

    [Fact]
    public async Task Tenant_override_does_not_leak_to_another_tenant()
    {
        (await _svc.SetAsync(B, "payroll.allow_skip_review", "true", Actor)).Status.Should().Be(PolicySetStatus.Changed);
        (await _svc.GetBoolAsync(B, "payroll.allow_skip_review")).Should().BeTrue();
        (await _svc.GetBoolAsync(A, "payroll.allow_skip_review")).Should().BeFalse();
        (await _svc.ListAsync(A)).Single(v => v.Definition.Key == "payroll.allow_skip_review").IsDefault.Should().BeTrue();
    }

    [Theory]
    [InlineData("payroll.allow_skip_review", "maybe")]
    [InlineData("overtime.late_approval_mode", "Sometimes")]
    [InlineData("overtime.compoff_cap_mode", "")]
    public async Task Invalid_values_are_rejected_and_nothing_is_written(string key, string value)
    {
        var r = await _svc.SetAsync(A, key, value, Actor);
        r.Status.Should().Be(PolicySetStatus.Invalid);
        (await _db.SystemSettings.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _db.AdminAuditLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_key_on_write_is_reported()
    {
        (await _svc.SetAsync(A, "nope.nothing", "x", Actor)).Status.Should().Be(PolicySetStatus.UnknownKey);
    }

    [Fact]
    public async Task Csv_is_trimmed_lowercased_and_deduplicated()
    {
        await _svc.SetAsync(A, "employees.profile_change_approval_fields", " Salary, IBAN ,salary,,", Actor);
        (await _svc.GetCsvAsync(A, "employees.profile_change_approval_fields")).Should().Equal("salary", "iban");
    }

    [Fact]
    public async Task Csv_can_be_cleared_to_no_approval_fields()
    {
        await _svc.SetAsync(A, "employees.profile_change_approval_fields", "", Actor);
        (await _svc.GetCsvAsync(A, "employees.profile_change_approval_fields")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_change_writes_one_audit_row_with_old_new_and_actor()
    {
        await _svc.SetAsync(A, "overtime.late_approval_mode", "BackPay", Actor);
        var log = await _db.AdminAuditLogs.IgnoreQueryFilters().SingleAsync();
        log.TenantId.Should().Be(A);
        log.EntityType.Should().Be("PolicySetting");
        log.EntityId.Should().Be("overtime.late_approval_mode");
        log.OldValuesJson.Should().Contain("Refuse");
        log.NewValuesJson.Should().Contain("BackPay");
        log.PerformedBy.Should().Be(Actor.UserId);
        log.PerformedByName.Should().Be("Admin One");
    }

    [Fact]
    public async Task Second_change_audits_the_previous_override_as_old()
    {
        await _svc.SetAsync(A, "overtime.late_approval_mode", "BackPay", Actor);
        await _svc.SetAsync(A, "overtime.late_approval_mode", "Refuse", Actor);
        var logs = await _db.AdminAuditLogs.IgnoreQueryFilters().OrderBy(l => l.CreatedAtUtc).ToListAsync();
        logs.Should().HaveCount(2);
        logs[1].OldValuesJson.Should().Contain("BackPay");
        logs[1].NewValuesJson.Should().Contain("Refuse");
    }

    [Fact]
    public async Task Unchanged_value_writes_no_audit_and_no_row()
    {
        (await _svc.SetAsync(A, "payroll.allow_skip_review", "false", Actor)).Status.Should().Be(PolicySetStatus.Unchanged);
        await _svc.SetAsync(A, "payroll.allow_skip_review", "true", Actor);
        (await _svc.SetAsync(A, "payroll.allow_skip_review", "TRUE", Actor)).Status.Should().Be(PolicySetStatus.Unchanged);
        (await _db.AdminAuditLogs.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    // ── controller ──────────────────────────────────────────────────────────

    private PolicySettingsController Ctrl(Guid tenant)
    {
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "Admin One") };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new PolicySettingsController(_svc) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public async Task Get_lists_every_key_with_default_current_and_isDefault()
    {
        var ok = (await Ctrl(A).List(default)).Should().BeOfType<OkObjectResult>().Subject;
        var items = ok.Value.Should().BeAssignableTo<IReadOnlyList<PolicySettingDto>>().Subject;
        items.Should().HaveCount(5);
        var ot = items.Single(i => i.Key == "overtime.late_approval_mode");
        ot.AllowedValues.Should().Equal("Refuse", "BackPay");
        ot.DefaultValue.Should().Be("Refuse"); ot.Value.Should().Be("Refuse"); ot.IsDefault.Should().BeTrue();
        ot.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Put_maps_results_to_status_codes()
    {
        var c = Ctrl(A);
        (await c.Update("overtime.late_approval_mode", new("BackPay"), default)).Should().BeOfType<OkObjectResult>();
        (await c.Update("overtime.late_approval_mode", new("zzz"), default)).Should().BeOfType<BadRequestObjectResult>();
        (await c.Update("no.such", new("x"), default)).Should().BeOfType<NotFoundObjectResult>();
        (await c.Update("overtime.late_approval_mode", new("BackPay"), default)).Should().BeOfType<OkObjectResult>(); // no-op still 200
    }

    [Fact]
    public void Endpoints_require_the_setup_permission_not_inline_role_checks()
    {
        foreach (var m in new[] { nameof(PolicySettingsController.List), nameof(PolicySettingsController.Update) })
        {
            var attr = typeof(PolicySettingsController).GetMethod(m)!.GetCustomAttribute<HasPermissionAttribute>();
            attr.Should().NotBeNull($"{m} must be permission-gated");
        }
        typeof(PolicySettingsController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
    }

    [Fact]
    public async Task Generic_system_settings_endpoint_cannot_write_the_policy_category()
    {
        var claims = new List<Claim> { new("tenant_id", A.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new SetupSettingsController(_db, null!) { ControllerContext = new ControllerContext { HttpContext = http } };
        var r = await ctrl.UpsertSystemSetting(new SystemSettingRequest("Policy", "payroll.allow_skip_review", "true", null, null), null!, default);
        r.Should().BeOfType<ForbidResult>();
        (await _db.SystemSettings.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
