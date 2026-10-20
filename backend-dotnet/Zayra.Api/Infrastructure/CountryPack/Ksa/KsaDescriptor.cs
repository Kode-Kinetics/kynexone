using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

public sealed class KsaDescriptor : ICountryPackDescriptor
{
    public PackDescriptor GetDescriptor() => new(
        CountryCode:              CountryCodes.Saudi,
        CountryNameEn:            "Saudi Arabia",
        CountryNameAr:            "المملكة العربية السعودية",
        SocialInsuranceScheme:    "GOSI",
        SocialInsuranceDescription:
            "General Organization for Social Insurance — Saudi nationals on covered wage (basic+housing, ≤ SAR 45,000): " +
            "existing subscribers (first registered before 3 July 2024): Annuities (9% EE + 9% ER) + SANED (0.75% each) + OH (2% ER); total EE 9.75%, ER 11.75%. " +
            "New entrants (first registered on or after 3 July 2024, New Social Insurance Law, Royal Decree M/273): Annuities start at 9% each side and rise " +
            "half a point every July to 11% each side from July 2028, plus the same SANED and OH; the cohort is set by the GOSI first-registration date. " +
            "Expatriates: Occupational Hazard only (2% employer).",
        EosbFormula:
            "KSA Labor Law Art. 84 — ½ month's last wage per year (yrs 1–5), 1 full month's last wage per year (after 5 yrs), pro-rated by days. " +
            "Last wage = basic + housing, plus transport by default; other allowances (food, mobile, other) are excluded unless configured. " +
            "Resignation (Art. 85) pays: nil (<2 yrs), ⅓ (2–5 yrs), ⅔ (5–10 yrs), full (>10 yrs).",
        // The KSA artefact this product generates is an internal register, not a WPS file: Mudad accepts
        // only the bank-signed file. The Saudi bank file is the ANB Connect payroll instruction.
        WpsFormat:     Zayra.Api.Infrastructure.Payroll.WpsConformance.KsaPayrollRegisterFormat,
        WpsFormatLabel: Zayra.Api.Infrastructure.Payroll.WpsConformance.KsaPayrollRegisterLabel
            + ". Saudi bank file: ANB Connect payroll instruction with WPS fields.",
        NationalizationScheme: "Nitaqat");
}
