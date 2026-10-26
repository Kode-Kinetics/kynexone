using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class EmployeeInputGuardTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Guid Tenant, Company Company) Seed(ZayraDbContext db, string country = "SA")
    {
        var tenant = Guid.NewGuid();
        var c = new Company { TenantId = tenant, LegalNameEn = "Main", TradeName = "Main", CountryCode = country, Jurisdiction = "t",
            RegistrationNumber = "RC-" + Guid.NewGuid().ToString("N"), DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(c); db.SaveChanges();
        return (tenant, c);
    }

    private static RequestContext Ctx(Guid tenant, params string[] perms) =>
        new("127.0.0.1", "test", Guid.NewGuid(), tenant, ["HR Manager"], perms);

    private static EmployeeInputGuard Guard(ZayraDbContext db) => new(db);

    private static EmployeeCreateRequest Req(Company c, params EmployeeComplianceRecordRequest[] records) =>
        EmployeeInputValidatorTests.Req(records: records.Length == 0 ? null : records) with { CompanyId = c.Id };

    private static DateTime Today => DateTime.UtcNow.Date;

    [Fact]
    public async Task Guard_ReturnsNormalisedRequest_CountryDerivedFromCompany()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var r = Req(c, new EmployeeComplianceRecordRequest("AE", "id_number", "ID", "١٠٠٠٠٠٠٠٠٨", null, null, true, true)) with { MobileNumber = "0512345678", Nationality = "سعودي" };
        var o = await Guard(db).EnforceCreateAsync(t, r, Ctx(t), default);
        o.MobileNumber.Should().Be("+966512345678");
        o.NationalityCode.Should().Be("SA");
        o.ComplianceRecords!.Single().CountryCode.Should().Be("SA");
        o.ComplianceRecords!.Single().FieldValue.Should().Be("1000000008");
    }

    [Fact]
    public async Task Guard_HardErrors_Throw400_WithFieldCodes()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var r = Req(c, new EmployeeComplianceRecordRequest("SA", "id_number", "ID", "1000000009", null, null, true, true)) with { EnglishName = new string('x', 151) };
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() => Guard(db).EnforceCreateAsync(t, r, Ctx(t), default));
        ex.Status.Should().Be(400); ex.Code.Should().Be("validation_failed");
        ex.Errors.Should().ContainKey("englishName");
        ex.Errors.Should().ContainKey("complianceRecords[0].fieldValue");
    }

    [Fact]
    public async Task Warnings_Throw409_ThenReplayWithReason_Passes()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var r = Req(c) with { JoiningDate = Today.AddDays(-45), ProbationStartDate = DateOnly.FromDateTime(Today), ProbationEndDate = DateOnly.FromDateTime(Today).AddDays(200) };
        var ex = await Assert.ThrowsAsync<EmployeeWarningsRequireAckException>(() => Guard(db).EnforceCreateAsync(t, r, Ctx(t), default));
        ex.Warnings.Select(w => w.Code).Should().Contain([EmployeeWarningCodes.JoiningBackdated, EmployeeWarningCodes.ProbationOver180]);

        var ack = ex.Warnings.Select(w => new AcknowledgedWarning(w.Code, "Approved by the director, signed contract on file")).ToList();
        var o = await Guard(db).EnforceCreateAsync(t, r with { AcknowledgedWarnings = ack }, Ctx(t), default);
        o.ProbationEndDate.Should().Be(DateOnly.FromDateTime(Today).AddDays(200), "stored unclamped (G33)");
    }

    [Fact]
    public async Task Acknowledgement_WithoutReason_Is422_OverrideReasonRequired()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var r = Req(c) with { JoiningDate = Today.AddDays(-45), AcknowledgedWarnings = [new AcknowledgedWarning(EmployeeWarningCodes.JoiningBackdated, "short")] };
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() => Guard(db).EnforceCreateAsync(t, r, Ctx(t), default));
        ex.Status.Should().Be(422); ex.Code.Should().Be(EmployeeProblemCodes.OverrideReasonRequired);
    }

    [Fact]
    public async Task MinimumAge_Override_RequiresPermission()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var dob = DateOnly.FromDateTime(Today).AddYears(-14);
        var r = Req(c) with { DateOfBirth = dob, JoiningDate = Today,
            AcknowledgedWarnings = [new AcknowledgedWarning(EmployeeWarningCodes.UnderMinimumAge, "Apprenticeship permitted by the labour office")] };
        var denied = await Assert.ThrowsAsync<EmployeeValidationException>(() => Guard(db).EnforceCreateAsync(t, r, Ctx(t), default));
        denied.Status.Should().Be(422); denied.Code.Should().Be(EmployeeProblemCodes.OverrideNotPermitted);
        (await Guard(db).EnforceCreateAsync(t, r, Ctx(t, "employees.override_validation"), default)).DateOfBirth.Should().Be(dob);
    }

    [Fact]
    public async Task ChecksumOverride_NeedsPermissionAndReason_FormatNeverOverridable()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var badSum = new EmployeeComplianceRecordRequest("SA", "id_number", "ID", "1000000009", null, null, true, true);
        var ack = new[] { new AcknowledgedWarning(EmployeeWarningCodes.IdChecksumInvalid, "Legacy card number verified against Absher") };
        var r = Req(c, badSum) with { AcknowledgedWarnings = ack };
        var noPerm = await Assert.ThrowsAsync<EmployeeValidationException>(() => Guard(db).EnforceCreateAsync(t, r, Ctx(t), default));
        noPerm.Code.Should().Be(EmployeeProblemCodes.OverrideNotPermitted);
        (await Guard(db).EnforceCreateAsync(t, r, Ctx(t, "employees.override_validation"), default)).Should().NotBeNull();
        var shortReason = r with { AcknowledgedWarnings = [new AcknowledgedWarning(EmployeeWarningCodes.IdChecksumInvalid, "9 chars..")] };
        (await Assert.ThrowsAsync<EmployeeValidationException>(() => Guard(db).EnforceCreateAsync(t, shortReason, Ctx(t, "employees.override_validation"), default)))
            .Code.Should().Be(EmployeeProblemCodes.OverrideReasonRequired);
        var badFormat = Req(c, new EmployeeComplianceRecordRequest("SA", "id_number", "ID", "100000008", null, null, true, true)) with { AcknowledgedWarnings = ack };
        (await Assert.ThrowsAsync<EmployeeValidationException>(() => Guard(db).EnforceCreateAsync(t, badFormat, Ctx(t, "employees.override_validation"), default)))
            .Status.Should().Be(400);
    }

    [Fact]
    public async Task Guard_ForeignCompany_Is422_NotA500()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var (_, foreign) = Seed(db);
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() => Guard(db).EnforceCreateAsync(t, Req(foreign), Ctx(t), default));
        ex.Status.Should().Be(422);
    }

    [Fact]
    public async Task Draft_NeedsOnlyNameAndCompany()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var draft = EmployeeInputValidatorTests.Req(nationality: null) with { CompanyId = c.Id, Gender = null, EmploymentType = null, ContractType = null };
        var o = await Guard(db).EnforceDraftAsync(t, null, draft, Ctx(t), default);
        o.EnglishName.Should().Be(draft.EnglishName);
    }
}
