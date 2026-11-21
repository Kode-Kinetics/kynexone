using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The default Saudi offer letter used to say "a probation period of 90 days" whatever the offer recorded. The letter now states the
/// offer's own probation, in English and in Arabic, and the department it names is the department's name, not its code. A letter
/// that is generated again (a draft saved, a revision created) picks the fix up; an original that was already frozen is never touched.
/// No database server: pure rules, the in-memory provider and PdfPig.
/// </summary>
public sealed class OfferLetterProbationFixTests
{
    private static readonly DateOnly Start = new(2027, 2, 1);

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"offer-probation-fix-{Guid.NewGuid():N}").Options);

    private sealed record Letter(OfferDesignSnapshot Snapshot, string Text, string Clause);

    private static async Task<Letter> LetterAsync(string country, int months, bool arabic, OfferDesign? design = null, string department = "Operations",
        Action<ZayraDbContext, Guid, Guid>? seed = null)
    {
        await using var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, CountryCode = country, LegalNameEn = "Test Company" };
        db.Companies.Add(company);
        seed?.Invoke(db, tenant, company.Id);
        await db.SaveChangesAsync();
        var offer = new OfferLetter
        {
            TenantId = tenant, CompanyId = company.Id, ApplicationId = Guid.NewGuid(), CandidateName = "Test Candidate", OfferedJobTitle = "Analyst",
            OfferedDepartment = department, StartDate = Start, ProbationMonths = months, BasicSalary = 10_000, GrossSalary = 10_000,
        };
        var snapshot = await OfferLetterDesign.SnapshotAsync(db, offer, design ?? OfferLetterDesign.KsaDefault(arabic), null, null, CancellationToken.None);
        var raw = string.Join("\n", snapshot.Design.Blocks.Where(b => b.Enabled).SelectMany(b => new[] { b.Text }.Concat(b.Rows.Select(r => r.Label + " " + r.Value))));
        return new(snapshot, OfferLetterDesign.Merge(raw, snapshot.Values), snapshot.Values["probation_clause"]);
    }

    private static string PdfText(OfferDesignSnapshot snapshot)
    {
        snapshot.Design.Header = ""; snapshot.Design.Footer = "";
        using var document = PdfDocument.Open(OfferLetterDesign.Render(snapshot));
        return Regex.Replace(string.Join(" ", document.GetPages().Select(p => p.Text)), @"\s+", " ");
    }

    // ── The English letter states the recorded probation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoProbation_says_the_employee_will_not_serve_one_and_drops_the_extension_and_Eid_sentences()
    {
        var letter = await LetterAsync("SA", 0, arabic: false);
        letter.Text.Should().Contain("You will not serve a probation period. Your employment is confirmed from the start date.");
        letter.Text.Should().NotContain("90 days").And.NotContain("may be extended").And.NotContain("Eid al-Fitr");
    }

    [Theory]
    [InlineData(1, "probation period of 1 month from the start date")]
    [InlineData(3, "probation period of 3 months from the start date")]
    public async Task One_to_five_months_state_the_recorded_period_with_the_extension_capped_at_180_days(int months, string expected)
    {
        var letter = await LetterAsync("SA", months, arabic: false);
        letter.Text.Should().Contain(expected).And.Contain("may be extended by written agreement").And.Contain("never exceeds 180 days");
        letter.Text.Should().NotContain("90 days");
    }

    [Fact]
    public async Task Six_months_in_a_Saudi_company_is_the_legal_maximum_and_cannot_be_extended()
    {
        var letter = await LetterAsync("SA", 6, arabic: false);
        letter.Text.Should().Contain("probation period of 180 days from the start date")
            .And.Contain("the maximum the Labor Law allows and cannot be extended");
        letter.Text.Should().NotContain("90 days").And.NotContain("may be extended");
    }

    [Fact]
    public async Task A_twelve_month_probation_outside_Saudi_Arabia_is_stated_as_recorded_without_the_Saudi_ceiling()
    {
        var letter = await LetterAsync("AE", 12, arabic: false);
        letter.Text.Should().Contain("probation period of 12 months from the start date")
            .And.Contain("within the maximum permitted by the labour law applying to your employment");
        letter.Text.Should().NotContain("90 days").And.NotContain("180 days");
    }

    [Theory]
    [InlineData("SA", 0, "will not serve a probation period")]
    [InlineData("SA", 1, "probation period of 1 month")]
    [InlineData("SA", 6, "probation period of 180 days")]
    [InlineData("AE", 12, "probation period of 12 months")]
    public async Task The_rendered_english_pdf_carries_the_recorded_probation(string country, int months, string expected)
    {
        var letter = await LetterAsync("" + country, months, arabic: false);
        var text = PdfText(letter.Snapshot);
        text.Should().Contain(expected);
        text.Should().NotContain("90 days");
    }

    // ── The Arabic letter states it in Arabic ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "لا تخضعون لفترة تجربة")]
    [InlineData(1, "تخضعون لفترة تجربة مدتها شهر واحد من تاريخ المباشرة")]
    [InlineData(2, "تخضعون لفترة تجربة مدتها شهران من تاريخ المباشرة")]
    [InlineData(3, "تخضعون لفترة تجربة مدتها (3) أشهر من تاريخ المباشرة")]
    public async Task The_arabic_letter_states_the_recorded_probation_in_arabic_forms(int months, string expected)
    {
        var letter = await LetterAsync("SA", months, arabic: true);
        letter.Text.Should().Contain(expected);
        letter.Text.Should().NotContain("(90)");
        Regex.IsMatch(letter.Clause, "[A-Za-z]").Should().BeFalse("no English text may appear inside the Arabic probation clause");
        if (months > 0) letter.Text.Should().Contain("ويجوز تمديدها باتفاق خطي").And.Contain("(180) يومًا");
        else letter.Text.Should().NotContain("عيدي الفطر");
    }

    [Fact]
    public async Task The_arabic_letter_for_six_months_says_180_days_is_the_maximum_and_cannot_be_extended()
    {
        var letter = await LetterAsync("SA", 6, arabic: true);
        letter.Text.Should().Contain("تخضعون لفترة تجربة مدتها (180) يومًا من تاريخ المباشرة").And.Contain("ولا يجوز تمديدها");
        letter.Text.Should().NotContain("(90)").And.NotContain("ويجوز تمديدها");
        Regex.IsMatch(letter.Clause, "[A-Za-z]").Should().BeFalse();
    }

    [Fact]
    public async Task The_arabic_letter_uses_the_correct_plural_for_eleven_months_outside_Saudi_Arabia()
    {
        var letter = await LetterAsync("AE", 11, arabic: true);
        letter.Text.Should().Contain("مدتها (11) شهرًا");
        Regex.IsMatch(letter.Clause, "[A-Za-z]").Should().BeFalse();
    }

    // ── What the templates carry, and a design saved before the fix ───────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_default_saudi_templates_no_longer_hard_code_a_ninety_day_probation(bool arabic)
    {
        var text = string.Join("\n", OfferLetterDesign.KsaDefault(arabic).Blocks.Select(b => b.Text));
        text.Should().Contain("{{probation_clause}}").And.NotContain("90 days").And.NotContain("(90)");
        OfferLetterDesign.Validate(OfferLetterDesign.KsaDefault(arabic));
        OfferLetterDesign.Tokens.Should().Contain("probation_clause");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_design_saved_with_the_old_fixed_sentence_prints_the_recorded_probation_when_it_is_generated_again(bool arabic)
    {
        var design = OfferLetterDesign.KsaDefault(arabic);
        var block = design.Blocks.Single(b => b.Text == "{{probation_clause}}");
        block.Text = arabic ? OfferLetterClauses.LegacyProbationAr : OfferLetterClauses.LegacyProbationEn;
        var letter = await LetterAsync("SA", 6, arabic, design);
        letter.Text.Should().NotContain("90 days").And.NotContain("(90)");
        letter.Text.Should().Contain(arabic ? "(180) يومًا" : "180 days");
    }

    // ── The department is the name, not the code ──────────────────────────────────────────────────────────────────────

    private static void SeedDepartment(ZayraDbContext db, Guid tenant, Guid company, string code = "ALM-DAIRY-KSA-OPS", string name = "Operations", string nameAr = "العمليات") =>
        db.Departments.Add(new Department { TenantId = tenant, CompanyId = company, Code = code, NameEn = name, NameAr = nameAr, IsActive = true });

    [Fact]
    public async Task The_letter_prints_the_department_name_when_the_offer_holds_its_code()
    {
        var letter = await LetterAsync("SA", 3, arabic: false, department: "ALM-DAIRY-KSA-OPS", seed: (db, t, c) => SeedDepartment(db, t, c));
        letter.Snapshot.Values["department"].Should().Be("Operations");
        letter.Text.Should().Contain("Department: Operations.").And.NotContain("ALM-DAIRY-KSA-OPS");
    }

    [Fact]
    public async Task The_arabic_letter_prints_the_arabic_department_name()
    {
        var letter = await LetterAsync("SA", 3, arabic: true, department: "ALM-DAIRY-KSA-OPS", seed: (db, t, c) => SeedDepartment(db, t, c));
        letter.Snapshot.Values["department"].Should().Be("العمليات");
    }

    [Fact]
    public async Task A_stored_name_stays_the_name_and_an_unknown_code_falls_back_to_the_code()
    {
        (await LetterAsync("SA", 3, false, department: "Operations", seed: (db, t, c) => SeedDepartment(db, t, c))).Snapshot.Values["department"].Should().Be("Operations");
        (await LetterAsync("SA", 3, false, department: "NO-SUCH-CODE", seed: (db, t, c) => SeedDepartment(db, t, c))).Snapshot.Values["department"].Should().Be("NO-SUCH-CODE");
        (await LetterAsync("SA", 3, false, department: "", seed: (db, t, c) => SeedDepartment(db, t, c))).Snapshot.Values["department"].Should().BeEmpty();
    }

    [Fact]
    public async Task When_two_companies_share_a_code_the_offers_own_company_department_name_is_used()
    {
        var letter = await LetterAsync("SA", 3, false, department: "OPS", seed: (db, t, c) =>
        {
            SeedDepartment(db, t, Guid.NewGuid(), code: "OPS", name: "Other company operations");
            SeedDepartment(db, t, c, code: "OPS", name: "Operations");
        });
        letter.Snapshot.Values["department"].Should().Be("Operations");
    }

    [Fact]
    public async Task The_placement_helper_returns_the_name_for_a_code_and_never_changes_what_is_stored()
    {
        await using var db = NewDb();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        SeedDepartment(db, tenant, company); await db.SaveChangesAsync();
        (await OfferPlacement.DisplayDepartmentAsync(db, tenant, company, "ALM-DAIRY-KSA-OPS", arabic: false, default)).Should().Be("Operations");
        (await OfferPlacement.DisplayDepartmentAsync(db, tenant, company, "alm-dairy-ksa-ops", arabic: false, default)).Should().Be("Operations");
        (await OfferPlacement.DisplayDepartmentAsync(db, tenant, company, "Operations", arabic: false, default)).Should().Be("Operations");
        (await OfferPlacement.DisplayDepartmentAsync(db, tenant, Guid.NewGuid(), "ALM-DAIRY-KSA-OPS", arabic: false, default)).Should().Be("Operations",
            "a code held by one department only is unambiguous whichever company asks");
        (await OfferPlacement.DisplayDepartmentAsync(db, Guid.NewGuid(), company, "ALM-DAIRY-KSA-OPS", arabic: false, default)).Should().Be("ALM-DAIRY-KSA-OPS",
            "another tenant's departments are never read");
    }

    // ── The legacy HTML stored with every offer states the recorded terms ─────────────────────────────────────────────

    [Fact]
    public void The_stored_html_letter_names_the_recorded_contract_type_probation_and_deadline()
    {
        using var db = NewDb();
        var html = new RecruitmentService(db).GenerateOfferLetterHtml(new("Test Candidate", "Analyst", "Operations", Start, 10_000, 0, 0, 0, 10_000, 6, "SAR",
            ContractType: "Fixed-term", ContractTermMonths: 12, CompanyCountryCode: "SA", ResponseDeadlineUtc: new DateTime(2027, 1, 20, 0, 0, 0, DateTimeKind.Utc)));
        html.Should().Contain("Full-Time, Fixed-term (12 months)").And.NotContain("Full-Time, Permanent");
        html.Should().Contain("180 days (ending 31 July 2027)");
        html.Should().Contain("valid until <strong>20 January 2027</strong>").And.NotContain("valid for <strong>7 days</strong>");
    }

    [Fact]
    public void The_stored_html_letter_still_reads_permanent_and_seven_days_when_nothing_else_was_recorded()
    {
        using var db = NewDb();
        var html = new RecruitmentService(db).GenerateOfferLetterHtml(new("Test Candidate", "Analyst", "Operations", Start, 10_000, 0, 0, 0, 10_000, 3, "SAR"));
        html.Should().Contain("Full-Time, Permanent").And.Contain("3 months (ending 01 May 2027)").And.Contain("valid for <strong>7 days</strong>");
    }

    [Fact]
    public void The_stored_html_letter_says_no_probation_when_there_is_none()
    {
        using var db = NewDb();
        var html = new RecruitmentService(db).GenerateOfferLetterHtml(new("Test Candidate", "Analyst", "Operations", Start, 10_000, 0, 0, 0, 10_000, 0, "SAR"));
        html.Should().Contain("<td>Probation Period</td><td>None</td>");
    }
}
