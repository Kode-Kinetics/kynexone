using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Shifts;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Infrastructure.Shifts;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Employee reference values (gender, marital status, employment type, contract type) used to be stored as the
/// English label, so renaming a value in Setup orphaned every employee holding the old label and Arabic users saw
/// English. The forms and the import now store the code. Older rows still hold the label and are not rewritten, so
/// every reader goes through <see cref="ReferenceList"/>: these tests pin that a legacy-label row and a code row read
/// and compare the same, that a rename does not orphan a code row, and that pay equity, the roster planner and the
/// import all honour both forms.
/// </summary>
public class ReferenceListCodeOrLabelTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ReferenceList Genders(params (string Code, string En, string Ar)[] values) =>
        ReferenceList.FromValues(ReferenceList.GenderType, values);

    private static readonly (string, string, string)[] SeededGenders = [("MALE", "Male", "ذكر"), ("FEMALE", "Female", "أنثى")];

    // ── the helper ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("MALE")]
    [InlineData("Male")]
    [InlineData("male")]
    [InlineData("  Male ")]
    [InlineData("ذكر")]
    [InlineData("M")]
    public void ACodeALabelAndTheArabicLabel_AllReadAsTheCode(string stored)
    {
        Genders(SeededGenders).ToCode(stored).Should().Be("MALE");
        Genders(SeededGenders).Same(stored, "MALE").Should().BeTrue();
    }

    [Fact]
    public void ALegacyLabelRowAndACodeRow_CompareTheSame()
    {
        var list = Genders(SeededGenders);
        list.Same("Female", "FEMALE").Should().BeTrue();
        list.Same("Female", "MALE").Should().BeFalse();
        list.Normalize("Female").Should().Be(list.Normalize("FEMALE"));
    }

    [Fact]
    public void RenamingALabel_DoesNotOrphanACodeRow_NorTheOldLabelRow()
    {
        // HR renamed "Male" to "Man" in Setup. The code is immutable (MasterDataController ignores it on update).
        var renamed = Genders(("MALE", "Man", "رجل"), ("FEMALE", "Woman", "امرأة"));

        renamed.ToCode("MALE").Should().Be("MALE", "a row that stores the code is untouched by a rename");
        renamed.ToCode("Man").Should().Be("MALE");
        renamed.ToCode("Male").Should().Be("MALE", "a legacy row still holding the old label resolves through the starter values");
        renamed.LabelEn("MALE").Should().Be("Man", "the code reads with today's label");
        renamed.LabelEn("Male").Should().Be("Man");
    }

    [Fact]
    public void ATenantThatRecodedItsList_StillMatchesTheSeededCodeAndLabel()
    {
        var recoded = Genders(("M", "Male", "ذكر"), ("F", "Female", "أنثى"));
        recoded.ToCode("MALE").Should().Be("M");
        recoded.ToCode("Male").Should().Be("M");
        recoded.Same("MALE", "M").Should().BeTrue();
    }

    [Fact]
    public void AnUnknownValue_IsKeptAsTyped_NeverTurnedIntoAnotherValue()
    {
        var list = Genders(SeededGenders);
        list.ToCode("Prefer not to say").Should().BeNull();
        list.Normalize("  Prefer not to say ").Should().Be("Prefer not to say");
        list.Normalize(null).Should().BeEmpty();
        list.Same("Prefer not to say", "MALE").Should().BeFalse();
    }

    [Fact]
    public void ACodeWins_OverAnotherValuesLabelThatReadsTheSame()
    {
        var list = ReferenceList.FromValues("Custom", [("A", "B", ""), ("B", "Bee", "")]);
        list.ToCode("B").Should().Be("B");
        list.ToCode("Bee").Should().Be("B");
    }

    [Fact]
    public async Task LoadAsync_ReadsOnlyThisTenantsUndeletedValues_IncludingRetiredOnes()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var type = new MasterDataType { TenantId = tenant, Code = ReferenceList.GenderType, NameEn = "Gender" };
        var otherType = new MasterDataType { TenantId = other, Code = ReferenceList.GenderType, NameEn = "Gender" };
        db.MasterDataTypes.AddRange(type, otherType);
        db.MasterDataValues.AddRange(
            new MasterDataValue { TenantId = tenant, TypeId = type.Id, Code = "MALE", ValueEn = "Man", ValueAr = "رجل" },
            new MasterDataValue { TenantId = tenant, TypeId = type.Id, Code = "X", ValueEn = "Retired value", IsActive = false },
            new MasterDataValue { TenantId = tenant, TypeId = type.Id, Code = "GONE", ValueEn = "Deleted value", IsDeleted = true },
            new MasterDataValue { TenantId = other, TypeId = otherType.Id, Code = "OTHER", ValueEn = "Other tenant" });
        await db.SaveChangesAsync();

        var list = await ReferenceList.LoadAsync(db, tenant, ReferenceList.GenderType, CancellationToken.None);

        list.ToCode("Man").Should().Be("MALE");
        list.ToCode("Retired value").Should().Be("X", "an employee may still hold a retired value");
        list.ToCode("Deleted value").Should().BeNull();
        list.ToCode("Other tenant").Should().BeNull("another tenant's list never leaks in");
    }

    // ── pay equity ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Male", "Male")]
    [InlineData("MALE", "Male")]
    [InlineData("m", "Male")]
    [InlineData("Female", "Female")]
    [InlineData("FEMALE", "Female")]
    [InlineData("أنثى", "Female")]
    [InlineData("", "Not stated")]
    [InlineData("Unknown", "Not stated")]
    public void PayEquity_GroupsLabelAndCodeRowsTogether(string stored, string group) =>
        PayEquityService.Gender(stored).Should().Be(group);

    [Fact]
    public void PayEquity_AfterARename_StillGroupsCodeAndLegacyRowsTogether()
    {
        var renamed = Genders(("MALE", "Man", "رجل"), ("FEMALE", "Woman", "امرأة"));
        PayEquityService.Gender("MALE", renamed).Should().Be("Male");
        PayEquityService.Gender("Male", renamed).Should().Be("Male");
        PayEquityService.Gender("Woman", renamed).Should().Be("Female");
    }

    // ── roster planner ────────────────────────────────────────────────────────

    private sealed class NoLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("the deterministic planner must not call a model");
    }

    private sealed class NoRecorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task RosterGenderRule_AppliesToCodeRowsAndLegacyLabelRowsAlike()
    {
        var day = Guid.NewGuid();
        var night = Guid.NewGuid();
        var from = new DateOnly(2026, 8, 17);
        var input = new RosterPlanInput(
            from, from.AddDays(2),
            [new(1, "Code row", "FEMALE", "Ops"), new(2, "Label row", "Female", "Ops"), new(3, "Man", "MALE", "Ops"), new(4, "Arabic label row", "أنثى", "Ops")],
            [new(day, "DAY", "Day", new TimeOnly(8, 0), new TimeOnly(16, 0), "#2F6BFF"),
             new(night, "NIGHT", "Night", new TimeOnly(20, 0), new TimeOnly(4, 0), "#123456")],
            new RosterPlanPolicy([new GenderShiftRule("Female", ["DAY"], "required"), new GenderShiftRule("Male", ["NIGHT"], "required")],
                [], [], [], 8, 6),
            new HashSet<DateOnly>(), new HashSet<DateOnly>(),
            new RosterPlanCaller(Guid.NewGuid(), Guid.NewGuid(), "HR Manager"));
        var options = new AiOptions("fallback", "test-model", string.Empty, string.Empty, string.Empty, "key", 4096, true, false);
        var planner = new RosterPlannerService(new NoLlm(), options, new NoRecorder(), NullLogger<RosterPlannerService>.Instance);

        var result = await planner.PlanAsync(input, CancellationToken.None);

        result.Assignments.Where(a => a.EmployeeId is 1 or 2 or 4).Should().NotBeEmpty()
            .And.OnlyContain(a => a.ShiftCode == "DAY", "a 'Female' rule binds a FEMALE row, a Female row and an أنثى row the same way");
        result.Assignments.Where(a => a.EmployeeId == 3).Should().NotBeEmpty()
            .And.OnlyContain(a => a.ShiftCode == "NIGHT");
    }

    // ── employee import ───────────────────────────────────────────────────────

    [Fact]
    public async Task Import_StoresTheCode_ForACodeALabelOrAnArabicLabel_AndKeepsAnUnknownValueAsTyped()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Ref", Slug = $"r-{tenantId:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxEmployees = 100, Plan = "Enterprise", Status = "Active" });
        var gender = new MasterDataType { TenantId = tenantId, Code = ReferenceList.GenderType, NameEn = "Gender" };
        var employment = new MasterDataType { TenantId = tenantId, Code = ReferenceList.EmploymentTypeType, NameEn = "Employment Type" };
        db.MasterDataTypes.AddRange(gender, employment);
        db.MasterDataValues.AddRange(
            new MasterDataValue { TenantId = tenantId, TypeId = gender.Id, Code = "MALE", ValueEn = "Man", ValueAr = "ذكر" },
            new MasterDataValue { TenantId = tenantId, TypeId = gender.Id, Code = "FEMALE", ValueEn = "Woman", ValueAr = "أنثى" },
            new MasterDataValue { TenantId = tenantId, TypeId = employment.Id, Code = "FULL_TIME", ValueEn = "Full-Time", ValueAr = "دوام كامل" });
        await db.SaveChangesAsync();
        var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

        var csv =
            "EmployeeCode,FullName,Gender,MaritalStatus,EmploymentType,ContractType,JoiningDate\n" +
            "R1,Code Row,FEMALE,MARRIED,FULL_TIME,UNLIMITED,2024-01-01\n" +
            "R2,Legacy Label Row,Female,Married,Full-Time,Unlimited Term,2024-01-01\n" +
            "R3,Arabic Row,ذكر,متزوج,دوام كامل,غير محدد المدة,2024-01-01\n" +
            "R4,Unknown Row,Prefer not to say,,Seasonal,,2024-01-01\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var rows = await db.Employees.Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.EmployeeCode);
        rows["R1"].Gender.Should().Be("FEMALE");
        rows["R2"].Gender.Should().Be("FEMALE", "the old label 'Female' is recognised even after the rename to 'Woman'");
        rows["R3"].Gender.Should().Be("MALE");
        rows["R4"].Gender.Should().Be("Prefer not to say", "an unknown value is imported as typed, never blocked or guessed");
        rows.Values.Where(e => e.EmployeeCode != "R4").Select(e => e.EmploymentType).Should().OnlyContain(v => v == "FULL_TIME");
        rows["R4"].EmploymentType.Should().Be("Seasonal");
        // MaritalStatus / ContractType have no tenant list here: the provisioned starter values still resolve them.
        new[] { rows["R1"], rows["R2"], rows["R3"] }.Select(e => e.MaritalStatus).Should().OnlyContain(v => v == "MARRIED");
        new[] { rows["R1"], rows["R2"], rows["R3"] }.Select(e => e.ContractType).Should().OnlyContain(v => v == "UNLIMITED");
    }
}
