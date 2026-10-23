using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers.Localization;
using Zayra.Api.Infrastructure.Localization;

namespace Zayra.Api.Tests;

/// <summary>
/// The create-company wizard's "Suggest Arabic" for organisation labels: curated Modern Standard Arabic wording or
/// nothing. Never a letter-by-letter guess, never a partial label.
/// </summary>
public class OrganizationLabelArabicTests
{
    private readonly TransliterationService _service = new();

    [Theory]
    // {Function} Cost Center / Centre → مركز تكلفة {function}; the country pack names its cost centres this way.
    [InlineData("Human Resources Cost Center", "مركز تكلفة الموارد البشرية")]
    [InlineData("HR Cost Centre", "مركز تكلفة الموارد البشرية")]
    [InlineData("Finance Cost Center", "مركز تكلفة المالية")]
    [InlineData("Information Technology Cost Center", "مركز تكلفة تقنية المعلومات")]
    [InlineData("Administration Cost Center", "مركز تكلفة الشؤون الإدارية")]
    [InlineData("Riyadh Cost Centre", "مركز تكلفة الرياض")]
    // {Function} Department → إدارة {function}, with the idiomatic exceptions.
    [InlineData("Human Resources Department", "إدارة الموارد البشرية")]
    [InlineData("Sales Department", "إدارة المبيعات")]
    [InlineData("Operations Department", "إدارة العمليات")]
    [InlineData("Department of Engineering", "إدارة الهندسة")]
    [InlineData("Finance Department", "الإدارة المالية")]
    [InlineData("Admin Department", "إدارة الشؤون الإدارية")]
    [InlineData("Quality Section", "قسم الجودة")]
    // {Place} Branch / Office / Head Office, including the new cities and districts.
    [InlineData("Riyadh Branch", "فرع الرياض")]
    [InlineData("Sulaymaniyah Branch", "فرع السليمانية")]
    [InlineData("Al Olaya Branch", "فرع العليا")]
    [InlineData("Al-Olaya Office", "مكتب العليا")]
    [InlineData("Olaya", "العليا")]
    [InlineData("Dammam Head Office", "المكتب الرئيسي في الدمام")]
    [InlineData("Doha Office", "مكتب الدوحة")]
    [InlineData("Muscat Branch", "فرع مسقط")]
    [InlineData("Manama Office", "مكتب المنامة")]
    [InlineData("Kuwait City Branch", "فرع مدينة الكويت")]
    [InlineData("Ras Al Khaimah Warehouse", "مستودع رأس الخيمة")]
    [InlineData("Jubail Site", "موقع الجبيل")]
    [InlineData("Riyadh", "الرياض")]
    [InlineData("Head Office", "المكتب الرئيسي")]
    [InlineData("Main gate", "البوابة الرئيسية")]
    [InlineData("Gate 2", "البوابة 2")]
    [InlineData("Riyadh Sales", "مبيعات الرياض")]
    // Job titles: the country pack's defaults, then common GCC titles and patterns.
    [InlineData("Accountant", "محاسب")]
    [InlineData("Chief Executive Officer", "الرئيس التنفيذي")]
    [InlineData("Software Engineer", "مهندس برمجيات")]
    [InlineData("HR Manager", "مدير الموارد البشرية")]
    [InlineData("HR Officer", "أخصائي موارد بشرية")]
    [InlineData("IT Engineer", "مهندس تقنية المعلومات")]
    [InlineData("Driver", "سائق")]
    [InlineData("Sales Executive", "مسؤول مبيعات")]
    [InlineData("Admin Assistant", "مساعد إداري")]
    [InlineData("Receptionist", "موظف استقبال")]
    [InlineData("Cashier", "أمين صندوق")]
    [InlineData("Storekeeper", "أمين مستودع")]
    [InlineData("Technician", "فني")]
    [InlineData("Supervisor", "مشرف")]
    [InlineData("Sales Manager", "مدير المبيعات")]
    [InlineData("Manager of Operations", "مدير العمليات")]
    [InlineData("Director of Human Resources", "مدير إدارة الموارد البشرية")]
    [InlineData("Finance Director", "مدير الإدارة المالية")]
    [InlineData("Operations Supervisor", "مشرف العمليات")]
    [InlineData("Assistant HR Manager", "مساعد مدير الموارد البشرية")]
    [InlineData("Payroll Specialist", "أخصائي رواتب")]
    [InlineData("Senior Accountant", "محاسب أول")]
    [InlineData("Senior Software Engineer", "مهندس برمجيات أول")]
    [InlineData("Junior Accountant", "محاسب مبتدئ")]
    [InlineData("Senior Manager", "مدير أول")]
    [InlineData("Government Relations Officer", "مسؤول العلاقات الحكومية")]
    // Grades: "Grade {n}" and the country-pack ladder's known tiers.
    [InlineData("Grade 1", "الدرجة 1")]
    [InlineData("Grade 12", "الدرجة 12")]
    [InlineData("Grade 2 - Junior", "الدرجة 2 - مبتدئ")]
    [InlineData("Grade 5 - Executive", "الدرجة 5 - تنفيذي")]
    [InlineData("Grade 7 - Senior Executive", "الدرجة 7 - تنفيذي أول")]
    [InlineData("Level 3", "المستوى 3")]
    [InlineData("Professional", "مهني")]
    [InlineData("  sales   department ", "إدارة المبيعات")]
    public void CuratedPatternsGiveStandardBusinessArabic(string source, string expected)
    {
        _service.ToArabicOrganizationLabel(source).Should().Be(expected);
        _service.ToArabicOrganizationLabel(source).Should().Be(expected, "the offline suggestion is deterministic");
    }

    [Theory]
    // Unknown wording: no phonetic guess and no partial label.
    [InlineData("Western Region Office")]
    [InlineData("Jeddah Downtown")]
    [InlineData("Widget Department")]
    [InlineData("Quantum Cost Center")]
    [InlineData("Chief Happiness Officer")]
    [InlineData("Finance Officer")]
    [InlineData("Senior HR Manager")]
    [InlineData("Senior Chief Executive Officer")]
    [InlineData("Grade 1 - Entry")]
    [InlineData("Gate 2 - North")]
    [InlineData("Riyadh Finance")]
    [InlineData("Jeddah Office 2")]
    [InlineData("Evostel")]
    [InlineData("HR & Admin")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownWordingGivesNoSuggestion(string source)
    {
        _service.ToArabicOrganizationLabel(source).Should().BeEmpty();
    }

    [Fact]
    public void ALabelOverTwoHundredCharactersGivesNoSuggestion()
    {
        _service.ToArabicOrganizationLabel(new string('a', 190) + " Department").Should().BeEmpty();
    }

    [Fact]
    public void NoSuggestionContainsLatinLettersOrALetterByLetterReading()
    {
        foreach (var source in new[] { "HR Manager", "Grade 3", "Sulaymaniyah Branch", "IT Engineer", "Doha Office" })
            _service.ToArabicOrganizationLabel(source).Should().NotMatchRegex("[A-Za-z]", source);
        // The phonetic generic mode would read "Olaya" letter by letter; the organisation mode never does.
        _service.ToArabicOrganizationLabel("Olaya").Should().NotBe(_service.ToArabic("Olaya"));
    }

    [Fact]
    public void BatchAnswersEveryLabelInOrderWithNoSuggestionMarked()
    {
        var controller = Controller();
        var result = controller.TransliterateBatch(new TransliterateBatchRequest(["Riyadh Branch", "Western Region Office", null, "HR Manager"], "ar", "organization-label"))
            .Should().BeOfType<OkObjectResult>().Subject;
        var items = JsonSerializer.SerializeToElement(result.Value).GetProperty("suggestions").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("suggestion").GetString()).Should().Equal("فرع الرياض", "", "", "مدير الموارد البشرية");
        items.Select(i => i.GetProperty("requiresManualEntry").GetBoolean()).Should().Equal(false, true, true, false);
        items[0].GetProperty("text").GetString().Should().Be("Riyadh Branch");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("generic")]
    [InlineData("person-name")]
    public void BatchIsOnlyForOrganisationLabels(string? kind)
    {
        Controller().TransliterateBatch(new TransliterateBatchRequest(["Muhammad Umar"], "ar", kind))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void BatchIsBoundedAndTenantScoped()
    {
        var many = Enumerable.Range(0, TransliterationController.MaxBatch + 1).Select(i => (string?)$"Grade {i}").ToList();
        Controller().TransliterateBatch(new TransliterateBatchRequest(many, "ar", "organization-label")).Should().BeOfType<BadRequestObjectResult>();
        Controller(withTenant: false).TransliterateBatch(new TransliterateBatchRequest(["Riyadh Branch"], "ar", "organization-label"))
            .Should().BeOfType<UnauthorizedResult>();
    }

    private static TransliterationController Controller(bool withTenant = true)
    {
        var http = new DefaultHttpContext();
        if (withTenant)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", Guid.NewGuid().ToString())], "Test"));
        return new TransliterationController(new TransliterationService()) { ControllerContext = new ControllerContext { HttpContext = http } };
    }
}
