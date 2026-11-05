using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>The three company payroll rules behind the reconciliation: the variance threshold, its minimum amount, and whether a sign-off is required.</summary>
public class ReconCompanyRulesTests
{
    [Theory]
    [InlineData("recon_variance_pct", "0.1", true)]
    [InlineData("recon_variance_pct", "5", true)]
    [InlineData("recon_variance_pct", "100", true)]
    [InlineData("recon_variance_pct", "0.09", false)]
    [InlineData("recon_variance_pct", "100.5", false)]
    [InlineData("recon_variance_pct", "five", false)]
    [InlineData("recon_variance_min_amount", "0", true)]
    [InlineData("recon_variance_min_amount", "2500.50", true)]
    [InlineData("recon_variance_min_amount", "-1", false)]
    [InlineData("recon_variance_min_amount", "lots", false)]
    [InlineData("recon_signoff_required", "true", true)]
    [InlineData("recon_signoff_required", "false", true)]
    [InlineData("recon_signoff_required", "maybe", false)]
    public void TheValues_AreValidatedAgainstTheirLegalRange(string key, string value, bool legal) =>
        (CompanyPayrollRules.Evaluate(key, value).Error is null).Should().Be(legal);

    [Fact]
    public void TheThreeKeys_AreListedWithTheOtherRules() =>
        PayrollRuleKeys.All.Should().Contain(new[] { "recon_variance_pct", "recon_variance_min_amount", "recon_signoff_required" });

    [Fact]
    public void RequiringASignoff_IsNotAWarning_ButTurningItOffIs()
    {
        CompanyPayrollRules.Evaluate("recon_signoff_required", "true").Warning.Should().BeNull();
        var off = CompanyPayrollRules.Evaluate("recon_signoff_required", "false").Warning;
        off.Should().NotBeNull();
        off!.Code.Should().Be(CompanyPayrollRules.WarnReconSignoffOff);
    }

    [Fact]
    public void TurningTheSignoffOff_CannotLowerAnyonesPay() =>
        CompanyPayrollRules.LowersPay("recon_signoff_required", "true", "false").Should().BeFalse();

    [Theory]
    [InlineData("recon_variance_pct", "5")]
    [InlineData("recon_variance_pct", "50")]
    [InlineData("recon_variance_pct", "0.1")]
    [InlineData("recon_variance_min_amount", "0")]
    [InlineData("recon_variance_min_amount", "1000")]
    public void SettingEitherThreshold_RaisesTheThresholdWarning_WhateverTheValue(string key, string value)
    {
        var warning = CompanyPayrollRules.Evaluate(key, value).Warning;
        warning.Should().NotBeNull("any change to when a movement needs an explanation needs a second person and an acknowledgement");
        warning!.Code.Should().Be(CompanyPayrollRules.WarnReconThresholdChanged);
        warning.Key.Should().Be(key);
    }

    [Theory]
    [InlineData("recon_variance_pct")]
    [InlineData("recon_variance_min_amount")]
    public void ClearingEitherThresholdBackToTheDefault_RaisesTheSameWarning(string key)
    {
        var (errors, warnings) = CompanyPayrollRules.Validate(new[] { new PayrollRuleChange(key, null) });
        errors.Should().BeEmpty();
        warnings.Should().ContainSingle(w => w.Code == CompanyPayrollRules.WarnReconThresholdChanged);
        CompanyPayrollRules.WarningCodesFor(new[] { new PayrollRuleChange(key, null) }).Should().Equal($"{key}:RECON_THRESHOLD_CHANGED");
    }

    private static (ZayraDbContext Db, Guid Tenant, Guid Company) NewDb() =>
        (new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task TurningTheSignoffOff_IsRefusedUntilItIsAcknowledged_ThenSaved_AndTheGateSeesIt()
    {
        var (db, tenant, company) = NewDb();
        var off = new[] { new PayrollRuleChange("recon_signoff_required", "false") };

        var refused = await CompanyPayrollRules.SaveAsync(db, tenant, company, Guid.NewGuid(), off, false, null, DateTime.UtcNow, default);
        refused.Saved.Should().BeFalse();
        refused.UnacknowledgedWarnings.Should().ContainSingle(w => w.Code == "RECON_SIGNOFF_OFF");
        (await CompanyPayrollRules.ResolveReconAsync(db, tenant, company, default)).SignoffRequired.Should().BeTrue();

        var saved = await CompanyPayrollRules.SaveAsync(db, tenant, company, Guid.NewGuid(), off, true,
            "Pilot month: finance reviews outside the system.", DateTime.UtcNow, default,
            acknowledgedWarnings: CompanyPayrollRules.WarningCodesFor(off).ToList());
        saved.Saved.Should().BeTrue(string.Join("; ", saved.Errors));
        (await CompanyPayrollRules.ResolveReconAsync(db, tenant, company, default)).SignoffRequired.Should().BeFalse();
    }

    [Fact]
    public async Task TheThresholds_AreSaved_AndReachTheReconciliationAsTheCompanysOwn()
    {
        var (db, tenant, company) = NewDb();
        var changes = new[] { new PayrollRuleChange("recon_variance_pct", "7.5"), new PayrollRuleChange("recon_variance_min_amount", "250") };
        var result = await CompanyPayrollRules.SaveAsync(db, tenant, company, Guid.NewGuid(), changes, true, "Agreed with finance", DateTime.UtcNow, default,
            acknowledgedWarnings: CompanyPayrollRules.WarningCodesFor(changes).ToList());
        result.Saved.Should().BeTrue(string.Join("; ", result.Errors));

        var values = await CompanyPayrollRules.ResolveReconAsync(db, tenant, company, default);
        ReconThresholds.FromRules(values.VariancePct, values.VarianceMinAmount).Should().Be(new ReconThresholds(7.5m, 250m, "company"));
        values.SignoffRequired.Should().BeTrue("the requirement stays on unless a company switches it off");
    }

    [Fact]
    public async Task ACompanyThatSetNothing_GetsTheDefaults_AndANullCompanyGetsThemToo()
    {
        var (db, tenant, company) = NewDb();
        var none = await CompanyPayrollRules.ResolveReconAsync(db, tenant, company, default);
        (none.VariancePct, none.VarianceMinAmount, none.SignoffRequired).Should().Be(((string?)null, (string?)null, true));
        (await CompanyPayrollRules.ResolveReconAsync(db, tenant, null, default)).SignoffRequired.Should().BeTrue();
    }

    [Fact]
    public async Task ARuleFutureDatedForTomorrow_DoesNotApplyToday()
    {
        var (db, tenant, company) = NewDb();
        var tomorrow = CompanyPayrollRules.Today().AddDays(1);
        var change = new[] { new PayrollRuleChange("recon_variance_pct", "20") };
        var result = await CompanyPayrollRules.SaveAsync(db, tenant, company, Guid.NewGuid(),
            change, true, "Agreed with finance", DateTime.UtcNow, default, effectiveFrom: tomorrow,
            acknowledgedWarnings: CompanyPayrollRules.WarningCodesFor(change).ToList());
        result.Saved.Should().BeTrue();
        (await CompanyPayrollRules.ResolveReconAsync(db, tenant, company, default)).VariancePct.Should().BeNull();
        (await CompanyPayrollRules.ResolveReconAsync(db, tenant, company, default, tomorrow)).VariancePct.Should().Be("20");
    }
}
