using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Infrastructure.Entitlements;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Separation of duties on a monthly cash allowance (owner rules: HR makes, Finance approves; the subject never decides; one decider per request;
/// nobody confirms what they made or changed; no exemption for a company with a single admin). Whoever adds an allowance, and whoever edits, corrects or
/// widens it after Finance last confirmed it, can never be the one who confirms it — whatever Finance permission they hold. Any change to a confirmed
/// allowance's money terms, by anyone, sends it back to pending for another Finance user.
/// </summary>
public class AllowanceFinanceSeparationTests
{
    private static readonly DateOnly Dec1 = new(2026, 12, 1);

    private static CreateBenefitRequest Cash(string name, Action<CreateBenefitRequestBuilder>? more = null) => Contract(name, b =>
    {
        b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Allowance"; b.PolicyClass = "Contractual"; more?.Invoke(b);
    });

    private static (int? Status, string? Error) Outcome(IActionResult r) => (Status(r), Error(r));

    private static Task<IActionResult> Confirm(Bd h, Guid user, string code = "ALW_CAR", Guid[]? scope = null) =>
        h.As(user, finance: true, scope).ConfirmBenefit(h.Service, code, null, default);

    /// <summary>A group allowance HR added and another Finance user confirmed.</summary>
    private static async Task<string> ConfirmedGroupAllowanceAsync(Bd h, Guid? maker = null, bool makerIsFinance = false, Guid? checker = null)
    {
        var code = Component(await h.Add(Cash("Car"), user: maker ?? h.Hr, finance: makerIsFinance)).Code;
        Component(await Confirm(h, checker ?? h.FinanceA, code));
        return code;
    }

    private static async Task<string> VersionAsync(Bd h, string code, Guid? companyId = null) =>
        ((EntitlementMatrixDto)((OkObjectResult)await h.As(h.Hr).Matrix(h.Service, companyId, Bd.Today, default)).Value!)
            .Components.Single(c => c.Code == code).Definition!.Version;

    private static UpdateBenefitRequest MoveStart(string version, DateOnly from) => new(version, "Car", null) { EffectiveFrom = from };

    // ══ P1: widening ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WideningAConfirmedCompanyAllowance_ByAWidenerWhoHoldsFinance_SendsItBackToPending_AndTheWidenerCannotConfirmIt()
    {
        await using var h = await Bd.Create();
        var car = Component(await h.As(h.HrTwo, companyScope: [h.Alpha.Id]).AddBenefit(h.Service, Cash("Car", b => b.CompanyId = h.Alpha.Id), default));
        Component(await Confirm(h, h.FinanceA, car.Code, [h.Alpha.Id]));
        var version = await VersionAsync(h, car.Code, h.Alpha.Id);

        var widened = Component(await h.Put(car.Code, new UpdateBenefitRequest(version, "Car", null) { Scope = new BenefitScopeInput([h.Alpha.Id, h.Beta.Id, h.Gamma.Id]) },
            user: h.Hr, finance: true));

        widened.CashAllowance!.FinancePending.Should().BeTrue("the widener holds Finance, but nobody confirms what they changed");
        var row = (await h.Rows(car.Code)).Single();
        (row.CompanyId, row.FinanceConfirmedAtUtc, row.FinanceConfirmedByUserId, row.CreatedBy).Should().Be(((Guid?)null, (DateTime?)null, (Guid?)null, (Guid?)h.Hr));
        Outcome(await Confirm(h, h.Hr, car.Code)).Status.Should().Be(403);
        Status(await Confirm(h, h.FinanceB, car.Code)).Should200("another Finance user confirms it for every company");
    }

    // ══ P2: a Finance maker or editor never changes a confirmed allowance alone ═════════════════

    [Fact]
    public async Task AFinanceMaker_ChangingTheDatesOfTheirConfirmedAllowance_SendsItBackToPending_ForAnotherFinanceUser()
    {
        await using var h = await Bd.Create();
        var code = await ConfirmedGroupAllowanceAsync(h, maker: h.FinanceB, makerIsFinance: true, checker: h.FinanceA);

        var edited = Component(await h.Put(code, MoveStart(await VersionAsync(h, code), Bd.Nov1), user: h.FinanceB, finance: true));

        edited.CashAllowance!.FinancePending.Should().BeTrue();
        (await h.Rows(code)).Single().FinanceConfirmedAtUtc.Should().BeNull();
        Outcome(await Confirm(h, h.FinanceB, code)).Should().Be(((int?)403, "maker_cannot_confirm"));
        Status(await Confirm(h, h.FinanceA, code)).Should200("the earlier checker confirms the change");
    }

    [Fact]
    public async Task AFinanceUserWhoDidNotMakeIt_ChangingTheDates_StillSendsItBackToPending_AndCannotConfirmTheirOwnChange()
    {
        await using var h = await Bd.Create();
        var code = await ConfirmedGroupAllowanceAsync(h);

        Component(await h.Put(code, MoveStart(await VersionAsync(h, code), Bd.Nov1), user: h.FinanceB, finance: true)).CashAllowance!.FinancePending.Should().BeTrue();

        Outcome(await Confirm(h, h.FinanceB, code)).Should().Be(((int?)403, "maker_cannot_confirm"), "the person who changed it is the maker of the change");
        Outcome(await Confirm(h, h.Hr, code)).Should().Be(((int?)403, "maker_cannot_confirm"), "whoever added it never confirms it");
        Status(await Confirm(h, h.FinanceA, code)).Should200("a Finance user who neither made nor changed it");
    }

    [Fact]
    public async Task AFinanceCorrection_SendsTheNewVersionBackToPending_ShowsItAsWaiting_AndTheCorrectorCannotConfirmIt()
    {
        await using var h = await Bd.Create();
        var code = await ConfirmedGroupAllowanceAsync(h);

        var corrected = await h.As(h.FinanceA, finance: true).CorrectBenefit(h.Service, code, new CorrectAllowanceRequest(Dec1, "Re-route the expense", GlDriverKey: "EARN:CAR2"), default);

        Component(corrected).CashAllowance!.FinancePending.Should().BeTrue("the person who confirmed it changed it, so someone else confirms the change");
        var versions = await h.Rows(code);
        versions[0].FinanceConfirmedAtUtc.Should().NotBeNull("the months before the correction keep the confirmed terms");
        (versions[1].FinanceConfirmedAtUtc, versions[1].CreatedBy).Should().Be(((DateTime?)null, (Guid?)h.FinanceA));
        var shown = (await h.Read()).Components.Single(c => c.Code == code).CashAllowance!;
        (shown.FinancePending, shown.GlDriverKey).Should().Be((true, "EARN:CAR2"), "the grid shows the change waiting for Finance, so it can be found and confirmed");
        JsonDocument.Parse((await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitCorrected)).Metadata!)
            .RootElement.GetProperty("repended").GetBoolean().Should().BeTrue();

        Outcome(await Confirm(h, h.FinanceA, code)).Should().Be(((int?)403, "maker_cannot_confirm"));
        Outcome(await Confirm(h, h.Hr, code)).Should().Be(((int?)403, "maker_cannot_confirm"));
        Status(await Confirm(h, h.FinanceB, code)).Should200("another Finance user confirms the correction");
        (await h.Rows(code))[1].FinanceConfirmedByUserId.Should().Be(h.FinanceB);
        (await h.Read()).Components.Single(c => c.Code == code).CashAllowance!.GlDriverKey.Should().Be(CarDriver, "once confirmed, the grid speaks for the version in force today again");
    }

    // ══ P2: makership cannot be passed on ═══════════════════════════════════════════════════════

    [Fact]
    public async Task ACreatorWhoseAllowanceSomeoneElseEdited_StillCannotConfirmIt_AndIsToldInBothLanguages()
    {
        await using var h = await Bd.Create();
        var code = Component(await h.Add(Cash("Car"), user: h.Hr)).Code;
        Component(await h.Put(code, MoveStart(await VersionAsync(h, code), Bd.Nov1), user: h.HrTwo));
        (await h.Rows(code)).Single().CreatedBy.Should().Be(h.HrTwo, "the last editor of a pending allowance is its maker");

        var refused = await Confirm(h, h.Hr, code);

        Outcome(refused).Should().Be(((int?)403, "maker_cannot_confirm"));
        var body = JsonSerializer.SerializeToElement(((ObjectResult)refused).Value, Web);
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("messageAr").GetString().Should().MatchRegex("[؀-ۿ]");
        Outcome(await Confirm(h, h.HrTwo, code)).Status.Should().Be(403);
        (await h.Rows(code)).Single().FinanceConfirmedAtUtc.Should().BeNull();
        Status(await Confirm(h, h.FinanceA, code)).Should200("someone who neither made nor changed it");
    }

    [Fact]
    public async Task AnyoneWhoEditedItSinceTheLastConfirmation_CannotConfirmIt_EvenAfterTheMakerRoleMovedOn()
    {
        await using var h = await Bd.Create();
        var code = Component(await h.Add(Cash("Car"), user: h.Hr)).Code;
        Component(await h.Put(code, MoveStart(await VersionAsync(h, code), Bd.Nov1), user: h.HrTwo));
        Component(await h.Put(code, MoveStart(await VersionAsync(h, code), Bd.Nov1.AddMonths(1)), user: h.FinanceB, finance: true));

        Outcome(await Confirm(h, h.HrTwo, code)).Should().Be(((int?)403, "changer_cannot_confirm"), "HrTwo changed it, and FinanceB changing it after does not clear that");
        Outcome(await Confirm(h, h.FinanceB, code)).Should().Be(((int?)403, "maker_cannot_confirm"));
        Status(await Confirm(h, h.FinanceA, code)).Should200("");
    }

    // ══ P3: group Finance never confirms at creation ════════════════════════════════════════════

    [Fact]
    public async Task AGroupAllowanceAddedByAFinanceHolder_StaysPending_ForAnotherFinanceUser()
    {
        await using var h = await Bd.Create();

        var added = Component(await h.Add(Cash("Car"), user: h.FinanceA, finance: true));

        added.CashAllowance!.FinancePending.Should().BeTrue("nobody confirms what they made, at the group level too");
        (await h.Rows(added.Code)).Single().FinanceConfirmedAtUtc.Should().BeNull();
        Outcome(await Confirm(h, h.FinanceA, added.Code)).Should().Be(((int?)403, "maker_cannot_confirm"));
        Status(await Confirm(h, h.FinanceB, added.Code)).Should200("");
    }
}
