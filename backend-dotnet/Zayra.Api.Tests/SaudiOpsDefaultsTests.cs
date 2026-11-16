using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Launch wave: what a NEW Saudi company starts with.
///
/// <para><b>M7 — the working day.</b> A company with no shift assigned used to be judged against a hard-coded 09:00 start plus the
/// policy's working day and break. A Saudi office that works 08:00–17:00 (480 working minutes and a 60-minute break) therefore
/// showed a 45-minute early exit every day (60 minutes before the assumed 18:00 end, less the 15-minute allowance). A new Saudi
/// tenant's default attendance policy now carries the 08:00 start, so the default window is 08:00–17:00 and the policy's grace
/// (10) and early-exit allowance (15) mean what they say. A policy with no start keeps the 09:00 rule exactly, so every existing
/// company reads the same as before.</para>
///
/// <para><b>L5.3 — the payslip language.</b> A new Saudi company's default payslip template is bilingual (Branding.Locale =
/// "bilingual"). A tenant that already has a default template keeps it; nothing existing is rewritten.</para>
/// </summary>
public class SaudiOpsDefaultsTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateOnly Sunday = new(2026, 11, 8); // a working day on the Sunday–Thursday week

    private static DateTime At(int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(Sunday.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Unspecified), Riyadh);

    private static AttendancePolicy Policy(TimeOnly? defaultStart = null) => new()
    {
        TenantId = Guid.NewGuid(), Code = "DEFAULT", Name = "Default attendance policy",
        GraceMinutes = 10, EarlyExitThresholdMinutes = 15, HalfDayThresholdMinutes = 240, StandardWorkMinutes = 480, BreakMinutes = 60,
        DefaultShiftStart = defaultStart,
    };

    // ── M7: the window ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApolicyWithNoStart_KeepsTheLegacyNineOClockWindow_SoExistingCompaniesAreUnchanged()
    {
        var (late, early) = AttendanceDerivedArtifacts.LateAndEarlyExit(At(8, 0), At(17, 0), Sunday, null, null, Riyadh, Policy());
        (late, early).Should().Be((0, 45), "09:00 + 480 + 60 ends at 18:00: leaving at 17:00 is 60 minutes early, less the 15-minute allowance");
    }

    [Fact]
    public void TheKsaDefaultStart_MakesEightToFive_OnTime()
    {
        var (late, early) = AttendanceDerivedArtifacts.LateAndEarlyExit(At(8, 0), At(17, 0), Sunday, null, null, Riyadh, Policy(new TimeOnly(8, 0)));
        (late, early).Should().Be((0, 0));
    }

    [Fact]
    public void TheKsaDefaultStart_StillCatchesARealLateArrivalAndARealEarlyExit()
    {
        var (late, early) = AttendanceDerivedArtifacts.LateAndEarlyExit(At(8, 25), At(16, 30), Sunday, null, null, Riyadh, Policy(new TimeOnly(8, 0)));
        late.Should().Be(15, "25 minutes after 08:00, less the 10-minute grace");
        early.Should().Be(15, "30 minutes before 17:00, less the 15-minute allowance");
    }

    [Fact]
    public void AnAssignedShift_AlwaysWinsOverThePolicysDefaultStart()
    {
        var (late, early) = AttendanceDerivedArtifacts.LateAndEarlyExit(At(10, 0), At(19, 0), Sunday,
            new TimeOnly(10, 0), new TimeOnly(19, 0), Riyadh, Policy(new TimeOnly(8, 0)));
        (late, early).Should().Be((0, 0));
    }

    [Fact]
    public void TheDayHasNotEnded_UntilTheDefaultWindowCloses()
    {
        var policy = Policy(new TimeOnly(8, 0));
        AttendanceDerivedArtifacts.DayHasEnded(At(16, 59), Sunday, null, null, Riyadh, policy).Should().BeFalse();
        AttendanceDerivedArtifacts.DayHasEnded(At(17, 0), Sunday, null, null, Riyadh, policy).Should().BeTrue();
    }

    // ── M7: what a new tenant is born with ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SA")]
    [InlineData("sa")]
    public async Task ANewSaudiTenants_DefaultPolicy_StartsAtEight_AndItsNumbersAddUpToFive(string home)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        (await TenantProvisioningBundle.InstallDefaultAttendancePolicyAsync(db, tenantId, home, default)).Should().Be(1);
        await db.SaveChangesAsync();

        var policy = await db.AttendancePolicies.IgnoreQueryFilters().SingleAsync(p => p.TenantId == tenantId);
        policy.DefaultShiftStart.Should().Be(new TimeOnly(8, 0));
        (policy.GraceMinutes, policy.EarlyExitThresholdMinutes, policy.StandardWorkMinutes, policy.BreakMinutes)
            .Should().Be((10, 15, 480, 60));
        var (_, end) = AttendanceDerivedArtifacts.ShiftWindowUtc(Sunday, null, null, Riyadh, policy);
        TimeZoneInfo.ConvertTimeFromUtc(end, Riyadh).TimeOfDay.Should().Be(new TimeSpan(17, 0, 0),
            "08:00 plus 480 working minutes plus the 60-minute break");
    }

    [Theory]
    [InlineData("AE")]
    [InlineData("US")]
    public async Task ANewTenantOutsideSaudiArabia_KeepsTheLegacyStart(string home)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        await TenantProvisioningBundle.InstallDefaultAttendancePolicyAsync(db, tenantId, home, default);
        await db.SaveChangesAsync();

        (await db.AttendancePolicies.IgnoreQueryFilters().SingleAsync(p => p.TenantId == tenantId)).DefaultShiftStart.Should().BeNull();
    }

    [Fact]
    public async Task AnExistingDefaultPolicy_IsNeverRewritten_WhateverTheHomeCountry()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var existing = Policy();
        existing.TenantId = tenantId;
        db.AttendancePolicies.Add(existing);
        await db.SaveChangesAsync();

        (await TenantProvisioningBundle.InstallDefaultAttendancePolicyAsync(db, tenantId, "SA", default)).Should().Be(0);
        await db.SaveChangesAsync();

        (await db.AttendancePolicies.IgnoreQueryFilters().SingleAsync(p => p.TenantId == tenantId)).DefaultShiftStart.Should().BeNull();
    }

    // ── L5.3: the payslip default ─────────────────────────────────────────────────────────────────

    private static string LocaleOf(PayslipTemplate t) =>
        JsonSerializer.Deserialize<PayslipBrandingConfig>(t.BrandingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Locale;

    [Theory]
    [InlineData("SA")]
    [InlineData("sa")]
    [InlineData("SAU")]
    public async Task ANewSaudiCompany_GetsABilingualActiveDefaultPayslipTemplate(string country)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        (await PayslipTemplateDefaults.EnsureSaudiBilingualDefaultAsync(db, tenantId, country, default)).Should().BeTrue();
        await db.SaveChangesAsync();

        var template = await db.PayslipTemplates.IgnoreQueryFilters().SingleAsync(t => t.TenantId == tenantId);
        (template.IsDefault, template.Status, template.IsDeleted, template.Version).Should().Be((true, "active", false, 1));
        LocaleOf(template).Should().Be("bilingual");
        JsonSerializer.Deserialize<PayslipLayoutConfig>(template.LayoutJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Locale.Should().Be("bilingual");
        PayslipTemplateRegistry.ValidateBranding(JsonSerializer.Deserialize<PayslipBrandingConfig>(template.BrandingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Should().BeEmpty("the seeded template must pass the same validation a user's template does");
    }

    [Theory]
    [InlineData("AE")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ACompanyOutsideSaudiArabia_GetsNoTemplate(string? country)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        (await PayslipTemplateDefaults.EnsureSaudiBilingualDefaultAsync(db, tenantId, country, default)).Should().BeFalse();
        await db.SaveChangesAsync();

        (await db.PayslipTemplates.IgnoreQueryFilters().AnyAsync(t => t.TenantId == tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task ATenantThatAlreadyHasADefaultTemplate_KeepsIt_AndIsNotCalledTwice()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var english = new PayslipTemplate { TenantId = tenantId, Name = "Our payslip", IsDefault = true, Status = "active", BrandingJson = "{\"locale\":\"en\"}" };
        db.PayslipTemplates.Add(english);
        await db.SaveChangesAsync();

        (await PayslipTemplateDefaults.EnsureSaudiBilingualDefaultAsync(db, tenantId, "SA", default)).Should().BeFalse();
        await db.SaveChangesAsync();

        var all = await db.PayslipTemplates.IgnoreQueryFilters().Where(t => t.TenantId == tenantId).ToListAsync();
        all.Should().ContainSingle().Which.Id.Should().Be(english.Id);
        LocaleOf(all[0]).Should().Be("en");

        // And a second Saudi company in a fresh tenant does not add a second default.
        var fresh = Guid.NewGuid();
        await PayslipTemplateDefaults.EnsureSaudiBilingualDefaultAsync(db, fresh, "SA", default);
        await db.SaveChangesAsync();
        (await PayslipTemplateDefaults.EnsureSaudiBilingualDefaultAsync(db, fresh, "SA", default)).Should().BeFalse();
        await db.SaveChangesAsync();
        (await db.PayslipTemplates.IgnoreQueryFilters().CountAsync(t => t.TenantId == fresh)).Should().Be(1);
    }

    [Fact]
    public async Task CreatingASaudiCompany_SeedsTheBilingualDefault_AndCreatingAnotherCountrysDoesNot()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var service = new OrganizationSetupService(db, new AuditService(db));

        await service.CreateCompanyAsync(tenantId, new CompanyRequest("Gulf Trading", null, null, "SA", null, "G-1", null, null, null, null, "SAR"),
            new RequestContext("127.0.0.1", "tests", userId, tenantId), default);
        await service.CreateCompanyAsync(other, new CompanyRequest("Dubai Trading", null, null, "AE", null, "D-1", null, null, null, null, "AED"),
            new RequestContext("127.0.0.1", "tests", userId, other), default);

        var saudi = await db.PayslipTemplates.IgnoreQueryFilters().SingleAsync(t => t.TenantId == tenantId);
        LocaleOf(saudi).Should().Be("bilingual");
        (await db.PayslipTemplates.IgnoreQueryFilters().AnyAsync(t => t.TenantId == other)).Should().BeFalse();
    }
    [Fact]
    public async Task AnExistingTenantWithACompany_AddingASaudiCompany_GetsNoTenantWideBilingualDefault()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var service = new OrganizationSetupService(db, new AuditService(db));
        await service.CreateCompanyAsync(tenantId, new CompanyRequest("Dubai Trading", null, null, "AE", null, "D-1", null, null, null, null, "AED"),
            new RequestContext("127.0.0.1", "tests", userId, tenantId), default);
        (await db.PayslipTemplates.IgnoreQueryFilters().AnyAsync(t => t.TenantId == tenantId)).Should().BeFalse();

        await service.CreateCompanyAsync(tenantId, new CompanyRequest("Gulf Trading", null, null, "SA", null, "G-1", null, null, null, null, "SAR"),
            new RequestContext("127.0.0.1", "tests", userId, tenantId), default);

        (await db.PayslipTemplates.IgnoreQueryFilters().AnyAsync(t => t.TenantId == tenantId)).Should().BeFalse(
            "a tenant that already runs another company keeps its payslip exactly as it is");
    }

    [Fact]
    public async Task ATenantWithPayrollHistory_AddingASaudiCompany_GetsNoTenantWideBilingualDefault()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.PayrollRuns.Add(new PayrollRun { TenantId = tenantId, Year = 2026, Month = 10 });
        await db.SaveChangesAsync();

        (await PayslipTemplateDefaults.EnsureSaudiBilingualDefaultForNewCompanyAsync(db, tenantId, "SA", default)).Should().BeFalse();
        await db.SaveChangesAsync();

        (await db.PayslipTemplates.IgnoreQueryFilters().AnyAsync(t => t.TenantId == tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task ANewTenantsFirstSaudiCompany_StillGetsTheBilingualDefault()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        (await PayslipTemplateDefaults.EnsureSaudiBilingualDefaultForNewCompanyAsync(db, tenantId, "SA", default)).Should().BeTrue();
        await db.SaveChangesAsync();

        LocaleOf(await db.PayslipTemplates.IgnoreQueryFilters().SingleAsync(t => t.TenantId == tenantId)).Should().Be("bilingual");
    }
}
