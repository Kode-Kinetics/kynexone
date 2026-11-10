using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The security-review findings on the benefit definition (feature <c>benefit_definition_v1</c>): the locked-period guard compares the old window with
/// the new one; an omitted PUT field means "unchanged" and an explicit null means "clear"; the flag-off body is exactly the old one; a replay finds its
/// result before the code lookup and is refused when the body differs; scope is capped and strict; the flag is read inside the lock; names refuse
/// text-direction characters; a deactivated benefit stays listed so it can be switched on again.
/// </summary>
public class BenefitDefinitionReviewFixTests
{
    private static readonly JsonSerializerOptions Web = Bd.Web;
    private static readonly DateOnly Aug1 = new(2026, 8, 1);

    private static UpdateBenefitRequest FromJson(string json) => JsonSerializer.Deserialize<UpdateBenefitRequest>(json, Web)!;

    private static CreateBenefitRequest CashRequest(string name, Action<CreateBenefitRequestBuilder>? more = null) => Contract(name, b =>
    {
        b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Allowance"; b.PolicyClass = "Contractual"; more?.Invoke(b);
    });

    private static async Task LockNovember(Bd h)
    {
        h.Db.PayrollRuns.Add(new PayrollRun { TenantId = h.Tid, CompanyId = h.Alpha.Id, Year = 2026, Month = 11, Status = "Locked" });
        await h.Db.SaveChangesAsync();
    }

    // ══ 1. the locked-period guard compares the old window with the new one ═════════════════════

    public static TheoryData<string, DateOnly?, DateOnly?, bool?, int, string?> WindowEdits => new()
    {
        // scenario, new from, new to, new active, status, error — base window is 2026-08-01 .. 2027-01-31 ("gap": 2026-08-01 .. 2026-09-30), November 2026 is locked
        { "clearFrom", null, new DateOnly(2027, 1, 31), null, 409, "effective_in_locked_period" },
        { "startLater", new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 31), null, 409, "effective_in_locked_period" },
        { "startEarlier", new DateOnly(2026, 6, 1), new DateOnly(2027, 1, 31), null, 409, "effective_in_locked_period" },
        { "cutInsidePaidMonth", Aug1, new DateOnly(2026, 10, 31), null, 409, "effective_in_locked_period" },
        { "deactivateAlone", Aug1, new DateOnly(2027, 1, 31), false, 409, "effective_in_locked_period" },
        { "gap:extendAcrossPaidGap", Aug1, new DateOnly(2026, 12, 31), null, 409, "effective_in_locked_period" },
        { "extendEndFuture", Aug1, new DateOnly(2027, 3, 31), null, 200, null },
        { "cutEndFuture", Aug1, new DateOnly(2026, 12, 31), null, 200, null },
    };

    [Theory]
    [MemberData(nameof(WindowEdits))]
    public async Task ADateOrActiveEdit_IsRefused_WhenAnyMonthItChangesIsAtOrBeforeTheLatestCommittedPeriod(
        string scenario, DateOnly? to1, DateOnly? to2, bool? active, int status, string? error)
    {
        await using var h = await Bd.Create();
        var gap = scenario.StartsWith("gap:");
        var created = Component(await h.Add(Contract("Parking", b => { b.Code = "PARK"; b.EffectiveFrom = Aug1; b.EffectiveTo = gap ? new DateOnly(2026, 9, 30) : new DateOnly(2027, 1, 31); })));
        await LockNovember(h);

        var result = await h.Put("BEN_PARK", new UpdateBenefitRequest(created.Definition!.Version, "Parking", null)
            { Category = null, PolicyClass = null, EffectiveFrom = to1, EffectiveTo = to2, IsActive = active });

        (Status(result), Error(result)).Should().Be((status, error), Describe(result));
        var row = (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single();
        if (status != 200) (row.EffectiveFrom, row.EffectiveTo, row.IsActive).Should().Be(((DateOnly?)Aug1, gap ? new DateOnly(2026, 9, 30) : new DateOnly(2027, 1, 31), true));
        else (row.EffectiveFrom, row.EffectiveTo).Should().Be(((DateOnly?)to1, to2));
    }

    [Fact]
    public async Task SwitchingACashAllowanceOff_IsLockChecked_BeforeItWouldAbortAPayrollRun()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car", b => b.EffectiveFrom = Aug1), user: h.FinanceA, finance: true));
        await LockNovember(h);

        var off = await h.Put("ALW_CAR", new UpdateBenefitRequest(created.Definition!.Version, "Car", null)
            { Category = "Allowance", PolicyClass = "Contractual", EffectiveFrom = Aug1, EffectiveTo = null, GlDriverKey = CarDriver, IsActive = false });

        (Status(off), Error(off)).Should().Be((409, "effective_in_locked_period"));
        (await h.Rows("ALW_CAR")).Single().IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ACashAllowanceCreatedOpenToThePast_IsRefused_OnceAPeriodIsCommitted_ButAContractBenefitIsNot()
    {
        await using var h = await Bd.Create();
        await LockNovember(h);

        var cash = await h.Add(CashRequest("Car"));
        var contract = await h.Add(Contract("Parking"));

        (Status(cash), Error(cash)).Should().Be((409, "effective_in_locked_period"));
        Status(contract).Should().Be(200, "a non-paying contract benefit changes no paid month");
    }

    // ══ 2. omitted means unchanged; an explicit null clears ═════════════════════════════════════

    private static async Task<(Bd H, string Version)> ConfirmedCar()
    {
        var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car", b => { b.EffectiveFrom = Bd.Nov1; b.EffectiveTo = Bd.Oct31Next; }), user: h.FinanceA, finance: true));
        return (h, created.Definition!.Version);
    }

    [Fact]
    public async Task ARenameOnlyRequest_ByHr_ChangesOnlyTheName_AndLeavesAConfirmedAllowanceConfirmed()
    {
        var (h, version) = await ConfirmedCar();
        await using var _ = h;

        var edited = Component(await h.Put("ALW_CAR", FromJson($$"""{"expectedVersion":"{{version}}","nameEn":"Company car"}""")));

        edited.NameEn.Should().Be("Company car");
        edited.CashAllowance!.FinancePending.Should().BeFalse();
        var row = (await h.Rows("ALW_CAR")).Single();
        (row.NameAr, row.BenefitCategory, row.PolicyClass, row.EffectiveFrom, row.EffectiveTo, row.GlDriverKey, row.IsActive, row.CreatedBy)
            .Should().Be(("Car", "Allowance", "Contractual", (DateOnly?)Bd.Nov1, (DateOnly?)Bd.Oct31Next, CarDriver, true, (Guid?)h.FinanceA));
        row.FinanceConfirmedByUserId.Should().Be(h.FinanceA);
    }

    [Fact]
    public async Task AnExplicitNull_ClearsTheCategory_WhileOmittedFieldsKeepTheirValues()
    {
        var (h, version) = await ConfirmedCar();
        await using var _ = h;

        Component(await h.Put("ALW_CAR", FromJson($$"""{"expectedVersion":"{{version}}","nameEn":"Car","category":null}""")));

        var row = (await h.Rows("ALW_CAR")).Single();
        (row.BenefitCategory, row.PolicyClass, row.EffectiveFrom, row.EffectiveTo).Should().Be(((string?)null, "Contractual", (DateOnly?)Bd.Nov1, (DateOnly?)Bd.Oct31Next));
    }

    [Fact]
    public async Task ARenameThatChangesTheAllowancesCautions_ByHr_PutsItBackToPendingFinance()
    {
        var (h, version) = await ConfirmedCar();
        await using var _ = h;

        // "Phone" reads like a reimbursement, so with end of service included the caution changes: Finance looks again.
        var edited = Component(await h.Put("ALW_CAR", FromJson($$"""{"expectedVersion":"{{version}}","nameEn":"Phone allowance"}""")));

        edited.CashAllowance!.FinancePending.Should().BeTrue();
        (await h.Rows("ALW_CAR")).Single().CreatedBy.Should().Be(h.Hr);
    }

    // ══ 3. flag off = exactly the old body ══════════════════════════════════════════════════════

    [Fact]
    public async Task WithTheFlagOff_AnEditIsRefusedForTheFlag_NotForItsUnknownFields()
    {
        var (h, version) = await ConfirmedCar();
        await using var _ = h;
        await h.SetFlag(false);

        var result = await h.Put("ALW_CAR", FromJson($$"""{"expectedVersion":"{{version}}","nameEn":"Car","bogus":1}"""));

        Error(result).Should().Be("feature_not_enabled");
    }

    // ══ 5. replay ═══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ARetryAfterARename_ReturnsTheStoredResult_NotNotFound()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(Contract("Parking", b => b.Code = "PARK")));
        var requestId = Guid.NewGuid();
        UpdateBenefitRequest Rename() => new(created.Definition!.Version, "Parking", null) { Code = "PARKING_BAY", Category = null, PolicyClass = null, EffectiveFrom = null, EffectiveTo = null, RequestId = requestId };
        var first = Component(await h.Put("BEN_PARK", Rename()));

        var retry = await h.Put("BEN_PARK", Rename());

        Component(retry).Code.Should().Be("BEN_PARKING_BAY");
        Component(retry).Definition!.Version.Should().Be(first.Definition!.Version);
        (await h.AuditCount(EntitlementMatrixService.AuditBenefitUpdated)).Should().Be(1);
    }

    [Fact]
    public async Task AReusedRequestId_WithADifferentBody_IsRefused_ForEditAndForCreate()
    {
        await using var h = await Bd.Create();
        var requestId = Guid.NewGuid();
        var created = Component(await h.Add(Contract("Parking", b => { b.Code = "PARK"; b.RequestId = requestId; })));

        var createAgain = await h.Add(Contract("Something else", b => { b.Code = "ELSE"; b.RequestId = requestId; }));
        var editId = Guid.NewGuid();
        Component(await h.Put("BEN_PARK", new UpdateBenefitRequest(created.Definition!.Version, "Parking", null) { Category = "One", PolicyClass = null, EffectiveFrom = null, EffectiveTo = null, RequestId = editId }));
        var editAgain = await h.Put("BEN_PARK", new UpdateBenefitRequest(created.Definition.Version, "Parking", null) { Category = "Two", PolicyClass = null, EffectiveFrom = null, EffectiveTo = null, RequestId = editId });

        (Status(createAgain), Error(createAgain)).Should().Be((422, "request_id_reused"));
        (Status(editAgain), Error(editAgain)).Should().Be((422, "request_id_reused"));
        (await h.Rows("BEN_ELSE")).Should().BeEmpty();
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single().BenefitCategory.Should().Be("One");
    }

    // ══ 6. scope ════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AMisspeltScopeKey_IsRefused_NotReadAsAllCompanies()
    {
        await using var h = await Bd.Create();
        var req = JsonSerializer.Deserialize<CreateBenefitRequest>(
            $$"""{"nameEn":"Parking","effectiveFrom":"2026-11-01","scope":{"cmpanyIds":["{{h.Alpha.Id}}"] } }""", Web)!;

        var result = await h.Add(req);

        (Status(result), Error(result)).Should().Be((400, "unrecognised_field"));
        (await h.Rows("BEN_PARKING")).Should().BeEmpty();
    }

    [Fact]
    public async Task AScopeListingMoreCompaniesThanTheTenantHas_IsRefusedBeforeAnyLookup()
    {
        await using var h = await Bd.Create();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();

        var result = await h.Add(Contract("Parking", b => { b.EffectiveFrom = Bd.Nov1; b.Scope = new BenefitScopeInput(ids); }));

        (Status(result), Error(result)).Should().Be((400, "scope_too_large"));
    }

    // ══ 7. the flag is read inside the lock ═════════════════════════════════════════════════════

    [Fact]
    public async Task TheFlagIsReadInsideTheLock_ForCreateAndForEdit()
    {
        await using var h = await Bd.Create();
        h.Service.AfterLockHook = () => h.SetFlag(false);

        var create = await h.Add(Contract("Parking", b => b.Category = "Facilities"));

        (Status(create), Error(create)).Should().Be((403, "feature_not_enabled"));
        h.Service.AfterLockHook = null;
        await h.SetFlag(true);
        var created = Component(await h.Add(Contract("Parking", b => b.Category = "Facilities")));
        h.Service.AfterLockHook = () => h.SetFlag(false);

        var edit = await h.Put("BEN_PARKING", new UpdateBenefitRequest(created.Definition!.Version, "Parking", null) { Category = "Other", PolicyClass = null, EffectiveFrom = null, EffectiveTo = null });

        (Status(edit), Error(edit)).Should().Be((403, "feature_not_enabled"));
    }

    // ══ 8. names refuse text-direction characters ═══════════════════════════════════════════════

    [Fact]
    public async Task ANameWithATextDirectionOverride_IsRefused_OnCreateAndEdit_ButOnlyWithTheFlagOn()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(Contract("Parking")));

        var create = await h.Add(Contract("Park\u202Eing"));
        var edit = await h.Put("BEN_PARKING", new UpdateBenefitRequest(created.Definition!.Version, "Park\u202Eing", null) { Category = null, PolicyClass = null, EffectiveFrom = null, EffectiveTo = null });
        var arabic = await h.Add(Contract("Garage", b => b.NameAr = "موقف السيارات"));

        (Error(create), Error(edit)).Should().Be(("name_invalid", "name_invalid"));
        Status(arabic).Should().Be(200, "ordinary Arabic letters and spaces are fine");
        await h.SetFlag(false);
        Status(await h.Add(Contract("Park\u202Eing"))).Should().Be(200, "the old body behaves as before");
    }
}
