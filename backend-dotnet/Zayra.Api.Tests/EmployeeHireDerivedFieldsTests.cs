using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// DEFECT (hire rehearsal): an employee created through the Add Employee wizard was saved with the Qiwa/GOSI classification
/// columns blank. <c>saudi_or_non_saudi</c> and <c>id_type</c> were written only by the CSV import and by self-service, never by the
/// create/update every wizard save goes through, and the contract and probation start dates were never set because the wizard does
/// not ask for them (they are the start date, not a separate answer). Every Saudi/non-Saudi decision downstream (Qiwa, the GOSI
/// nationality classification, bank export) reads these columns.
///
/// <para>What is NOT derived here, on purpose: the GOSI cohort. It is keyed on the date the person first registered with GOSI, a fact
/// read off their GOSI record, never inferred from nationality or the start date (see <c>GosiCohorts</c>).</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class EmployeeHireDerivedFieldsTests
{
    private readonly PostgresFixture _fx;
    public EmployeeHireDerivedFieldsTests(PostgresFixture fx) => _fx = fx;

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "tests", Guid.NewGuid(), tenantId);

    private static readonly DateTime Joining = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The shape the hire wizard sends: a nationality CODE, an identity compliance record, a contract type, a start date and a
    /// probation end date. No contract start, no probation start, no notice period, no Saudi/non-Saudi class, no id type.</summary>
    private static EmployeeCreateRequest Wizard(string name, string nationalityCode, params EmployeeComplianceRecordRequest[] records) =>
        new(
            EmployeeCode: $"HD-{Guid.NewGuid():N}"[..14], ManualEmployeeCode: true, EnglishName: name,
            ArabicName: null, PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: null, MaritalStatus: null,
            PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
            CompanyId: null, BranchId: null, DepartmentId: null, DesignationId: null, GradeId: null, CostCenterId: null, JobTitle: null,
            ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
            EmploymentType: "Full-time", ContractType: "Permanent", JoiningDate: Joining,
            ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: new DateOnly(2027, 2, 28),
            NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null,
            ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null,
            PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: records.Length == 0 ? null : records,
            NationalityCode: nationalityCode);

    private static EmployeeComplianceRecordRequest Record(string key, string value) =>
        new("SA", key, key, value, null, null, true, false);

    private async Task<Employee> CreateAsync(EmployeeCreateRequest request)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var created = await Service(db).CreateAsync(tenantId, request, Ctx(tenantId), default);
        return await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id);
    }

    [Fact]
    public async Task SaudiCreatedWithNationalId_IsClassifiedSaudi_WithNationalIdType()
    {
        var e = await CreateAsync(Wizard("Saudi Hire", "SA", Record("id_number", "1000000008")));

        e.SaudiOrNonSaudi.Should().Be("Saudi", "the nationality on the record is Saudi; Qiwa and the GOSI classification read this column");
        e.IdType.Should().Be("NationalId", "National ID was the document chosen in the wizard (stored as the id_number record)");
    }

    [Fact]
    public async Task ExpatCreatedWithIqamaAndPassport_IsNonSaudi_AndTheIqamaIsThePrimaryDocument()
    {
        var e = await CreateAsync(Wizard("Expat Hire", "PK", Record("iqama_number", "2000000006"), Record("passport_number", "AB1234567")));

        e.SaudiOrNonSaudi.Should().Be("NonSaudi");
        e.IdType.Should().Be("Iqama", "the passport is the second document the wizard adds; the primary one is the Iqama");
    }

    [Fact]
    public async Task ExpatWithOnlyAPassport_HasPassportAsIdType()
    {
        var e = await CreateAsync(Wizard("Passport Only", "IN", Record("passport_number", "Z1234567")));

        e.SaudiOrNonSaudi.Should().Be("NonSaudi");
        e.IdType.Should().Be("Passport");
    }

    [Fact]
    public async Task ANationalityTextOfOneClass_WithAConfirmedCountryOfTheOther_IsRefused_OnCreateAndOnUpdate()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var service = Service(db);

        var create = async () => await service.CreateAsync(tenantId, Wizard("Mismatch Hire", "IN") with { Nationality = "Saudi" }, Ctx(tenantId), default);
        (await create.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("contradict");

        var created = await service.CreateAsync(tenantId, Wizard("Update Hire", "IN", Record("passport_number", "Z1234567")), Ctx(tenantId), default);
        var update = async () => await service.UpdateAsync(tenantId, created.Id,
            Wizard("Update Hire", "IN", Record("passport_number", "Z1234567")) with { Nationality = "Saudi" }, Ctx(tenantId), default);
        (await update.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("contradict");

        await using var verify = _fx.CreateDb();
        var stored = await verify.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        stored.NationalityCode.Should().Be("IN");
        stored.Nationality.Should().NotBe("Saudi");
        stored.SaudiOrNonSaudi.Should().Be("NonSaudi");
    }

    [Theory]
    [InlineData("Indian", "IN", "NonSaudi")]
    [InlineData("Saudi", "SA", "Saudi")]
    [InlineData("Kuwaiti", "KW", "")]        // GCC: no class, nothing to contradict
    [InlineData("Something else", "IN", "NonSaudi")]   // unrecognised text decides nothing
    public async Task ANationalityTextThatAgreesOrDecidesNothing_IsAccepted(string text, string code, string expectedClass)
    {
        var e = await CreateAsync(Wizard("Agreeing Hire", code) with { Nationality = text });

        e.NationalityCode.Should().Be(code);
        e.SaudiOrNonSaudi.Should().Be(expectedClass);
    }

    [Fact]
    public async Task GccNational_IsNotClassified_HrConfirms()
    {
        // GCC nationals have their own treatment in several Saudi rules; the existing classifier (WorkerNationality.ClassOf) never guesses them.
        var e = await CreateAsync(Wizard("Kuwaiti Hire", "KW", Record("civil_id", "287010100123")));

        e.SaudiOrNonSaudi.Should().BeEmpty();
        e.IdType.Should().Be("CivilId");
    }

    [Fact]
    public async Task NoNationalityAndNoDocument_StayBlank_NothingIsGuessed()
    {
        var e = await CreateAsync(Wizard("Blank Hire", ""));

        e.SaudiOrNonSaudi.Should().BeEmpty();
        e.IdType.Should().BeEmpty();
    }

    [Fact]
    public async Task ContractAndProbationStart_FollowTheStartDate_WhenTheyAreNotStated()
    {
        var e = await CreateAsync(Wizard("Dates Hire", "SA", Record("id_number", "1000000008")));

        e.ContractStartDate.Should().Be(new DateOnly(2026, 12, 1), "a new contract starts on the start date; the wizard has no separate answer for it");
        e.ProbationStartDate.Should().Be(new DateOnly(2026, 12, 1), "probation runs from the start date to the probation end date the wizard collects");
    }

    [Fact]
    public async Task NoProbationEndDate_MeansNoProbationStart_AndWaivedProbationKeepsNoDates()
    {
        var none = await CreateAsync(Wizard("No Probation Date", "SA") with { ProbationEndDate = null });
        none.ProbationStartDate.Should().BeNull("there is no probation to start");

        var waived = await CreateAsync(Wizard("Waived", "SA") with { ProbationWaived = true, ProbationEndDate = null });
        waived.ProbationStartDate.Should().BeNull();
        waived.ProbationEndDate.Should().BeNull();
    }

    [Fact]
    public async Task StatedProbationStart_IsKept()
    {
        var e = await CreateAsync(Wizard("Stated", "SA") with { ProbationStartDate = new DateOnly(2026, 12, 15) });
        e.ProbationStartDate.Should().Be(new DateOnly(2026, 12, 15));
    }

    [Fact]
    public async Task NoticePeriod_IsNotDefaulted_ItIsABusinessSetting()
    {
        // The notice period decides notice pay at exit; a default would change money. It stays blank until HR states it.
        var e = await CreateAsync(Wizard("Notice", "SA"));
        e.NoticePeriodDays.Should().BeNull();
    }

    [Fact]
    public async Task GosiCohort_IsNeverInferredFromNationality()
    {
        var e = await CreateAsync(Wizard("Cohort", "SA", Record("id_number", "1000000008")));

        e.GosiFirstRegisteredOn.Should().BeNull("the cohort is keyed on the first GOSI registration date, a fact HR reads off the GOSI record");
        Zayra.Api.Application.CountryPack.GosiCohorts.Resolve(e.GosiFirstRegisteredOn).Should().Be("Unknown");
    }

    [Fact]
    public async Task ChangingNationalityOnUpdate_ReclassifiesThePerson()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var created = await Service(db).CreateAsync(tenantId, Wizard("Changer", "SA", Record("id_number", "1000000008")), Ctx(tenantId), default);

        await Service(db).UpdateAsync(tenantId, created.Id, Wizard("Changer", "PK", Record("iqama_number", "2000000006")) with { EmployeeCode = created.EmployeeCode }, Ctx(tenantId), default);

        var stored = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        stored.SaudiOrNonSaudi.Should().Be("NonSaudi");
        stored.IdType.Should().Be("Iqama");
    }

    [Fact]
    public async Task APassportOnlyEdit_DoesNotReplaceAnExistingNationalIdType()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var created = await Service(db).CreateAsync(tenantId, Wizard("Keeps", "SA", Record("id_number", "1000000008")), Ctx(tenantId), default);

        await Service(db).UpdateAsync(tenantId, created.Id, Wizard("Keeps", "SA", Record("passport_number", "P7654321")) with { EmployeeCode = created.EmployeeCode }, Ctx(tenantId), default);

        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IdType.Should().Be("NationalId");
    }
}
