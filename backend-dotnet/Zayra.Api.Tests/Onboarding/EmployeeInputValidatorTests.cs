using FluentAssertions;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class EmployeeInputValidatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);

    internal static EmployeeCreateRequest Req(
        string name = "Fatima Al Harbi", string? nationality = "Saudi", DateOnly? dob = null, DateTime? joining = null,
        string? mobile = null, string? photo = null, EmployeeSalaryBreakdownRequest? salary = null,
        EmployeePayrollProfileRequest? payroll = null, IReadOnlyCollection<EmployeeComplianceRecordRequest>? records = null,
        string? workEmail = null, string? employeeCode = null, DateOnly? probStart = null, DateOnly? probEnd = null,
        DateOnly? confirmation = null, int? notice = null, string? nationalityCode = null,
        IReadOnlyCollection<AcknowledgedWarning>? ack = null) =>
        new(employeeCode, employeeCode is not null, name, null, null, "Female", dob, nationality, null, null, workEmail, mobile,
            photo, Guid.NewGuid(), null, null, null, null, null, null, null, null, "Full-Time", "Unlimited", joining,
            confirmation, probStart, probEnd, notice, null, null, null, null, null, payroll, salary, records,
            NationalityCode: nationalityCode, AcknowledgedWarnings: ack);

    private static EmployeeInputValidationContext Ctx(EmployeeGuardMode mode = EmployeeGuardMode.Create, string country = "SA") =>
        new(country, Today, mode, TimeZoneInfo.Utc);

    private static EmployeeInputValidationResult Run(EmployeeCreateRequest r, EmployeeGuardMode mode = EmployeeGuardMode.Create, string country = "SA") =>
        EmployeeInputValidator.Validate(r, Ctx(mode, country));

    private static DateTime Day(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    [Fact]
    public void InputValidator_EnglishName151_Is400()
    {
        Run(Req(name: new string('a', 151))).Errors["englishName"].Should().Contain(EmployeeFieldErrors.ValueTooLong);
        Run(Req(name: new string('a', 150))).Errors.Should().NotContainKey("englishName");
        Run(Req(name: "  ")).Errors["englishName"].Should().Contain(EmployeeFieldErrors.Required);
    }

    [Fact]
    public void InputValidator_FutureDob_Hard()
    {
        Run(Req(dob: Today.AddDays(1))).Errors["dateOfBirth"].Should().Contain(EmployeeFieldErrors.DobFuture);
        Run(Req(dob: Today.AddYears(-101))).Errors["dateOfBirth"].Should().Contain(EmployeeFieldErrors.DobOutOfRange);
        Run(Req(dob: Today.AddYears(-100))).Errors.Should().NotContainKey("dateOfBirth");
    }

    [Fact]
    public void InputValidator_Age14_Warning_NotHard()
    {
        var r = Run(Req(dob: Today.AddYears(-14), joining: Day(Today)));
        r.Errors.Should().BeEmpty();
        r.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.UnderMinimumAge);
        r.Warnings.Single(w => w.Code == EmployeeWarningCodes.UnderMinimumAge).RequiresPermission.Should().BeTrue();
    }

    [Fact]
    public void Age16_JuvenileWarning()
    {
        var r = Run(Req(dob: Today.AddYears(-16), joining: Day(Today)));
        r.Errors.Should().BeEmpty();
        r.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.Juvenile15To17);
        r.Warnings.Select(w => w.Code).Should().NotContain(EmployeeWarningCodes.UnderMinimumAge);
        Run(Req(dob: Today.AddYears(-71), joining: Day(Today))).Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.AgeOver70);
    }

    [Fact]
    public void MinimumAge_IsEvaluatedAtTheJoiningDate()
    {
        // 14 today, but joining is 2 years out -> 16 at joining: juvenile, not under-minimum.
        var r = Run(Req(dob: Today.AddYears(-14), joining: Day(Today.AddYears(2).AddDays(-1))));
        r.Warnings.Select(w => w.Code).Should().NotContain(EmployeeWarningCodes.UnderMinimumAge);
    }

    [Fact]
    public void Joining_BeforeDob_Hard_AndBeforeDobPlus15y_IsMinimumAgeWarning()
    {
        var dob = Today.AddYears(-30);
        Run(Req(dob: dob, joining: Day(dob.AddDays(-1)))).Errors["joiningDate"].Should().Contain(EmployeeFieldErrors.JoiningOutOfRange);
        var r = Run(Req(dob: dob, joining: Day(dob.AddYears(15).AddDays(-1))));
        r.Errors.Should().NotContainKey("joiningDate");
        r.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.UnderMinimumAge);
    }

    [Fact]
    public void InputValidator_JoiningWindow_Hard_30dBackdated_Warning()
    {
        Run(Req(joining: Day(Today.AddYears(-50).AddDays(-1)))).Errors["joiningDate"].Should().Contain(EmployeeFieldErrors.JoiningOutOfRange);
        Run(Req(joining: Day(Today.AddYears(2).AddDays(1)))).Errors["joiningDate"].Should().Contain(EmployeeFieldErrors.JoiningOutOfRange);
        var ok = Run(Req(joining: Day(Today.AddDays(-30))));
        ok.Errors.Should().BeEmpty();
        ok.Warnings.Select(w => w.Code).Should().NotContain(EmployeeWarningCodes.JoiningBackdated);
        var back = Run(Req(joining: Day(Today.AddDays(-45))));
        back.Errors.Should().BeEmpty();
        back.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.JoiningBackdated);   // G25
        Run(Req(joining: Day(Today.AddDays(181)))).Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.JoiningOver180dFuture);
        Run(Req(joining: null)).Errors.Should().BeEmpty();
    }

    [Fact]
    public void InputValidator_NegativeAllowance_Hard()
    {
        var r = Run(Req(salary: new(5000, -1, 0, 0, 0, 0, 0, null, null, "SAR")));
        r.Errors["salaryBreakdown.housingAllowance"].Should().Contain(EmployeeFieldErrors.NegativeAmount);
        Run(Req(salary: new(-5, 0, 0, 0, 0, 0, 0, null, null, "SAR"))).Errors["salaryBreakdown.basicSalary"].Should().Contain(EmployeeFieldErrors.NegativeAmount);
    }

    [Fact]
    public void InputValidator_Overflow_Hard()
    {
        var r = Run(Req(salary: new(10_000_000_000m, null, null, null, null, null, null, null, null, "SAR")));
        r.Errors["salaryBreakdown.basicSalary"].Should().Contain(EmployeeFieldErrors.AmountOverflow);
        Run(Req(salary: new(9_999_999_999.99m, null, null, null, null, null, null, null, null, "SAR"))).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Basic_Below50PctOfTotal_Warns()
    {
        var r = Run(Req(salary: new(1000, 2000, 0, 0, 0, 0, 0, null, null, "SAR")));
        r.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.BasicBelow50Pct);
        Run(Req(salary: new(3000, 1000, 0, 0, 0, 0, 0, null, null, "SAR"))).Warnings.Select(w => w.Code).Should().NotContain(EmployeeWarningCodes.BasicBelow50Pct);
    }

    [Theory]
    [InlineData("javascript:alert(1)")] [InlineData("data:image/png;base64,AAAA")] [InlineData("http://x.example/a.png")]
    [InlineData("file:///etc/passwd")] [InlineData("//evil.example/a.png")]
    public void InputValidator_ProfilePhotoUrl_RejectsJavascriptAndData(string url) =>
        Run(Req(photo: url)).Errors["profilePhotoUrl"].Should().Contain(EmployeeFieldErrors.PhotoUrlScheme);

    [Theory]
    [InlineData("https://cdn.example.com/p.png")] [InlineData("/api/employees/12/photo")]
    public void ProfilePhotoUrl_AllowsHttpsAndOwnRoute(string url) => Run(Req(photo: url)).Errors.Should().NotContainKey("profilePhotoUrl");

    [Fact]
    public void InputValidator_PhoneNormalisesLocalSaMobile()
    {
        var r = Run(Req(mobile: "0512345678"));
        r.Errors.Should().BeEmpty();
        r.Normalised.MobileNumber.Should().Be("+966512345678");
        Run(Req(mobile: "0412345678")).Errors["mobileNumber"].Should().Contain(EmployeeFieldErrors.PhoneInvalid);
    }

    [Fact]
    public void SaIban_Rules_G07()
    {
        EmployeePayrollProfileRequest P(string iban) => new(null, iban, null, null, null, null, null, true, true, null, null, null);
        Run(Req(payroll: P("SA0380000000608010167519"))).Errors.Should().BeEmpty();
        Run(Req(payroll: P("SA03 8000 0000 6080 1016 7519"))).Normalised.PayrollProfile!.Iban.Should().Be("SA0380000000608010167519");
        Run(Req(payroll: P("GB82WEST12345698765432"))).Errors["payrollProfile.iban"].Should().Contain(EmployeeFieldErrors.IbanInvalid);   // non-SA for SA company
        Run(Req(payroll: P("SA03800000006080101675"))).Errors["payrollProfile.iban"].Should().Contain(EmployeeFieldErrors.IbanInvalid);   // 22 chars
        Run(Req(payroll: P("SA0480000000608010167519"))).Errors["payrollProfile.iban"].Should().Contain(EmployeeFieldErrors.IbanInvalid); // mod-97
        Run(Req(payroll: P("GB82WEST12345698765432")), country: "AE").Errors.Should().NotContainKey("payrollProfile.iban");
    }

    [Fact]
    public void Probation_Order_And_Over180_Warning_NotClamped()
    {
        var s = Today;
        Run(Req(probStart: s.AddDays(10), probEnd: s)).Errors["probationEndDate"].Should().Contain(EmployeeFieldErrors.ProbationOrder);
        Run(Req(joining: Day(s), confirmation: s.AddDays(-1))).Errors["confirmationDate"].Should().Contain(EmployeeFieldErrors.ProbationOrder);
        var r = Run(Req(probStart: s, probEnd: s.AddDays(200)));
        r.Errors.Should().BeEmpty();
        r.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.ProbationOverLimit);
        r.Normalised.ProbationEndDate.Should().Be(s.AddDays(200), "the stored value is never clamped (G33)");
        Run(Req(probStart: s, probEnd: s.AddDays(180))).Warnings.Select(w => w.Code).Should().NotContain(EmployeeWarningCodes.ProbationOverLimit);
    }

    [Fact]
    public void NoticePeriod_0To365()
    {
        Run(Req(notice: 366)).Errors.Should().ContainKey("noticePeriodDays");
        Run(Req(notice: -1)).Errors.Should().ContainKey("noticePeriodDays");
        Run(Req(notice: 60)).Errors.Should().BeEmpty();
    }

    [Fact]
    public void ContractRules_FixedTermEnd_G26_CD17()
    {
        var s = new DateOnly(2026, 11, 1);
        ContractRules.Check("Fixed Term", s, s).Should().Contain(EmployeeFieldErrors.ContractEndBeforeStart);
        ContractRules.Check("Fixed Term", s, s.AddDays(-5)).Should().Contain(EmployeeFieldErrors.ContractEndBeforeStart);
        ContractRules.Check("Fixed Term", s, s.AddDays(1)).Should().BeEmpty();
        ContractRules.Check("Unlimited", s, s.AddYears(1)).Should().Contain(EmployeeFieldErrors.ContractEndWithoutFixedTerm);
        ContractRules.Check("Unlimited", s, null).Should().BeEmpty();
        ContractRules.Check(null, s, s.AddYears(1)).Should().Contain(EmployeeFieldErrors.ContractEndWithoutFixedTerm);
    }

    [Fact]
    public void Lengths_AndLists_AreBounded()
    {
        Run(Req(employeeCode: new string('E', 51))).Errors["employeeCode"].Should().Contain(EmployeeFieldErrors.ValueTooLong);
        Run(Req(employeeCode: "EMP 001!")).Errors["employeeCode"].Should().Contain(EmployeeFieldErrors.InvalidFormat);
        Run(Req(workEmail: new string('a', 250) + "@x.co")).Errors["workEmail"].Should().Contain(EmployeeFieldErrors.ValueTooLong);
        var many = Enumerable.Range(0, 41).Select(i => new EmployeeComplianceRecordRequest("SA", "k" + i, "L", "v", null, null, false, false)).ToList();
        Run(Req(records: many)).Errors["complianceRecords"].Should().Contain(EmployeeFieldErrors.ListTooLong);
        var longVal = new[] { new EmployeeComplianceRecordRequest("SA", "k", "L", new string('v', 201), null, null, false, false) };
        Run(Req(records: longVal)).Errors["complianceRecords[0].fieldValue"].Should().Contain(EmployeeFieldErrors.ValueTooLong);
    }

    [Fact]
    public void IdNumbers_AreValidated_FromComplianceRecords_AndNormalised()
    {
        EmployeeComplianceRecordRequest Rec(string key, string v) => new("SA", key, key, v, null, null, true, true);
        var bad = Run(Req(records: [Rec("id_number", "1000000009")]));
        bad.Errors["complianceRecords[0].fieldValue"].Should().Contain(EmployeeFieldErrors.IdChecksumInvalid);
        var good = Run(Req(records: [Rec("id_number", "١٠٠٠٠٠٠٠٠٨")]));
        good.Errors.Should().BeEmpty();
        good.Normalised.ComplianceRecords!.Single().FieldValue.Should().Be("1000000008");
        // Saudi with an Iqama-prefixed number is a hard integrity error; the reverse is only a warning.
        Run(Req(nationality: "Saudi", records: [Rec("iqama_number", "2000000006")])).Errors.Should().ContainKey("complianceRecords[0].fieldValue");
        var expat = Run(Req(nationality: "Indian", records: [Rec("id_number", "1000000008")]));
        expat.Errors.Should().BeEmpty();
        expat.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.IdPrefixNationalityMismatch);
        // Drafts keep a half-typed identity number (save what I have).
        Run(Req(records: [Rec("id_number", "1000")]), EmployeeGuardMode.Draft).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Country_IsDerivedFromCompany_IgnoresComplianceRecordCountry()
    {
        var records = new[]
        {
            new EmployeeComplianceRecordRequest("AE", "emirates_id", "Emirates ID", "784-1", null, null, false, true),
            new EmployeeComplianceRecordRequest("sau", "passport_number", "Passport", "P1234567", null, null, false, true),
        };
        var r = Run(Req(records: records), country: "SA");
        r.Normalised.ComplianceRecords!.Select(x => x.CountryCode).Should().OnlyContain(c => c == "SA");
    }

    [Fact]
    public void Nationality_IsResolvedToIso2_AndTextMirrorsCatalogName()
    {
        var r = Run(Req(nationality: "سعودي"));
        r.Normalised.NationalityCode.Should().Be("SA");
        r.Normalised.Nationality.Should().Be("Saudi Arabia");
        r.NationalityClass.Should().Be(NationalityClass.Saudi);
        Run(Req(nationality: null, nationalityCode: "pk")).Normalised.NationalityCode.Should().Be("PK");
        Run(Req(nationality: null, nationalityCode: "ZZ")).Errors["nationalityCode"].Should().Contain(EmployeeFieldErrors.NationalityUnknown);
        // Legacy free text that does not resolve is kept (Unresolved), never a hard error and never reclassified.
        var legacy = Run(Req(nationality: "Suadi"));
        legacy.Errors.Should().BeEmpty();
        legacy.Normalised.NationalityCode.Should().BeNull();
        legacy.Normalised.Nationality.Should().Be("Suadi");
    }

    [Fact]
    public void DraftMode_FloorIsEnglishNameAndCompany()
    {
        Run(Req(name: ""), EmployeeGuardMode.Draft).Errors["englishName"].Should().Contain(EmployeeFieldErrors.Required);
        var draft = Req() with { CompanyId = null };
        Run(draft, EmployeeGuardMode.Draft).Errors["companyId"].Should().Contain(EmployeeFieldErrors.Required);
        // Draft still enforces integrity bounds.
        Run(Req(dob: Today.AddDays(5)), EmployeeGuardMode.Draft).Errors.Should().ContainKey("dateOfBirth");
    }
}
