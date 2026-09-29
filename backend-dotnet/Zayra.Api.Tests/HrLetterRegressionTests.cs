using UglyToad.PdfPig;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

/// <summary>
/// Characterisation tests for the two B6 defects that CAN be expressed against the pre-fix code.
/// (The rest of B6 — templates, the register, the ESS path — did not exist, so there was nothing
/// to characterise; those live in <see cref="HrLetterIssuanceTests"/>.)
/// </summary>
public class HrLetterRegressionTests
{
    /// <summary>
    /// The Arabic-font defect, stated as the container states it.
    ///
    /// <para>The payslip has carried Arabic labels — المستحقات, صافي الراتب — since it was
    /// written, and nothing in the repo ever called <c>FontManager.RegisterFont</c>. On a
    /// developer Mac that was invisible, because QuestPDF silently borrows a host font and macOS
    /// ships Geeza Pro. The production image ships none, so those labels rendered as □□□□.</para>
    ///
    /// <para>This test models the container by refusing host fonts — which is also the production
    /// setting <see cref="DocumentFonts"/> now applies — and asks only that Arabic glyphs were
    /// drawn at all. Before the fix that count is zero on every platform. It says nothing about
    /// WHICH font supplies them, so it stays honest if the font is ever swapped.</para>
    /// </summary>
    [Fact]
    public async Task ArabicPayslip_DrawsArabicGlyphs_WithoutBorrowingAHostFont()
    {
        DocumentFonts.EnsureRegistered();
        Assert.False(QuestPDF.Settings.UseEnvironmentFonts,
            "Host-font fallback must stay off, or a developer laptop will keep hiding missing glyphs from the container.");

        var pdf = await new LetterService().GeneratePayslipPdfAsync(ArabicPayslip());

        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        var arabicGlyphs = page.Letters.Count(letter => letter.Value.Length > 0 && IsArabic(letter.Value[0]));

        Assert.True(arabicGlyphs > 10,
            $"An Arabic-locale payslip drew {arabicGlyphs} Arabic glyphs. Before the embedded font was " +
            "registered this was 0 and the page showed tofu boxes where the labels belong.");
    }

    /// <summary>
    /// The reference-number defect, preserved as evidence.
    ///
    /// <para>The pre-existing experience certificate computes its reference inline as
    /// <c>EXP-{EmployeeCode}-{yyyyMM}</c> and stores it nowhere. Two certificates for the same
    /// person in the same month therefore carry the identical reference, and neither exists in
    /// any table, so a caller quoting one cannot be answered.</para>
    ///
    /// <para>This test asserts the collision, deliberately: it documents what was true and it
    /// stays green. The fix is not to patch this string — it is that the API path no longer goes
    /// here. <c>GET /api/employees/{id}/letters/experience</c> now issues through
    /// <see cref="IHrLetterIssuer"/>, which allocates a stored, unique reference; that behaviour
    /// is asserted by
    /// <c>HrLetterIssuanceTests.TwoLettersOfTheSameTypeInTheSameMonth_GetDifferentReferenceNumbers</c>.</para>
    /// </summary>
    [Fact]
    public async Task TheSupersededExperienceLetter_StillCollides_WhichIsWhyNothingCallsItAnyMore()
    {
        var service = new LetterService();
        var data = new LetterData(
            EmployeeName: "Mohammed Abdullah Al-Harbi",
            EmployeeCode: "EMP-0001",
            Department: "Operations",
            Designation: "Site Supervisor",
            JoiningDate: new DateTime(2021, 3, 14, 0, 0, 0, DateTimeKind.Utc),
            LeavingDate: new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            BasicSalary: 10_000m,
            Currency: "SAR",
            CompanyName: "Najd Industrial Services Company",
            IssuedBy: "HR Department",
            IssuedDate: DateTime.UtcNow);

        var first = ExtractReference(await service.GenerateExperienceLetterAsync(data));
        var second = ExtractReference(await service.GenerateExperienceLetterAsync(data));

        Assert.Equal(first, second);
        Assert.Equal($"Ref:EXP-EMP-0001-{DateTime.UtcNow:yyyyMM}", first);
    }

    private static string ExtractReference(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var text = System.Text.RegularExpressions.Regex.Replace(
            document.GetPage(1).Text.Replace("\0", string.Empty), @"\s+", string.Empty);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"Ref:EXP-[A-Za-z0-9\-]*?-\d{6}(?![0-9])");
        Assert.True(match.Success, $"no reference found in: {text}");
        return match.Value;
    }

    private static bool IsArabic(char c) =>
        (c >= '؀' && c <= 'ۿ')      // Arabic block
        || (c >= 'ﭐ' && c <= '﻿');  // Arabic presentation forms — what a shaper emits

    private static PayslipData ArabicPayslip() => new(
        PayslipNumber: "PS-2026-09-0001",
        EmployeeCode: "EMP-0001",
        EmployeeName: "Mohammed Abdullah Al-Harbi",
        Department: "Operations",
        Designation: "Site Supervisor",
        PayYear: 2026,
        PayMonth: 9,
        Currency: "SAR",
        Items:
        [
            new PayslipLineItem("Basic", 10_000m, "Earning"),
            new PayslipLineItem("Housing", 2_500m, "Earning"),
            new PayslipLineItem("GOSI", 1_125m, "Deduction"),
            new PayslipLineItem("Net", 11_375m, "Net"),
        ],
        CompanyName: "Najd Industrial Services Company",
        CompanyNameAr: "شركة نجد للخدمات الصناعية",
        GeneratedOn: new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
        Branding: new PayslipBrandingConfig(Locale: "ar"));
}
