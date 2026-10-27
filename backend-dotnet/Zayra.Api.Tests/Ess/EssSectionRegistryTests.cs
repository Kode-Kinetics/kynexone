using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Ess;

/// <summary>P2 unit tests: the section registry and the validation adapter. No database, no HTTP.</summary>
public class EssSectionRegistryTests
{
    private static readonly EssEmployeeFacts Saudi = new(NationalityClass.Saudi, "SA", false);
    private static readonly EssEmployeeFacts Expat = new(NationalityClass.Expat, "SA", false);
    private static readonly EssEmployeeFacts Gcc = new(NationalityClass.Gcc, "SA", false);

    [Fact]
    public void EssSection_RejectsUnknownFieldsAndMasksBankOnRead()
    {
        // Whitelist: status, company, grade, salary, role... are never fields of any section.
        foreach (var key in EssSectionKeys.All)
            ProfileSectionRegistry.UnknownFields(key, NationalityClass.Saudi, ["status", "companyId", "grade", "salary", "role", "workEmail", "bankIban"])
                .Should().Contain(["status", "companyId", "grade", "salary", "role", "workEmail"], because: key);
        // bankIban is not a field name either; the bank section's field is "iban".
        ProfileSectionRegistry.UnknownFields(EssSectionKeys.Bank, null, ["iban", "bankName"]).Should().BeEmpty();
        ProfileSectionRegistry.UnknownFields(EssSectionKeys.Bank, null, ["iban", "bankIban"]).Should().Equal("bankIban");

        var iban = ProfileSectionRegistry.FieldsFor(EssSectionKeys.Bank, null).Single(f => f.Key == "iban");
        var masked = ProfileSectionRegistry.Mask(iban, "SA0380000000608010167519");
        masked.Should().EndWith("7519").And.NotContain("0608010167");
        masked.Should().Be("SA** **** **** **** **** 7519");
        ProfileSectionRegistry.Mask(iban, "").Should().BeEmpty();
        var nid = ProfileSectionRegistry.FieldsFor(EssSectionKeys.Id, NationalityClass.Saudi).Single(f => f.Sensitive);
        ProfileSectionRegistry.Mask(nid, "1000000008").Should().Be("******0008");
    }

    [Fact]
    public void SectionRegistry_EveryFieldHasValidatorAndMaxLength()
    {
        ProfileSectionRegistry.All.Select(s => s.Key).Should().Equal(EssSectionKeys.All);
        foreach (var cls in new NationalityClass?[] { NationalityClass.Saudi, NationalityClass.Gcc, NationalityClass.Expat, null })
            foreach (var section in EssSectionKeys.All)
                foreach (var f in ProfileSectionRegistry.FieldsFor(section, cls))
                {
                    f.MaxLength.Should().BeInRange(1, 200, because: $"{section}.{f.Key}");
                    f.LabelKey.Should().StartWith("ess.completion.field.");
                    // A validator exists: an over-long value is always refused with value_too_long.
                    var tooLong = new Dictionary<string, string> { [f.Key] = new string('9', f.MaxLength + 1) };
                    SectionValidation.Validate(section, cls, "SA", tooLong).Should().ContainKey(f.Key, because: $"{section}.{f.Key}")
                        .WhoseValue.Should().Contain(EmployeeFieldErrors.ValueTooLong);
                }
        // Sensitive sections need step-up.
        ProfileSectionRegistry.Find(EssSectionKeys.Id)!.RequiresStepUp.Should().BeTrue();
        ProfileSectionRegistry.Find(EssSectionKeys.Bank)!.RequiresStepUp.Should().BeTrue();
        ProfileSectionRegistry.Find(EssSectionKeys.Person)!.RequiresStepUp.Should().BeFalse();
    }

    [Fact]
    public void SectionRegistry_MissingDerivation_Saudi_vs_Expat()
    {
        var blank = new Employee();
        // Saudi: national id column; an Iqama never satisfies it.
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, blank, Saudi).Should().BeTrue();
        ProfileSectionRegistry.FieldsFor(EssSectionKeys.Id, NationalityClass.Saudi).Select(f => f.Key).Should().Equal("nationalId");
        var saudiWithIqamaOnly = new Employee { IqamaNumber = "2000000006" };
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, saudiWithIqamaOnly, Saudi).Should().BeTrue();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { IdNumber = "1000000008" }, Saudi).Should().BeFalse();

        // Expat: Iqama or Border Number; the national id column never satisfies it.
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, blank, Expat).Should().BeTrue();
        ProfileSectionRegistry.FieldsFor(EssSectionKeys.Id, NationalityClass.Expat).Select(f => f.Key)
            .Should().Equal("iqamaNumber", "iqamaExpiry", "borderNumber", "passportNumber", "passportExpiry");
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { IdNumber = "1000000008" }, Expat).Should().BeTrue();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { IqamaNumber = "2000000006" }, Expat).Should().BeFalse();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { BorderNumber = "3000000001" }, Expat).Should().BeFalse();

        // A GCC national holds their own national ID (never an Iqama or Border Number), plus a passport, each with an expiry.
        ProfileSectionRegistry.FieldsFor(EssSectionKeys.Id, NationalityClass.Gcc).Select(f => f.Key)
            .Should().Equal("gccNationalId", "gccIdExpiry", "passportNumber", "passportExpiry");
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { NationalityCode = "AE", IqamaNumber = "2000000006", BorderNumber = "3000000001" }, Gcc).Should().BeTrue();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { NationalityCode = "AE", EmiratesId = "784199012345671" }, Gcc).Should().BeFalse();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { NationalityCode = "QA", Qid = "28435600123" }, Gcc).Should().BeFalse();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { NationalityCode = "KW", CivilId = "289010100123" }, Gcc).Should().BeFalse();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Id, new Employee { NationalityCode = "AE", Qid = "28435600123" }, Gcc).Should().BeTrue("the column is the nationality's own");

        // The other sections derive from their own columns only.
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Person, blank, Saudi).Should().BeTrue();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Person, new Employee { PersonalEmail = "a@b.co", Phone = "+966500000000" }, Saudi).Should().BeFalse();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Emergency, new Employee { EmergencyContactName = "Ali" }, Saudi).Should().BeTrue();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.ArabicName, new Employee { ArabicName = "علي" }, Saudi).Should().BeFalse();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Bank, blank, Saudi).Should().BeTrue();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Bank, new Employee { BankIban = "SA0380000000608010167519" }, Saudi).Should().BeFalse();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Documents, blank, Saudi).Should().BeTrue();
        ProfileSectionRegistry.IsMissing(EssSectionKeys.Documents, blank, Saudi with { HasIdDocument = true }).Should().BeFalse();

        ProfileSectionRegistry.MissingSections(blank, Expat).Should().Equal(EssSectionKeys.All);
        var complete = new Employee
        {
            PersonalEmail = "a@b.co", Phone = "+966500000000", EmergencyContactName = "Ali", EmergencyContactPhone = "+966500000001",
            ArabicName = "علي", IdNumber = "1000000008", BankIban = "SA0380000000608010167519",
        };
        ProfileSectionRegistry.MissingSections(complete, Saudi with { HasIdDocument = true }).Should().BeEmpty();
    }

    [Fact]
    public void SectionValidation_SaudiIdChecksum_SameVectorsAsWizard()
    {
        var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hire-vectors.json"))).RootElement;
        foreach (var valid in vectors.GetProperty("saudiNationalId").GetProperty("valid").EnumerateArray().Select(x => x.GetString()!))
            SectionValidation.Validate(EssSectionKeys.Id, NationalityClass.Saudi, "SA", new Dictionary<string, string> { ["nationalId"] = valid })
                .Should().BeEmpty(because: valid);
        foreach (var bad in vectors.GetProperty("saudiNationalId").GetProperty("invalidChecksum").EnumerateArray().Select(x => x.GetString()!))
            SectionValidation.Validate(EssSectionKeys.Id, NationalityClass.Saudi, "SA", new Dictionary<string, string> { ["nationalId"] = bad })
                .Should().ContainKey("nationalId", because: bad).WhoseValue.Should().Contain(EmployeeFieldErrors.IdChecksumInvalid);
        foreach (var bad in vectors.GetProperty("saudiNationalId").GetProperty("invalidFormat").EnumerateArray().Select(x => x.GetString()!).Where(x => x.Length > 0))
            SectionValidation.Validate(EssSectionKeys.Id, NationalityClass.Saudi, "SA", new Dictionary<string, string> { ["nationalId"] = bad })
                .Should().ContainKey("nationalId", because: bad).WhoseValue.Should().Contain(EmployeeFieldErrors.IdFormatInvalid);
        foreach (var valid in vectors.GetProperty("iqama").GetProperty("valid").EnumerateArray().Select(x => x.GetString()!))
            SectionValidation.Validate(EssSectionKeys.Id, NationalityClass.Expat, "SA", new Dictionary<string, string> { ["iqamaNumber"] = valid })
                .Should().BeEmpty(because: valid);
        // Arabic-Indic digits are normalised the same way the wizard does.
        SectionValidation.Validate(EssSectionKeys.Id, NationalityClass.Saudi, "SA", new Dictionary<string, string> { ["nationalId"] = "١٠٠٠٠٠٠٠٠٨" })
            .Should().BeEmpty();
    }

    [Fact]
    public void SectionValidation_IdentityByClass_GccIdPassportAndExpiries()
    {
        Dictionary<string, List<string>> V(NationalityClass c, params (string, string)[] kv) =>
            SectionValidation.Validate(EssSectionKeys.Id, c, "SA", kv.ToDictionary(x => x.Item1, x => x.Item2));
        V(NationalityClass.Gcc, ("gccNationalId", "784199012345671"), ("gccIdExpiry", "2030-05-01"), ("passportNumber", "N1234567"), ("passportExpiry", "2031-01-31")).Should().BeEmpty();
        V(NationalityClass.Gcc, ("gccNationalId", "12")).Should().ContainKey("gccNationalId").WhoseValue.Should().Contain(EmployeeFieldErrors.IdFormatInvalid);
        V(NationalityClass.Gcc, ("gccNationalId", "78419901234 71!")).Should().ContainKey("gccNationalId");
        V(NationalityClass.Gcc, ("passportNumber", "AB")).Should().ContainKey("passportNumber").WhoseValue.Should().Contain(EmployeeFieldErrors.InvalidFormat);
        V(NationalityClass.Gcc, ("gccIdExpiry", "2030-13-45")).Should().ContainKey("gccIdExpiry").WhoseValue.Should().Contain(EmployeeFieldErrors.InvalidFormat);
        V(NationalityClass.Gcc, ("gccIdExpiry", "01/05/2030")).Should().ContainKey("gccIdExpiry");
        // An expat's Iqama keeps the wizard's checksum, and its expiry is a real date.
        V(NationalityClass.Expat, ("iqamaNumber", "2000000006"), ("iqamaExpiry", "2027-02-02"), ("passportNumber", "P7654321"), ("passportExpiry", "2030-02-02")).Should().BeEmpty();
        V(NationalityClass.Expat, ("iqamaNumber", "2000000007")).Should().ContainKey("iqamaNumber").WhoseValue.Should().Contain(EmployeeFieldErrors.IdChecksumInvalid);
        V(NationalityClass.Expat, ("iqamaExpiry", "2027-02-30")).Should().ContainKey("iqamaExpiry");
        // The fields of another class are unknown, not silently accepted.
        V(NationalityClass.Gcc, ("iqamaNumber", "2000000006")).Should().ContainKey("iqamaNumber").WhoseValue.Should().Contain("unknown_field");
        V(NationalityClass.Expat, ("gccNationalId", "784199012345671")).Should().ContainKey("gccNationalId");
        // Required at submit: Gcc needs the GCC id; the expat needs an Iqama or Border Number.
        SectionValidation.Validate(EssSectionKeys.Id, NationalityClass.Gcc, "SA", new Dictionary<string, string> { ["passportNumber"] = "N1234567" }, requireComplete: true)
            .Should().ContainKey("gccNationalId").WhoseValue.Should().Contain(EmployeeFieldErrors.Required);
        SectionValidation.Normalise(EssSectionKeys.Id, NationalityClass.Gcc, "SA", new Dictionary<string, string> { ["gccNationalId"] = " 784199012345671 ", ["passportNumber"] = "n1234567" })
            .Should().Equal(new Dictionary<string, string> { ["gccNationalId"] = "784199012345671", ["passportNumber"] = "N1234567" });
    }

    [Fact]
    public void Registry_GccAndExpatIdentityRoundTrip_ThroughTheNationalitysOwnColumns()
    {
        var ae = new Employee { NationalityCode = "AE" };
        ProfileSectionRegistry.Apply(EssSectionKeys.Id, ae, new Dictionary<string, string> { ["gccNationalId"] = "784199012345671", ["gccIdExpiry"] = "2030-05-01", ["passportNumber"] = "N1234567", ["passportExpiry"] = "2031-01-31" });
        ae.EmiratesId.Should().Be("784199012345671"); ae.EmiratesIdExpiryDate.Should().Be(new DateOnly(2030, 5, 1));
        ae.PassportNumber.Should().Be("N1234567"); ae.PassportExpiryDate.Should().Be(new DateOnly(2031, 1, 31));
        ae.IqamaNumber.Should().BeEmpty();
        ProfileSectionRegistry.CurrentValue(EssSectionKeys.Id, "gccNationalId", ae).Should().Be("784199012345671");
        ProfileSectionRegistry.CurrentValue(EssSectionKeys.Id, "gccIdExpiry", ae).Should().Be("2030-05-01");
        var qa = new Employee { NationalityCode = "QA" };
        ProfileSectionRegistry.Apply(EssSectionKeys.Id, qa, new Dictionary<string, string> { ["gccNationalId"] = "28435600123", ["gccIdExpiry"] = "2029-03-03" });
        qa.Qid.Should().Be("28435600123"); qa.QidExpiryDate.Should().Be(new DateOnly(2029, 3, 3));
        var kw = new Employee { NationalityCode = "KW" };
        ProfileSectionRegistry.Apply(EssSectionKeys.Id, kw, new Dictionary<string, string> { ["gccNationalId"] = "289010100123", ["gccIdExpiry"] = "2029-03-03" });
        kw.CivilId.Should().Be("289010100123"); kw.CivilIdExpiryDate.Should().Be(new DateOnly(2029, 3, 3));
        var pk = new Employee { NationalityCode = "PK" };
        ProfileSectionRegistry.Apply(EssSectionKeys.Id, pk, new Dictionary<string, string> { ["iqamaNumber"] = "2000000006", ["iqamaExpiry"] = "2027-02-02", ["passportNumber"] = "P7654321" });
        pk.IqamaNumber.Should().Be("2000000006"); pk.IqamaExpiryDate.Should().Be(new DateOnly(2027, 2, 2)); pk.PassportNumber.Should().Be("P7654321");
        // identity numbers are masked on read, expiries are not (HR and the employee see the date, with its Hijri equivalent)
        var gccId = ProfileSectionRegistry.FieldsFor(EssSectionKeys.Id, NationalityClass.Gcc).Single(f => f.Key == "gccNationalId");
        var passport = ProfileSectionRegistry.FieldsFor(EssSectionKeys.Id, NationalityClass.Gcc).Single(f => f.Key == "passportNumber");
        var expiry = ProfileSectionRegistry.FieldsFor(EssSectionKeys.Id, NationalityClass.Gcc).Single(f => f.Key == "gccIdExpiry");
        ProfileSectionRegistry.Mask(gccId, "784199012345671").Should().EndWith("5671").And.StartWith("*");
        ProfileSectionRegistry.Mask(passport, "N1234567").Should().Be("****4567");
        ProfileSectionRegistry.Mask(expiry, "2030-05-01").Should().Be("2030-05-01");
    }

    [Fact]
    public void SectionValidation_Iban_SaVsGenericByCompanyCountry()
    {
        var sa = "SA0380000000608010167519";
        var ae = "AE070331234567890123456";
        SectionValidation.Validate(EssSectionKeys.Bank, null, "SA", new Dictionary<string, string> { ["iban"] = sa }).Should().BeEmpty();
        SectionValidation.Validate(EssSectionKeys.Bank, null, "SA", new Dictionary<string, string> { ["iban"] = "SA0380000000608010167518" })
            .Should().ContainKey("iban").WhoseValue.Should().Contain(EmployeeFieldErrors.IbanInvalid);
        // A Saudi company needs a Saudi IBAN; another GCC company accepts any valid IBAN of its region.
        SectionValidation.Validate(EssSectionKeys.Bank, null, "SA", new Dictionary<string, string> { ["iban"] = ae })
            .Should().ContainKey("iban");
        SectionValidation.Validate(EssSectionKeys.Bank, null, "AE", new Dictionary<string, string> { ["iban"] = ae }).Should().BeEmpty();
        // spaces and case are normalised
        SectionValidation.Validate(EssSectionKeys.Bank, null, "SA", new Dictionary<string, string> { ["iban"] = "sa03 8000 0000 6080 1016 7519" }).Should().BeEmpty();
    }

    [Fact]
    public void LegacyReasonString_StillMapsToPersonAndEmergency()
    {
        ProfileSectionRegistry.SectionsForLegacyReason(EssCompletion.LegacyReason).Should().Equal(EssSectionKeys.Person, EssSectionKeys.Emergency);
        ProfileSectionRegistry.SectionsForLegacyReason("Anything else").Should().BeEmpty();
        ProfileSectionRegistry.SectionsForLegacyReason(null).Should().BeEmpty();
        // and the legacy fields are exactly the union of the two sections' fields.
        var legacy = new[] { "preferredName", "personalEmail", "phone", "maritalStatus", "emergencyContactName", "emergencyContactPhone" };
        ProfileSectionRegistry.FieldsFor(EssSectionKeys.Person, null).Concat(ProfileSectionRegistry.FieldsFor(EssSectionKeys.Emergency, null))
            .Select(f => f.Key).Should().BeEquivalentTo(legacy);
    }

    [Fact]
    public void EssIbanChange_NeverAppliedByHrApproval()
    {
        // The registry itself refuses to write a bank section onto the employee: acceptance of IBAN goes through Finance.
        var e = new Employee { BankIban = "SA0380000000608010167519", BankName = "Old" };
        var act = () => ProfileSectionRegistry.Apply(EssSectionKeys.Bank, e, new Dictionary<string, string> { ["iban"] = "SA4420000001234567891234", ["bankName"] = "New" });
        act.Should().Throw<InvalidOperationException>();
        e.BankIban.Should().Be("SA0380000000608010167519");
        e.BankName.Should().Be("Old");
        // Non-bank sections do apply.
        var p = new Employee();
        ProfileSectionRegistry.Apply(EssSectionKeys.Person, p, new Dictionary<string, string> { ["phone"] = "+966500000000", ["personalEmail"] = "x@y.co", ["maritalStatus"] = "Single", ["preferredName"] = "Sam" });
        p.Phone.Should().Be("+966500000000"); p.PersonalEmail.Should().Be("x@y.co"); p.MaritalStatus.Should().Be("Single"); p.PreferredName.Should().Be("Sam");
        var s = new Employee();
        ProfileSectionRegistry.Apply(EssSectionKeys.Id, s, new Dictionary<string, string> { ["nationalId"] = "1000000008" });
        s.IdNumber.Should().Be("1000000008"); s.IdType.Should().Be("NationalId");
        var x = new Employee();
        ProfileSectionRegistry.Apply(EssSectionKeys.Id, x, new Dictionary<string, string> { ["iqamaNumber"] = "2000000006" });
        x.IqamaNumber.Should().Be("2000000006");
    }

    [Fact]
    public void NoticeVersion_Required()
    {
        EssCompletion.NoticeVersion.Should().NotBeNullOrWhiteSpace();
        EssSubmitGuard.NoticeError(null).Should().Be("notice_required");
        EssSubmitGuard.NoticeError("").Should().Be("notice_required");
        EssSubmitGuard.NoticeError("ess-0").Should().Be("notice_required");
        EssSubmitGuard.NoticeError(EssCompletion.NoticeVersion).Should().BeNull();
    }
}
