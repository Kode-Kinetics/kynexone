using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// PUT /api/entitlements/components/{code}: editing a customer-added benefit's definition. Optimistic concurrency (409 stale_definition), the same
/// permission and group scope as create, an audit row in the same save, replays by request id, and the cash-allowance rules: maker is not checker,
/// and a confirmed allowance's GL driver and end-of-service setting change only through Finance's correction.
/// </summary>
public class BenefitDefinitionEditTests
{
    private static readonly DateOnly Dec1 = new(2026, 12, 1);

    private static UpdateBenefitRequest Edit(string version, Action<EditBuilder>? more = null)
    {
        var b = new EditBuilder { Version = version };
        more?.Invoke(b);
        return b.Build();
    }

    private sealed class EditBuilder
    {
        public string? Version, NameEn = "Parking", NameAr, Code, Kind, Delivery, Category = "Facilities", PolicyClass = "Discretionary", GlDriverKey, EosbExclusionCategory, EosbExclusionReason;
        public DateOnly? EffectiveFrom = Bd.Nov1, EffectiveTo = Bd.Oct31Next;
        public bool? IsActive, EosbIncluded, RequiresEnrolment;
        public BenefitScopeInput? Scope;
        public Guid? RequestId;
        public UpdateBenefitRequest Build() => new(Version, NameEn, NameAr)
        {
            Code = Code, Kind = Kind, Delivery = Delivery, Category = Category, PolicyClass = PolicyClass, EffectiveFrom = EffectiveFrom, EffectiveTo = EffectiveTo,
            IsActive = IsActive, EosbIncluded = EosbIncluded, EosbExclusionCategory = EosbExclusionCategory, EosbExclusionReason = EosbExclusionReason,
            GlDriverKey = GlDriverKey, RequiresEnrolment = RequiresEnrolment, Scope = Scope, RequestId = RequestId,
        };
    }

    private static async Task<(Bd H, string Version)> WithParking(Action<CreateBenefitRequestBuilder>? more = null)
    {
        var h = await Bd.Create();
        var created = Component(await h.Add(Contract("Parking", b =>
        {
            b.Code = "PARK"; b.Category = "Facilities"; b.PolicyClass = "Discretionary"; b.EffectiveFrom = Bd.Nov1; b.EffectiveTo = Bd.Oct31Next; more?.Invoke(b);
        })));
        return (h, created.Definition!.Version);
    }

    private static CreateBenefitRequest CashRequest(string name, Action<CreateBenefitRequestBuilder>? more = null) => Contract(name, b =>
    {
        b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Allowance"; b.PolicyClass = "Contractual"; more?.Invoke(b);
    });

    // ══ the edit itself ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnEdit_ChangesTheEditableFields_BumpsTheVersion_AndIsAudited_WithBeforeAndAfter()
    {
        var (h, version) = await WithParking();
        await using var _ = h;

        var edited = Component(await h.Put("BEN_PARK", Edit(version, b =>
        {
            b.NameEn = "Staff parking"; b.NameAr = "موقف الموظفين"; b.Category = "Transport"; b.PolicyClass = "Contractual"; b.EffectiveTo = new DateOnly(2028, 3, 31); b.RequestId = Guid.NewGuid();
        })));

        edited.NameEn.Should().Be("Staff parking");
        var row = (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single();
        (row.NameEn, row.NameAr, row.BenefitCategory, row.PolicyClass, row.EffectiveFrom, row.EffectiveTo, row.UpdatedBy)
            .Should().Be(("Staff parking", "موقف الموظفين", "Transport", "Contractual", (DateOnly?)Bd.Nov1, (DateOnly?)new DateOnly(2028, 3, 31), (Guid?)h.Hr));
        edited.Definition!.Version.Should().NotBe(version);
        var audit = await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitUpdated);
        (audit.UserId, audit.EntityId).Should().Be(((Guid?)h.Hr, row.Id.ToString()));
        var meta = JsonDocument.Parse(audit.Metadata!).RootElement;
        meta.GetProperty("before").GetProperty("category").GetString().Should().Be("Facilities");
        meta.GetProperty("after").GetProperty("category").GetString().Should().Be("Transport");
        meta.GetProperty("after").GetProperty("policyClass").GetString().Should().Be("Contractual");
    }

    [Fact]
    public async Task AnEdit_RaisesAnETag_AndTheControllerRepliesWithIt()
    {
        var (h, version) = await WithParking();
        await using var _ = h;
        var controller = h.As(h.Hr);

        var result = await controller.UpdateBenefit(h.Service, "BEN_PARK", Edit(version, b => b.Category = "Transport"), default);

        var def = Component(result).Definition!;
        controller.Response.Headers.ETag.ToString().Should().Be($"\"{def.Version}\"");
    }

    [Fact]
    public async Task AStaleVersion_IsRefusedWithStaleDefinition_AndNothingChanges()
    {
        var (h, version) = await WithParking();
        await using var _ = h;
        Component(await h.Put("BEN_PARK", Edit(version, b => b.Category = "First")));
        var auditBefore = await h.Db.AuditLogs.CountAsync();

        var second = await h.Put("BEN_PARK", Edit(version, b => b.Category = "Second"), user: h.HrTwo);

        (Status(second), Error(second)).Should().Be((409, "stale_definition"));
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single().BenefitCategory.Should().Be("First");
        (await h.Db.AuditLogs.CountAsync()).Should().Be(auditBefore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public async Task AMissingOrUnreadableVersion_IsRefused(string? version)
    {
        var (h, current) = await WithParking();
        await using var _ = h;

        var result = await h.Put("BEN_PARK", Edit(version!));

        (Status(result), Error(result)).Should().Be(string.IsNullOrEmpty(version) ? (400, "expected_version_required") : (409, "stale_definition"));
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single().BenefitCategory.Should().Be("Facilities");
        current.Should().NotBeNull();
    }

    [Fact]
    public async Task AReplayOfAnEdit_BySameRequestId_ReturnsTheCurrentState_EvenThoughTheVersionMovedOn()
    {
        var (h, version) = await WithParking();
        await using var _ = h;
        var requestId = Guid.NewGuid();
        var first = Component(await h.Put("BEN_PARK", Edit(version, b => { b.Category = "Transport"; b.RequestId = requestId; })));

        var replay = Component(await h.Put("BEN_PARK", Edit(version, b => { b.Category = "Transport"; b.RequestId = requestId; })));

        replay.Definition!.Version.Should().Be(first.Definition!.Version);
        (await h.AuditCount(EntitlementMatrixService.AuditBenefitUpdated)).Should().Be(1);
    }

    [Fact]
    public async Task TwoEditsWithTheSameVersion_OneWins_TheOtherIsStale()
    {
        var (h, version) = await WithParking();
        await using var _ = h;

        var one = await h.Put("BEN_PARK", Edit(version, b => b.Category = "One"));
        var two = await h.Put("BEN_PARK", Edit(version, b => b.Category = "Two"), user: h.HrTwo);

        (Status(one), Status(two), Error(two)).Should().Be((200, 409, "stale_definition"));
    }

    [Fact]
    public async Task AnEditTakesTheTenantMatrixLock()
    {
        var (h, version) = await WithParking();
        await using var _ = h;
        var locks = new List<(string Scope, Guid TenantId, Guid AggregateId)>();
        Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.Recorder.Value = locks;

        Component(await h.Put("BEN_PARK", Edit(version, b => b.Category = "Transport")));

        locks.Should().Contain((EntitlementMatrixService.LockScope, h.Tid, h.Tid));
    }

    // ══ what can and cannot change ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TheCode_CanBeRenamedWhileNothingUsesIt_AndIsLockedOnceAGradeValueExists()
    {
        var (h, version) = await WithParking();
        await using var _ = h;

        var renamed = Component(await h.Put("BEN_PARK", Edit(version, b => b.Code = "PARKING_BAY")));
        renamed.Code.Should().Be("BEN_PARKING_BAY");
        (await h.Rows("BEN_PARK")).Should().BeEmpty();
        Status(await h.PublishGroup(Bd.Nov1, Amount(h.G[1], "BEN_PARKING_BAY", 100m))).Should().Be(200);
        var inMatrix = (await h.Read(Bd.Nov1.AddDays(3))).Components.Single(c => c.Code == "BEN_PARKING_BAY");
        inMatrix.Definition!.CodeLocked.Should().BeTrue();

        var again = await h.Put("BEN_PARKING_BAY", Edit(inMatrix.Definition.Version, b => b.Code = "OTHER"));
        (Status(again), Error(again)).Should().Be((409, "code_locked"));
    }

    [Fact]
    public async Task ARenameToATakenOrReservedOrInvalidCode_IsRefused()
    {
        var (h, version) = await WithParking();
        await using var _ = h;
        Component(await h.Add(Contract("Gym", b => b.Code = "GYM")));

        foreach (var (code, expected) in new[] { ("GYM", "code_taken"), ("GOSI_X", "code_reserved"), ("1X", "code_invalid") })
        {
            var result = await h.Put("BEN_PARK", Edit(version, b => b.Code = code));
            (Status(result) is 400 or 409, Error(result)).Should().Be((true, expected), Describe(result));
        }
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Should().ContainSingle();
    }

    [Fact]
    public async Task TheKindAndDelivery_CannotChange_AndTheScopeChangesInTheOfferings()
    {
        var (h, version) = await WithParking();
        await using var _ = h;

        var kind = await h.Put("BEN_PARK", Edit(version, b => b.Kind = "Cash"));
        var delivery = await h.Put("BEN_PARK", Edit(version, b => b.Delivery = "SalaryAllowance"));
        var scope = await h.Put("BEN_PARK", Edit(version, b => b.Scope = new BenefitScopeInput([h.Alpha.Id])));
        var sameScope = await h.Put("BEN_PARK", Edit(version, b => b.Scope = new BenefitScopeInput(null)));

        (Error(kind), Error(delivery), Error(scope)).Should().Be(("kind_immutable", "kind_immutable", "scope_change_uses_offerings"));
        Status(sameScope).Should().Be(200, "restating the current scope is not a change");
    }

    [Fact]
    public async Task EnrolmentCannotBeSwitchedOn_AndAnUnknownFieldIsRefused()
    {
        var (h, version) = await WithParking();
        await using var _ = h;

        var enrol = await h.Put("BEN_PARK", Edit(version, b => b.RequiresEnrolment = true));
        var unknown = await h.Put("BEN_PARK", JsonSerializer.Deserialize<UpdateBenefitRequest>($$"""{"expectedVersion":"{{version}}","nameEn":"Parking","tenantId":"{{Guid.NewGuid()}}","isDeleted":true}""", Web)!);

        (Error(enrol), Error(unknown)).Should().Be(("delivery_not_supported", "unrecognised_field"));
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single().IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task AnInactiveBenefit_StaysListedWithItsVersion_ButIsNotOffered_AndTakesNoNewValues_UntilReactivated()
    {
        var (h, version) = await WithParking();
        await using var _ = h;

        var off = Component(await h.Put("BEN_PARK", Edit(version, b => b.IsActive = false)));
        // Still listed (with its version) so it can be switched on again, but not offered: no gaps, no new values.
        var matrix = await h.Read(Bd.Nov1.AddDays(3));
        var listed = matrix.Components.Single(c => c.Code == "BEN_PARK");
        (listed.Definition!.IsActive, listed.Definition.Version).Should().Be((false, off.Definition!.Version));
        matrix.Offerings.Single(o => o.ComponentCode == "BEN_PARK").Mode.Should().Be(EntitlementMatrixService.Modes.NotInEffect);
        matrix.Gaps.Should().NotContain(g => g.ComponentCode == "BEN_PARK");
        Status(await h.PublishGroup(Bd.Nov1, Amount(h.G[1], "BEN_PARK", 100m))).Should().Be(400);

        Component(await h.Put("BEN_PARK", Edit(listed.Definition.Version, b => b.IsActive = true)));
        (await h.Read(Bd.Nov1.AddDays(3))).Offerings.Single(o => o.ComponentCode == "BEN_PARK").Offered.Should().BeTrue();
    }

    [Fact]
    public async Task EditingACatalogueOrUnknownCode_IsRefused()
    {
        await using var h = await Bd.Create();

        var catalogue = await h.Put("HOUSING", Edit("1"));
        var unknown = await h.Put("BEN_NOPE", Edit("1"));
        var loan = await h.Put("LOAN_HOUSING_ADVANCE", Edit("1"));

        (Status(catalogue), Error(catalogue)).Should().Be((409, "built_in_benefit"));
        (Status(unknown), Error(unknown)).Should().Be((404, "unknown_component"));
        (Status(loan), Error(loan)).Should().Be((409, "built_in_benefit"));
    }

    [Fact]
    public async Task AnEditWithTheFlagOff_IsRefused_AndChangesNothing()
    {
        var (h, version) = await WithParking();
        await using var _ = h;
        await h.SetFlag(false);

        var result = await h.Put("BEN_PARK", Edit(version, b => b.Category = "Transport"));

        (Status(result), Error(result)).Should().Be((403, "feature_not_enabled"));
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single().BenefitCategory.Should().Be("Facilities");
    }

    [Fact]
    public async Task ADateEdit_FollowsTheSameWindowRulesAsCreate_IncludingTheLockedPeriod()
    {
        var (h, version) = await WithParking();
        await using var _ = h;
        h.Db.PayrollRuns.Add(new PayrollRun { TenantId = h.Tid, CompanyId = h.Alpha.Id, Year = 2026, Month = 11, Status = "Locked" });
        await h.Db.SaveChangesAsync();

        var intoLocked = await h.Put("BEN_PARK", Edit(version, b => b.EffectiveFrom = new DateOnly(2026, 10, 1)));
        var notMonthStart = await h.Put("BEN_PARK", Edit(version, b => b.EffectiveFrom = new DateOnly(2026, 12, 2)));
        var endBeforeLocked = await h.Put("BEN_PARK", Edit(version, b => { b.EffectiveFrom = null; b.EffectiveTo = new DateOnly(2026, 10, 31); }));

        (Error(intoLocked), Error(notMonthStart), Error(endBeforeLocked)).Should().Be(("effective_in_locked_period", "effective_from_not_month_start", "effective_in_locked_period"));
        var unchangedDates = await h.Put("BEN_PARK", Edit(version, b => b.Category = "Transport"));
        Status(unchangedDates).Should().Be(200, "an untouched date is not re-checked against the lock");
    }

    // ══ tenant isolation, scope and authorization ═══════════════════════════════════════════════

    [Fact]
    public async Task AnotherTenant_CannotSeeEditOrProbe_ACode_ItDoesNotOwn()
    {
        var (h, version) = await WithParking();
        await using var _ = h;
        var other = Guid.NewGuid();
        h.Db.TenantFeatureFlags.AddRange(new TenantFeatureFlag { TenantId = other, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true },
            new TenantFeatureFlag { TenantId = other, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = true });
        await h.Db.SaveChangesAsync();
        var intruder = h.As(Guid.NewGuid(), tenant: other);

        var edit = await intruder.UpdateBenefit(h.Service, "BEN_PARK", Edit(version, b => b.Category = "Hijacked"), default);
        var missing = await intruder.UpdateBenefit(h.Service, "BEN_NEVER_EXISTED", Edit(version), default);
        var remove = await intruder.RemoveBenefit(h.Service, "BEN_PARK", default);

        (Status(edit), Error(edit)).Should().Be((404, "unknown_component"));
        (Status(missing), Error(missing)).Should().Be((404, "unknown_component"), "an existing foreign code is indistinguishable from a missing one");
        Status(remove).Should().Be(404);
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single().BenefitCategory.Should().Be("Facilities");
        // The same code is free in the other tenant.
        var theirs = Component(await intruder.AddBenefit(h.Service, Contract("Parking", b => b.Code = "PARK"), default));
        theirs.Code.Should().Be("BEN_PARK");
        (await h.Db.PayComponents.CountAsync(p => p.Code == "BEN_PARK" && p.CompanyId == null)).Should().Be(2);
    }

    [Fact]
    public async Task ACompanyScopedAdmin_CannotEdit()
    {
        var (h, version) = await WithParking();
        await using var _ = h;

        var result = await h.As(h.Hr, companyScope: [h.Alpha.Id]).UpdateBenefit(h.Service, "BEN_PARK", Edit(version, b => b.Category = "x"), default);

        (Status(result), Error(result)).Should().Be((403, "group_scope_required"));
    }

    // ══ cash allowances: maker is not checker ═══════════════════════════════════════════════════

    [Fact]
    public async Task APendingCashAllowance_CanBeEditedByItsMaker_WhoCanStillNeverConfirmIt()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car")));

        var edited = Component(await h.Put("ALW_CAR", Edit(created.Definition!.Version, b =>
        {
            b.NameEn = "Car"; b.Category = "Vehicle"; b.PolicyClass = "Contractual"; b.GlDriverKey = "EARN:CAR2"; b.EffectiveFrom = null; b.EffectiveTo = null;
        })));

        edited.CashAllowance!.GlDriverKey.Should().Be("EARN:CAR2");
        edited.CashAllowance.FinancePending.Should().BeTrue();
        Error(await h.As(h.Hr, finance: true).ConfirmBenefit(h.Service, "ALW_CAR", null, default)).Should().Be("maker_cannot_confirm");
        Error(await h.As(h.FinanceA, finance: true).ConfirmBenefit(h.Service, "ALW_CAR", null, default)).Should().BeNull();
    }

    [Fact]
    public async Task APendingAllowance_EditedByAnotherPerson_MakesThemTheMaker_SoTheyCannotConfirmWhatTheyChanged()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car")));

        Component(await h.Put("ALW_CAR", Edit(created.Definition!.Version, b => { b.GlDriverKey = "EARN:CAR2"; b.EffectiveFrom = null; b.EffectiveTo = null; }), user: h.FinanceA, finance: true));

        (await h.Rows("ALW_CAR")).Single().CreatedBy.Should().Be(h.FinanceA);
        Error(await h.As(h.FinanceA, finance: true).ConfirmBenefit(h.Service, "ALW_CAR", null, default)).Should().Be("maker_cannot_confirm");
        Error(await h.As(h.FinanceB, finance: true).ConfirmBenefit(h.Service, "ALW_CAR", null, default)).Should().BeNull();
    }

    [Fact]
    public async Task AConfirmedAllowance_RefusesGlAndEndOfServiceEdits_ForEveryone_PointingToFinanceCorrection()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car"), user: h.FinanceA, finance: true));
        var version = created.Definition!.Version;

        var byHr = await h.Put("ALW_CAR", Edit(version, b => { b.GlDriverKey = "EARN:CAR2"; b.EffectiveFrom = null; b.EffectiveTo = null; }));
        var byFinance = await h.Put("ALW_CAR", Edit(version, b => { b.GlDriverKey = "EARN:CAR2"; b.EffectiveFrom = null; b.EffectiveTo = null; }), user: h.FinanceB, finance: true);
        var eosb = await h.Put("ALW_CAR", Edit(version, b => { b.EosbIncluded = false; b.EosbExclusionCategory = "Reimbursement"; b.EffectiveFrom = null; b.EffectiveTo = null; }), user: h.FinanceB, finance: true);

        (Error(byHr), Error(byFinance), Error(eosb)).Should().Be(("finance_correction_required", "finance_correction_required", "finance_correction_required"));
        var row = (await h.Rows("ALW_CAR")).Single();
        (row.GlDriverKey, row.EosbIncluded, row.FinanceConfirmedAtUtc != null).Should().Be((CarDriver, true, true));
    }

    [Fact]
    public async Task ChangingTheDatesOrPolicyClassOfAConfirmedAllowance_AsHr_RependsIt_AndMakesHrTheMaker()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car"), user: h.FinanceA, finance: true));

        var edited = Component(await h.Put("ALW_CAR", Edit(created.Definition!.Version, b =>
        {
            b.GlDriverKey = CarDriver; b.PolicyClass = "Discretionary"; b.EffectiveFrom = Bd.Nov1; b.EffectiveTo = null; b.Category = "Allowance";
        }), user: h.Hr));

        edited.CashAllowance!.FinancePending.Should().BeTrue("a money term changed by someone without the Finance permission needs Finance again");
        var row = (await h.Rows("ALW_CAR")).Single();
        (row.PolicyClass, row.CreatedBy, row.FinanceConfirmedAtUtc, row.FinanceConfirmedByUserId).Should().Be(("Discretionary", (Guid?)h.Hr, (DateTime?)null, (Guid?)null));
        var audit = await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitUpdated);
        JsonDocument.Parse(audit.Metadata!).RootElement.GetProperty("repended").GetBoolean().Should().BeTrue();
        Error(await h.As(h.Hr, finance: true).ConfirmBenefit(h.Service, "ALW_CAR", null, default)).Should().Be("maker_cannot_confirm");
        Error(await h.As(h.FinanceA, finance: true).ConfirmBenefit(h.Service, "ALW_CAR", null, default)).Should().BeNull("a different Finance approver can confirm");
    }

    [Fact]
    public async Task ChangingTheDates_AsFinance_KeepsTheAllowanceConfirmed()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car"), user: h.FinanceA, finance: true));

        var edited = Component(await h.Put("ALW_CAR", Edit(created.Definition!.Version, b =>
        {
            b.GlDriverKey = CarDriver; b.PolicyClass = "Contractual"; b.EffectiveFrom = Bd.Nov1; b.EffectiveTo = Bd.Oct31Next; b.Category = "Allowance";
        }), user: h.FinanceB, finance: true));

        edited.CashAllowance!.FinancePending.Should().BeFalse();
        var row = (await h.Rows("ALW_CAR")).Single();
        row.EffectiveFrom.Should().Be(Bd.Nov1);
        row.CreatedBy.Should().Be(h.FinanceA, "a Finance edit of a confirmed allowance keeps its original maker");
    }

    [Fact]
    public async Task OnlyNamesAndCategory_OfAConfirmedAllowance_AreEditableByHr_WithoutRepending()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car"), user: h.FinanceA, finance: true));

        var edited = Component(await h.Put("ALW_CAR", Edit(created.Definition!.Version, b =>
        {
            b.NameEn = "Company car"; b.Category = "Vehicle"; b.PolicyClass = "Contractual"; b.GlDriverKey = CarDriver; b.EffectiveFrom = null; b.EffectiveTo = null;
        })));

        edited.NameEn.Should().Be("Company car");
        edited.CashAllowance!.FinancePending.Should().BeFalse();
        (await h.Rows("ALW_CAR")).Single().BenefitCategory.Should().Be("Vehicle");
    }

    [Fact]
    public async Task AFinanceCorrection_CarriesTheDefinitionToTheNewVersion_AndTheMatrixStillShowsIt()
    {
        await using var h = await Bd.Create();
        Component(await h.Add(CashRequest("Car"), user: h.FinanceA, finance: true));

        var corrected = await h.As(h.FinanceB, finance: true).CorrectBenefit(h.Service, "ALW_CAR", new CorrectAllowanceRequest(Dec1, "Re-route the expense", GlDriverKey: "EARN:CAR2"), default);

        Status(corrected).Should().Be(200, Describe(corrected));
        var versions = await h.Rows("ALW_CAR", tenantWideOnly: true);
        versions.Should().HaveCount(2);
        versions.Should().OnlyContain(v => v.BenefitCategory == "Allowance" && v.PolicyClass == "Contractual" && v.RequiresEnrolment == false);
        var shown = (await h.Read(Dec1.AddDays(2))).Components.Single(c => c.Code == "ALW_CAR");
        // The grid speaks for the version in force today (the correction starts in December); the definition fields are the same on both.
        (shown.Definition!.Category, shown.Definition.PolicyClass, shown.CashAllowance!.GlDriverKey).Should().Be(("Allowance", "Contractual", CarDriver));
        versions.Last().GlDriverKey.Should().Be("EARN:CAR2");
    }

    [Fact]
    public async Task ACashAllowanceCode_CannotBeRenamed()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car")));

        var result = await h.Put("ALW_CAR", Edit(created.Definition!.Version, b => { b.Code = "VEHICLE"; b.GlDriverKey = CarDriver; b.EffectiveFrom = null; b.EffectiveTo = null; }));

        (Status(result), Error(result)).Should().Be((409, "code_locked"));
    }
}
