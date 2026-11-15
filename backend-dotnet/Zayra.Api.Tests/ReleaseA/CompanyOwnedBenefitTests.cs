using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A company's own benefits (owner decision 2026-11-15): an admin with access to one company adds a contract benefit or a monthly cash allowance that
/// belongs to that company only. It is stored as that company's own <c>pay_components</c> row (company_id = the company, is_offered = true), so every
/// other company — its grid, its resolver, its salaries and payroll — never sees it, and a request about it from another company is answered exactly
/// as one about a benefit that does not exist. Group HR keeps the group-wide add, and can widen a company's benefit to more companies, after which it
/// is the group's and the company admin can no longer edit or remove it.
/// </summary>
public class CompanyOwnedBenefitTests
{
    private static readonly DateOnly Nov1 = Bd.Nov1;
    private static readonly JsonSerializerOptions Web = Bd.Web;

    private static EntitlementMatrixController AlphaAdmin(Bd h, bool finance = false) => h.As(h.HrTwo, finance, [h.Alpha.Id]);
    private static EntitlementMatrixController BetaAdmin(Bd h, bool finance = false) => h.As(h.FinanceB, finance, [h.Beta.Id]);

    private static CreateBenefitRequest Own(Guid companyId, string name, Action<CreateBenefitRequestBuilder>? more = null) =>
        Contract(name, b => { b.CompanyId = companyId; more?.Invoke(b); });

    private static CreateBenefitRequest OwnCash(Guid companyId, string name, Action<CreateBenefitRequestBuilder>? more = null) =>
        Contract(name, b => { b.CompanyId = companyId; b.Kind = BenefitKinds.Cash; b.GlDriverKey = CarDriver; more?.Invoke(b); });

    private static async Task<EntitlementMatrixDto> ReadAs(Bd h, EntitlementMatrixController who, Guid? companyId, DateOnly? asOf = null) =>
        (EntitlementMatrixDto)((OkObjectResult)await who.Matrix(h.Service, companyId, asOf ?? Bd.Today, default)).Value!;

    private static (int? Status, string? Error) Outcome(IActionResult r) => (Status(r), Error(r));

    private static UpdateBenefitRequest Rename(string version, string nameEn, BenefitScopeInput? scope = null) =>
        new(version, nameEn, null) { Scope = scope };

    // ══ a company admin adds a benefit for its own company ══════════════════════════════════════

    [Fact]
    public async Task ACompanyAdmin_AddsAContractBenefit_ThatOnlyItsOwnCompanySees()
    {
        await using var h = await Bd.Create();

        var created = Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default));

        created.Code.Should().MatchRegex("^BEN_PARKING_[A-Z2-9]{6}$", "a company's own code carries the company's tag, so no sister company's code can collide with it");
        created.OwnerCompanyId.Should().Be(h.Alpha.Id);
        var row = (await h.Rows(created.Code)).Single();
        (row.CompanyId, row.IsOffered, row.ComponentType, row.EntitlementClass, row.CreatedBy)
            .Should().Be(((Guid?)h.Alpha.Id, true, PayComponentTypes.Benefit, PayEntitlementClasses.Contractual, (Guid?)h.HrTwo));
        (row.WpsIncluded, row.GosiSubject, row.IsTaxable, row.EosbIncluded).Should().Be((false, false, false, false), "still a non-paying contract benefit");

        var alpha = await ReadAs(h, AlphaAdmin(h), h.Alpha.Id);
        var shown = alpha.Components.Single(c => c.Code == created.Code);
        shown.OwnerCompanyId.Should().Be(h.Alpha.Id);
        shown.Definition!.OfferedCompanyIds.Should().Equal(h.Alpha.Id);
        var offering = alpha.Offerings.Single(o => o.ComponentCode == created.Code);
        (offering.Mode, offering.Offered, offering.CanBeSkipped).Should().Be((EntitlementMatrixService.Modes.Own, true, false));
        alpha.Capabilities!.CompanyBenefits.Should().BeTrue();

        foreach (var other in new[] { await ReadAs(h, BetaAdmin(h), h.Beta.Id), await ReadAs(h, h.As(h.Hr), h.Gamma.Id), await ReadAs(h, h.As(h.Hr), null) })
        {
            other.Components.Should().NotContain(c => c.Code == created.Code);
            other.Offerings.Should().NotContain(o => o.ComponentCode == created.Code);
            other.Gaps.Should().NotContain(g => g.ComponentCode == created.Code, "a benefit another company owns is no gap anywhere else");
        }

        var audit = await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitAdded);
        (audit.CompanyId, audit.UserId).Should().Be(((Guid?)h.Alpha.Id, (Guid?)h.HrTwo));
        JsonDocument.Parse(audit.Metadata!).RootElement.GetProperty("ownerCompanyId").GetGuid().Should().Be(h.Alpha.Id);
    }

    [Fact]
    public async Task ItsValuesArePublishedInItsCompany_AndOnlyThatCompanysGradeStandardEverCarriesIt()
    {
        await using var h = await Bd.Create();
        var code = Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default)).Code;

        var published = await AlphaAdmin(h).Publish(h.Service, new PublishMatrixRequest(h.Alpha.Id, Nov1, [Amount(h.G[1], code, 300m)]), false, default);
        Status(published).Should200(Describe(published));
        (await h.Db.GradeEntitlements.SingleAsync()).CompanyId.Should().Be(h.Alpha.Id);

        // The group grid and a sister company cannot take a value for it: it is not in their catalogue.
        var group = await h.As(h.Hr).Publish(h.Service, new PublishMatrixRequest(null, Nov1, [Amount(h.G[1], code, 300m)]), false, default);
        var beta = await h.As(h.Hr).Publish(h.Service, new PublishMatrixRequest(h.Beta.Id, Nov1, [Amount(h.G[1], code, 300m)]), false, default);
        var nothing = await h.As(h.Hr).Publish(h.Service, new PublishMatrixRequest(h.Beta.Id, Nov1, [Amount(h.G[1], "BEN_NOT_THERE", 300m)]), false, default);
        foreach (var refused in new[] { group, beta })
        {
            Status(refused).Should().Be(400);
            JsonSerializer.SerializeToElement(((ObjectResult)refused).Value, Web).GetProperty("errors")[0].GetProperty("code").GetString()
                .Should().Be(JsonSerializer.SerializeToElement(((ObjectResult)nothing).Value, Web).GetProperty("errors")[0].GetProperty("code").GetString());
        }
        (await h.Db.GradeEntitlements.CountAsync()).Should().Be(1);

        var resolver = new EntitlementResolver(h.Db);
        (await h.Service.GradeStandardLinesAsync(h.Tid, h.G[1].Id, h.Alpha.Id, Nov1, default)).Should().Contain(l => l.ComponentCode == code && l.Offered);
        (await resolver.GradeStandardAsync(h.Tid, h.G[1].Id, h.Alpha.Id, Nov1, default)).Should().Contain(l => l.ComponentCode == code && l.Offered);
        (await h.Service.GradeStandardLinesAsync(h.Tid, h.G[1].Id, h.Beta.Id, Nov1, default)).Should().NotContain(l => l.ComponentCode == code);
        (await resolver.GradeStandardAsync(h.Tid, h.G[1].Id, h.Beta.Id, Nov1, default)).Should().NotContain(l => l.ComponentCode == code);
    }

    [Fact]
    public async Task EvenAGroupCellWrittenForItBehindTheApisBack_IsNeverOfferedToASisterCompany()
    {
        await using var h = await Bd.Create();
        var code = Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default)).Code;
        h.Db.GradeEntitlements.Add(new GradeEntitlement
        {
            TenantId = h.Tid, CompanyId = null, GradeId = h.G[1].Id, PayComponentCode = code, EntitlementClass = PayEntitlementClasses.Contractual,
            Eligible = true, ValueType = "Amount", Amount = 300m, EffectiveFrom = Nov1, SourceRule = EntitlementMatrixService.SourceRuleMatrix,
        });
        await h.Db.SaveChangesAsync();

        (await h.Service.GradeStandardLinesAsync(h.Tid, h.G[1].Id, h.Beta.Id, Nov1, default)).Where(l => l.ComponentCode == code).Should().OnlyContain(l => !l.Offered);
        (await new EntitlementResolver(h.Db).GradeStandardAsync(h.Tid, h.G[1].Id, h.Beta.Id, Nov1, default)).Where(l => l.ComponentCode == code)
            .Should().OnlyContain(l => !l.Offered, "a definition only one company can see is offered to that company alone, whatever cells exist");
    }

    [Fact]
    public async Task ACompanyAdmin_AddsAMonthlyCashAllowance_ThatWaitsForFinanceOfThatCompany_AndTheMakerNeverConfirmsIt()
    {
        await using var h = await Bd.Create();

        var created = Component(await AlphaAdmin(h).AddBenefit(h.Service, OwnCash(h.Alpha.Id, "Car"), default));

        created.Code.Should().MatchRegex("^ALW_CAR_[A-Z2-9]{6}$");
        created.CashAllowance!.FinancePending.Should().BeTrue();
        var row = (await h.Rows(created.Code)).Single();
        (row.CompanyId, row.ComponentType, row.CalcMethod, row.GlDriverKey, row.WpsIncluded, row.GosiSubject, row.CreatedBy, row.FinanceConfirmedAtUtc)
            .Should().Be(((Guid?)h.Alpha.Id, PayComponentTypes.Earning, PayComponentCalcMethods.GradePackage, CarDriver, true, false, (Guid?)h.HrTwo, (DateTime?)null));

        Outcome(await AlphaAdmin(h, finance: true).ConfirmBenefit(h.Service, created.Code, null, default)).Should().Be(((int?)403, "maker_cannot_confirm"));
        Outcome(await BetaAdmin(h, finance: true).ConfirmBenefit(h.Service, created.Code, null, default))
            .Should().Be(Outcome(await BetaAdmin(h, finance: true).ConfirmBenefit(h.Service, "ALW_NOT_THERE", null, default)), "Finance of a sister company is told nothing about it");
        Outcome(await h.As(h.Hr, companyScope: [h.Alpha.Id]).ConfirmBenefit(h.Service, created.Code, null, default)).Should().Be(((int?)403, "finance_required"));
        (await h.Rows(created.Code)).Single().FinanceConfirmedAtUtc.Should().BeNull("nothing pays while it is pending");

        var confirmed = Component(await h.As(h.FinanceA, finance: true, companyScope: [h.Alpha.Id]).ConfirmBenefit(h.Service, created.Code, null, default));
        confirmed.CashAllowance!.FinancePending.Should().BeFalse();
        (await h.Rows(created.Code)).Single().FinanceConfirmedByUserId.Should().Be(h.FinanceA);
        (await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitConfirmed)).CompanyId.Should().Be(h.Alpha.Id);
    }

    [Fact]
    public async Task ACompanyAdminWhoHoldsTheFinancePermission_StillLeavesTheirOwnAllowancePending_ForAnotherFinanceApprover()
    {
        await using var h = await Bd.Create();

        var created = Component(await AlphaAdmin(h, finance: true).AddBenefit(h.Service, OwnCash(h.Alpha.Id, "Car"), default));

        created.CashAllowance!.FinancePending.Should().BeTrue("a company's own allowance is never confirmed by the person who added it");
    }

    [Fact]
    public async Task ACompanysAllowance_PostsToAGlDriverOfThatCompany_TheCompanyOverrideWinning()
    {
        await using var h = await Bd.Create();
        // A driver only Alpha has (company override row); the tenant has no such driver.
        var fleet = Bd.Driver(h.Tid, "EARN:ALPHA_FLEET", "5299");
        fleet.CompanyId = h.Alpha.Id;
        h.Db.GlDrivers.Add(fleet);
        await h.Db.SaveChangesAsync();

        var alphaDrivers = (IEnumerable<EntitlementMatrixService.CashGlDriverDto>)((OkObjectResult)await AlphaAdmin(h).CashGlDrivers(h.Service, h.Alpha.Id, default)).Value!;
        alphaDrivers.Should().Contain(x => x.Key == "EARN:ALPHA_FLEET" && x.Usable);
        var groupDrivers = (IEnumerable<EntitlementMatrixService.CashGlDriverDto>)((OkObjectResult)await h.As(h.Hr).CashGlDrivers(h.Service, null, default)).Value!;
        groupDrivers.Should().NotContain(x => x.Key == "EARN:ALPHA_FLEET");
        (await BetaAdmin(h).CashGlDrivers(h.Service, h.Alpha.Id, default)).Should().BeOfType<ForbidResult>();

        Component(await AlphaAdmin(h).AddBenefit(h.Service, OwnCash(h.Alpha.Id, "Fleet car", b => b.GlDriverKey = "EARN:ALPHA_FLEET"), default));
        Error(await h.As(h.Hr).AddBenefit(h.Service, Contract("Group fleet", b => { b.Kind = BenefitKinds.Cash; b.GlDriverKey = "EARN:ALPHA_FLEET"; }), default))
            .Should().Be("gl_driver_invalid", "a group allowance posts in every company, so a driver only one company has is not enough");
    }

    // ══ who may: the company in the body, and another company's benefit ═════════════════════════

    [Fact]
    public async Task TheCompanyInTheBody_MustBeOneTheCallerCanReach_AndAScopeCannotWidenIt()
    {
        await using var h = await Bd.Create();

        Outcome(await BetaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default)).Should().Be(((int?)403, "company_not_accessible"));
        Outcome(await AlphaAdmin(h).AddBenefit(h.Service, Own(Guid.NewGuid(), "Parking"), default)).Should().Be(((int?)403, "company_not_accessible"),
            "a company that is not the caller's is refused the same way whether or not it exists");
        Outcome(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking", b => { b.Scope = new BenefitScopeInput([h.Alpha.Id, h.Beta.Id]); b.EffectiveFrom = Nov1; }), default))
            .Should().Be(((int?)400, "scope_not_company"));
        Outcome(await AlphaAdmin(h).AddBenefit(h.Service, Contract("Parking"), default)).Should().Be(((int?)403, "group_scope_required"), "without a company it is a group benefit, as today");
        (await h.Db.PayComponents.CountAsync()).Should().Be(0, "every refusal writes nothing");

        Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking", b => { b.Scope = new BenefitScopeInput([h.Alpha.Id]); b.EffectiveFrom = Nov1; }), default));
    }

    [Fact]
    public async Task ASisterCompanysAdmin_CannotReachItInAnyWay_AndIsAnsweredAsIfItDidNotExist()
    {
        await using var h = await Bd.Create();
        var parking = Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default));
        var car = Component(await AlphaAdmin(h).AddBenefit(h.Service, OwnCash(h.Alpha.Id, "Car"), default));
        var beta = BetaAdmin(h);

        foreach (var (code, ghost) in new[] { (parking.Code, "BEN_PARKING_ZZZZZZ"), (car.Code, "ALW_CAR_ZZZZZZ") })
        {
            Outcome(await beta.UpdateBenefit(h.Service, code, Rename(parking.Definition!.Version, "Mine now"), default))
                .Should().Be(Outcome(await beta.UpdateBenefit(h.Service, ghost, Rename(parking.Definition!.Version, "Mine now"), default)));
            Outcome(await beta.RemoveBenefit(h.Service, code, default)).Should().Be(Outcome(await beta.RemoveBenefit(h.Service, ghost, default)));
            Outcome(await beta.SetOffering(h.Service, new SetOfferingRequest(h.Beta.Id, code, true, Nov1), default))
                .Should().Be(Outcome(await beta.SetOffering(h.Service, new SetOfferingRequest(h.Beta.Id, ghost, true, Nov1), default)));
            Outcome(await beta.SetOffering(h.Service, new SetOfferingRequest(h.Beta.Id, code, false, Nov1), default))
                .Should().Be(Outcome(await beta.SetOffering(h.Service, new SetOfferingRequest(h.Beta.Id, ghost, false, Nov1), default)));
        }
        Outcome(await beta.UpdateBenefit(h.Service, parking.Code, Rename(parking.Definition!.Version, "Mine now"), default)).Should().Be(((int?)404, "unknown_component"));
        (await beta.Matrix(h.Service, h.Alpha.Id, Bd.Today, default)).Should().BeOfType<ForbidResult>();

        var rows = await h.Db.PayComponents.AsNoTracking().Where(x => x.Code == parking.Code || x.Code == car.Code).ToListAsync();
        rows.Should().HaveCount(2).And.OnlyContain(r => r.CompanyId == h.Alpha.Id && !r.IsDeleted && r.IsOffered && r.NameEn != "Mine now");
    }

    [Fact]
    public async Task ACompanyAdmin_CannotEditOrRemoveAGroupBenefit_ButStillSwitchesItForTheirCompany()
    {
        await using var h = await Bd.Create();
        var gym = Component(await h.Add(Contract("Gym")));

        Outcome(await AlphaAdmin(h).UpdateBenefit(h.Service, gym.Code, Rename(gym.Definition!.Version, "Alpha gym"), default)).Should().Be(((int?)403, "group_scope_required"));
        Outcome(await AlphaAdmin(h).RemoveBenefit(h.Service, gym.Code, default)).Should().Be(((int?)403, "group_scope_required"));
        Status(await AlphaAdmin(h).SetOffering(h.Service, new SetOfferingRequest(h.Alpha.Id, gym.Code, false, Nov1), default)).Should().Be(200);
    }

    // ══ names and codes: two companies can each have a "Car", and nobody learns of the other's ══

    [Fact]
    public async Task TwoCompanies_CanEachOwnACar_WithTheirOwnCodes_AndNeitherAnswerMentionsTheOther()
    {
        await using var h = await Bd.Create();

        var alphaCar = Component(await AlphaAdmin(h).AddBenefit(h.Service, OwnCash(h.Alpha.Id, "Car"), default));
        var betaResponse = await BetaAdmin(h).AddBenefit(h.Service, OwnCash(h.Beta.Id, "Car"), default);
        var betaCar = Component(betaResponse);

        betaCar.Code.Should().NotBe(alphaCar.Code);
        JsonSerializer.Serialize(((ObjectResult)betaResponse).Value, Web).Should().NotContain(alphaCar.Code).And.NotContain(h.Alpha.Id.ToString());
        (await ReadAs(h, BetaAdmin(h), h.Beta.Id)).Components.Where(c => c.NameEn == "Car").Select(c => c.Code).Should().Equal(betaCar.Code);

        // The same name again in the SAME company is refused, as it always was.
        Outcome(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "car"), default)).Should().Be(((int?)409, "benefit_exists"));
        // A typed code is the company's own too: the same typed code in two companies makes two codes.
        var a = Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Fuel", b => b.Code = "FUEL"), default));
        var b = Component(await BetaAdmin(h).AddBenefit(h.Service, Own(h.Beta.Id, "Fuel", b => b.Code = "FUEL"), default));
        a.Code.Should().NotBe(b.Code);
        a.Code.Should().StartWith("BEN_FUEL_");
    }

    [Fact]
    public async Task ANameTheGroupAlreadyUses_IsRefusedForACompany_AndAGroupNameACompanyAlreadyOwns_IsRefusedForTheGroup()
    {
        await using var h = await Bd.Create();
        Component(await h.Add(Contract("Gym")));
        Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default));

        Outcome(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Gym"), default)).Should().Be(((int?)409, "benefit_exists"), "every company already sees the group's gym");
        Outcome(await h.Add(Contract("Parking"))).Should().Be(((int?)409, "benefit_exists"), "group HR sees every company, so a group parking would sit twice in Alpha's grid");
    }

    [Fact]
    public async Task RenamingToANameASisterCompanyUses_IsAllowed_SoAnEditIsNoOracleEither()
    {
        await using var h = await Bd.Create();
        Component(await BetaAdmin(h).AddBenefit(h.Service, Own(h.Beta.Id, "Valet parking"), default));
        var alpha = Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default));

        var renamed = Component(await AlphaAdmin(h).UpdateBenefit(h.Service, alpha.Code, Rename(alpha.Definition!.Version, "Valet parking"), default));

        renamed.NameEn.Should().Be("Valet parking");
    }

    // ══ ownership: the owner edits and removes; after the group widens it, it is the group's ════

    [Fact]
    public async Task TheOwningCompanysAdmin_EditsAndRemovesItsOwnUnusedBenefit_EachAuditedWithTheCompany()
    {
        await using var h = await Bd.Create();
        var created = Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default));

        var edited = Component(await AlphaAdmin(h).UpdateBenefit(h.Service, created.Code, new UpdateBenefitRequest(created.Definition!.Version, "Staff parking", null) { Category = "Facilities" }, default));
        edited.Definition!.Category.Should().Be("Facilities");
        Status(await AlphaAdmin(h).RemoveBenefit(h.Service, created.Code, default)).Should().Be(200);

        (await h.Db.PayComponents.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Code == created.Code)).IsDeleted.Should().BeTrue("removed, kept as history");
        (await ReadAs(h, AlphaAdmin(h), h.Alpha.Id)).Components.Should().NotContain(c => c.Code == created.Code);
        var audits = await h.Db.AuditLogs.Where(a => a.Action == EntitlementMatrixService.AuditBenefitUpdated || a.Action == EntitlementMatrixService.AuditBenefitRemoved).ToListAsync();
        audits.Should().HaveCount(2).And.OnlyContain(a => a.CompanyId == h.Alpha.Id && a.UserId == h.HrTwo);
    }

    [Fact]
    public async Task OnceGroupHrWidensIt_ItIsTheGroups_TheOtherCompaniesSeeIt_AndTheCompanyAdminCanNoLongerEditOrRemoveIt()
    {
        await using var h = await Bd.Create();
        var created = Component(await AlphaAdmin(h).AddBenefit(h.Service, Own(h.Alpha.Id, "Parking"), default));

        // The owner cannot widen it itself.
        Outcome(await AlphaAdmin(h).UpdateBenefit(h.Service, created.Code, Rename(created.Definition!.Version, "Parking", new BenefitScopeInput([h.Alpha.Id, h.Beta.Id])), default))
            .Should().Be(((int?)403, "group_scope_required"));

        var widened = Component(await h.Put(created.Code, Rename(created.Definition!.Version, "Parking", new BenefitScopeInput([h.Alpha.Id, h.Beta.Id]))));

        widened.OwnerCompanyId.Should().BeNull();
        widened.Definition!.OfferedCompanyIds.Should().BeEquivalentTo([h.Alpha.Id, h.Beta.Id]);
        var rows = await h.Rows(created.Code);
        rows.Single(r => r.IsOffered).CompanyId.Should().BeNull("the definition is the group's now");
        rows.Where(r => !r.IsOffered).Select(r => r.CompanyId).Should().Equal(h.Gamma.Id);
        (await ReadAs(h, BetaAdmin(h), h.Beta.Id)).Components.Should().Contain(c => c.Code == created.Code);
        (await ReadAs(h, h.As(h.Hr), h.Gamma.Id)).Offerings.Single(o => o.ComponentCode == created.Code).Mode.Should().Be(EntitlementMatrixService.Modes.Skipped);
        var audit = await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitUpdated);
        JsonDocument.Parse(audit.Metadata!).RootElement.GetProperty("widenedFrom").GetGuid().Should().Be(h.Alpha.Id);

        Outcome(await AlphaAdmin(h).UpdateBenefit(h.Service, created.Code, Rename(widened.Definition!.Version, "Alpha parking"), default)).Should().Be(((int?)403, "group_scope_required"));
        Outcome(await AlphaAdmin(h).RemoveBenefit(h.Service, created.Code, default)).Should().Be(((int?)403, "group_scope_required"));
    }

    [Fact]
    public async Task WideningACashAllowance_IsToEveryCompany_ChecksItsGlDriverForEachOne_AndSendsItBackToFinanceUnlessFinanceWidensIt()
    {
        await using var h = await Bd.Create();
        var car = Component(await AlphaAdmin(h).AddBenefit(h.Service, OwnCash(h.Alpha.Id, "Car"), default));
        Component(await h.As(h.FinanceA, finance: true, companyScope: [h.Alpha.Id]).ConfirmBenefit(h.Service, car.Code, null, default));
        var version = (await ReadAs(h, AlphaAdmin(h), h.Alpha.Id)).Components.Single(c => c.Code == car.Code).Definition!.Version;

        Outcome(await h.Put(car.Code, Rename(version, "Car", new BenefitScopeInput([h.Alpha.Id, h.Beta.Id])))).Should().Be(((int?)400, "scope_not_supported_for_cash"));
        var widened = Component(await h.Put(car.Code, Rename(version, "Car", new BenefitScopeInput([h.Alpha.Id, h.Beta.Id, h.Gamma.Id]))));

        widened.OwnerCompanyId.Should().BeNull();
        widened.CashAllowance!.FinancePending.Should().BeTrue("Finance confirmed it for one company; the group's Finance confirms it for all");
        (await h.Rows(car.Code)).Single().CompanyId.Should().BeNull();
    }

    [Fact]
    public async Task GroupHr_StillAddsGroupBenefits_AndCanAddACompanysOwnFromThatCompanysView()
    {
        await using var h = await Bd.Create();

        var group = Component(await h.Add(Contract("Gym")));
        var own = Component(await h.Add(Own(h.Beta.Id, "Parking")));

        group.OwnerCompanyId.Should().BeNull();
        (await h.Rows(group.Code)).Single().CompanyId.Should().BeNull();
        own.OwnerCompanyId.Should().Be(h.Beta.Id);
        (await ReadAs(h, h.As(h.Hr), null)).Components.Should().Contain(c => c.Code == group.Code).And.NotContain(c => c.Code == own.Code);
    }

    // ══ replay ══════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AReplayedCompanyCreate_IsHonouredOnlyWhileTheCallerStillReachesTheCompany()
    {
        await using var h = await Bd.Create();
        var requestId = Guid.NewGuid();
        var request = Own(h.Alpha.Id, "Parking", b => b.RequestId = requestId);
        var first = Component(await AlphaAdmin(h).AddBenefit(h.Service, request, default));

        Component(await AlphaAdmin(h).AddBenefit(h.Service, request, default)).Code.Should().Be(first.Code);
        // The same person, moved to Beta, replays the same request: nothing about Alpha's benefit comes back.
        Outcome(await h.As(h.HrTwo, companyScope: [h.Beta.Id]).AddBenefit(h.Service, request, default)).Should().Be(((int?)403, "company_not_accessible"));
        (await h.Db.PayComponents.CountAsync()).Should().Be(1);
    }
}
