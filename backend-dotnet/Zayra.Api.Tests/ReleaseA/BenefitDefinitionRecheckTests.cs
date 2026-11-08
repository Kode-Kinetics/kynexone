using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Second review of the benefit definition: an allowance that was ever confirmed never has its GL driver or end-of-service setting rewritten by a
/// plain edit (not even after an edit sent it back to pending); the policy class is not a payroll term; the committed periods are looked at once
/// more right before the save; names and categories refuse invisible and direction characters.
/// </summary>
public class BenefitDefinitionRecheckTests
{
    private static readonly DateOnly Aug1 = new(2026, 8, 1);

    private static CreateBenefitRequest CashRequest(string name, Action<CreateBenefitRequestBuilder>? more = null) => Contract(name, b =>
    {
        b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Allowance"; b.PolicyClass = "Contractual"; more?.Invoke(b);
    });

    private static UpdateBenefitRequest CashEdit(string version, Action<UpdateBenefitRequest>? unused = null, string? gl = null, bool? eosbIncluded = null,
        string? eosbCategory = null, string? policyClass = "Contractual", DateOnly? from = null) =>
        new(version, "Car", null)
        {
            Category = "Allowance", PolicyClass = policyClass, EffectiveFrom = from, EffectiveTo = null, GlDriverKey = gl, EosbIncluded = eosbIncluded, EosbExclusionCategory = eosbCategory,
        };

    // ══ A. re-pend, then retarget ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("gl")]
    [InlineData("eosb")]
    public async Task AnAllowanceThatWasEverConfirmed_NeverHasItsGlOrEosbRewrittenByAPlainEdit_NotEvenAfterAnEditSentItBackToPending(string term)
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car"), user: h.FinanceA, finance: true));
        // Step 1: HR changes the dates, which sends the confirmed allowance back to pending Finance.
        var step1 = Component(await h.Put("ALW_CAR", CashEdit(created.Definition!.Version, from: Bd.Nov1)));
        step1.CashAllowance!.FinancePending.Should().BeTrue();

        // Step 2: the same editor, now on a pending allowance, tries to retarget the expense.
        var step2 = await h.Put("ALW_CAR", CashEdit(step1.Definition!.Version, from: Bd.Nov1,
            gl: term == "gl" ? "EARN:CAR2" : null, eosbIncluded: term == "eosb" ? false : null, eosbCategory: term == "eosb" ? "Reimbursement" : null));

        (Status(step2), Error(step2)).Should().Be((409, "finance_correction_required"), Describe(step2));
        var row = (await h.Rows("ALW_CAR")).Single();
        (row.GlDriverKey, row.EosbIncluded).Should().Be((CarDriver, true));
    }

    [Fact]
    public async Task AnAllowanceThatWasNeverConfirmed_StillTakesGlAndEosbEdits()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car")));

        var edited = await h.Put("ALW_CAR", CashEdit(created.Definition!.Version, gl: "EARN:CAR2"));

        Status(edited).Should().Be(200, Describe(edited));
        (await h.Rows("ALW_CAR")).Single().GlDriverKey.Should().Be("EARN:CAR2");
    }

    // ══ B. the policy class is not a payroll term ═══════════════════════════════════════════════

    [Fact]
    public async Task ChangingOnlyThePolicyClass_OfAConfirmedAllowance_ByHr_DoesNotSendItBackToPending()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(CashRequest("Car"), user: h.FinanceA, finance: true));

        var edited = Component(await h.Put("ALW_CAR", CashEdit(created.Definition!.Version, policyClass: "Discretionary")));

        edited.CashAllowance!.FinancePending.Should().BeFalse();
        var row = (await h.Rows("ALW_CAR")).Single();
        (row.PolicyClass, row.CreatedBy, row.FinanceConfirmedByUserId).Should().Be(("Discretionary", (Guid?)h.FinanceA, (Guid?)h.FinanceA));
    }

    // ══ C. the committed periods are looked at once more right before the save ═════════════════

    private static async Task LockNovemberDuringTheRequest(Bd h) =>
        h.Service.BeforeDefinitionSaveHook = async () =>
        {
            await using var other = h.NewContext();
            other.PayrollRuns.Add(new PayrollRun { TenantId = h.Tid, CompanyId = h.Alpha.Id, Year = 2026, Month = 11, Status = "Locked" });
            await other.SaveChangesAsync();
            h.Service.BeforeDefinitionSaveHook = null;
        };

    [Fact]
    public async Task APayrollApprovalRacingAnEdit_IsCaught_ByTheLastLookBeforeTheSave()
    {
        await using var h = await Bd.Create();
        var created = Component(await h.Add(Contract("Parking", b => { b.Code = "PARK"; b.EffectiveFrom = Aug1; b.EffectiveTo = new DateOnly(2026, 9, 30); })));
        await LockNovemberDuringTheRequest(h);

        var result = await h.Put("BEN_PARK", new UpdateBenefitRequest(created.Definition!.Version, "Parking", null)
            { Category = null, PolicyClass = null, EffectiveFrom = Aug1, EffectiveTo = new DateOnly(2026, 12, 31) });

        (Status(result), Error(result)).Should().Be((409, "effective_in_locked_period"), Describe(result));
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single().EffectiveTo.Should().Be(new DateOnly(2026, 9, 30));
        (await h.AuditCount(EntitlementMatrixService.AuditBenefitUpdated)).Should().Be(0);
    }

    [Fact]
    public async Task APayrollApprovalRacingACreate_IsCaught_ByTheLastLookBeforeTheSave()
    {
        await using var h = await Bd.Create();
        await LockNovemberDuringTheRequest(h);

        var result = await h.Add(Contract("Parking", b => { b.Code = "PARK"; b.EffectiveFrom = Aug1; b.EffectiveTo = new DateOnly(2026, 12, 31); }));

        (Status(result), Error(result)).Should().Be((409, "effective_in_locked_period"), Describe(result));
        (await h.Rows("BEN_PARK")).Should().BeEmpty();
        (await h.AuditCount(EntitlementMatrixService.AuditBenefitAdded)).Should().Be(0);
    }

    // ══ D. invisible and direction characters ═══════════════════════════════════════════════════

    [Theory]
    [InlineData("\u200B")] // zero width space
    [InlineData("\u200C")] // zero width non-joiner
    [InlineData("\u200D")] // zero width joiner
    [InlineData("\u2060")] // word joiner
    [InlineData("\uFEFF")] // byte order mark
    [InlineData("\u200E")] // left-to-right mark
    [InlineData("\u200F")] // right-to-left mark
    [InlineData("\u061C")] // Arabic letter mark
    [InlineData("\u202E")] // right-to-left override
    public async Task AnInvisibleOrDirectionCharacter_IsRefused_InANameAndInACategory(string character)
    {
        await using var h = await Bd.Create();

        var inEnglishName = await h.Add(Contract($"Park{character}ing"));
        var inArabicName = await h.Add(Contract("Garage", b => b.NameAr = $"موقف{character}"));
        var inCategory = await h.Add(Contract("Spa", b => b.Category = $"Fac{character}ilities"));

        (Error(inEnglishName), Error(inArabicName), Error(inCategory)).Should().Be(("name_invalid", "name_invalid", "category_invalid"));
        (await h.Db.PayComponents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OrdinaryArabicAndEnglishText_StillPasses()
    {
        await using var h = await Bd.Create();

        var arabic = await h.Add(Contract("Garage", b => { b.NameAr = "موقف السيارات - المبنى (أ)"; b.Category = "مرافق وخدمات"; }));

        Status(arabic).Should().Be(200, Describe(arabic));
        Component(arabic).NameAr.Should().Be("موقف السيارات - المبنى (أ)");
    }
}
