using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

/// <summary>
/// Shared factory for a <see cref="GosiReconciliationService"/> wired with a KSA pack resolver, for
/// tests that construct GosiController / SaudiComplianceDashboardService directly. Mirrors production:
/// "SAU"/"SA" resolve the real <see cref="KsaDeductionCalculator"/> (default GOSI rates via
/// <see cref="StubRuleReader"/>), everything else falls back to the default (zero) calculator.
/// </summary>
internal static class TestReconciliation
{
    public static StubRuleReader KsaRuleReader() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m);

    public static GosiReconciliationService For(ZayraDbContext db) =>
        new(db, new KsaTestPackResolver(KsaRuleReader()));
}

/// <summary>Reusable KSA-only pack resolver for tests (not file-scoped, unlike the regression stub).</summary>
internal sealed class KsaTestPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public KsaTestPackResolver(IStatutoryRuleReader r) => _r = r;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA"
            ? new KsaDeductionCalculator(_r)
            : new DefaultStatutoryDeductionCalculator();

    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new DefaultEndOfServiceCalculator();

    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new DefaultWageProtectionExporter();

    public INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new DefaultNationalizationTracker();

    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new DefaultLocalizationProfile();

    public ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new DefaultCountryPackDescriptor();
}
