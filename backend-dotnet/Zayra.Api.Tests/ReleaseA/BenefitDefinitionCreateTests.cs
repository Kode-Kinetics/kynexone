using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// POST /api/entitlements/components with the benefit definition fields (feature <c>benefit_definition_v1</c>): what is stored where, what the matrix
/// shows, every refusal (each writes nothing), the company scope in the same save, the audit row, and the flag-off posture.
/// </summary>
public class BenefitDefinitionCreateTests
{
    private static readonly DateOnly Nov1 = Bd.Nov1;
    private static readonly DateOnly Oct31 = Bd.Oct31Next;
    private static readonly JsonSerializerOptions Web = Bd.Web;

    private static CreateBenefitRequest Parking(Action<CreateBenefitRequestBuilder>? more = null) => Contract("Parking", b =>
    {
        b.Code = "PARK"; b.Category = "Facilities"; b.PolicyClass = "Discretionary"; b.EffectiveFrom = Nov1; b.EffectiveTo = Oct31;
        more?.Invoke(b);
    });

    // ══ the happy path: stored, shown, publishable ══════════════════════════════════════════════

    [Fact]
    public async Task ABenefitCreatedWithEveryField_IsStoredOnTheTenantRow_ShownInTheMatrixWithItsDefinition_AndItsCellsCanBePublished()
    {
        await using var h = await Bd.Create();

        var created = Component(await h.Add(Parking(b => b.RequestId = Guid.NewGuid())));

        created.Code.Should().Be("BEN_PARK");
        created.Definition.Should().NotBeNull();
        var row = (await h.Rows("BEN_PARK")).Single();
        (row.CompanyId, row.ComponentType, row.EntitlementClass, row.BenefitCategory, row.PolicyClass, row.RequiresEnrolment, row.SourceBenefitPlanId)
            .Should().Be(((Guid?)null, PayComponentTypes.Benefit, PayEntitlementClasses.Contractual, "Facilities", "Discretionary", (bool?)false, (Guid?)null));
        (row.EffectiveFrom, row.EffectiveTo, row.IsActive, row.IsOffered).Should().Be(((DateOnly?)Nov1, (DateOnly?)Oct31, true, true));
        (row.GosiSubject, row.IsTaxable, row.WpsIncluded, row.EosbIncluded, row.GlDriverKey).Should().Be((false, false, false, false, (string?)null),
            "a contract benefit stays non-paying: every payroll flag off");

        var matrix = await h.Read(Nov1.AddDays(14));
        matrix.Capabilities!.BenefitDefinition.Should().BeTrue();
        var shown = matrix.Components.Single(c => c.Code == "BEN_PARK");
        shown.IsCustom.Should().BeTrue();
        var def = shown.Definition!;
        (def.Delivery, def.Category, def.PolicyClass, def.EffectiveFrom, def.EffectiveTo, def.IsActive, def.RequiresEnrolment, def.SourcePlanId, def.CodeLocked)
            .Should().Be(("Coverage", "Facilities", "Discretionary", (DateOnly?)Nov1, (DateOnly?)Oct31, true, false, (Guid?)null, false));
        def.OfferedCompanyIds.Should().BeEquivalentTo([h.Alpha.Id, h.Beta.Id, h.Gamma.Id]);
        def.Version.Should().NotBeNullOrWhiteSpace();
        matrix.Components.Where(c => c.Definition is not null).Select(c => c.Code).Should().Equal(new[] { "BEN_PARK" }, "built-in components have no definition");

        Status(await h.PublishGroup(Nov1, Amount(h.G[1], "BEN_PARK", 1_200m))).Should().Be(200);
        (await h.Db.GradeEntitlements.SingleAsync()).PayComponentCode.Should().Be("BEN_PARK");
        (await h.Read(Nov1.AddDays(14))).Cells.Should().ContainSingle(c => c.ComponentCode == "BEN_PARK" && c.Amount == 1_200m);
    }

    [Fact]
    public async Task ACreate_WritesOneAuditRow_WithTheFieldsTheActorAndTheRequestId_InTheSameSave()
    {
        await using var h = await Bd.Create();
        var requestId = Guid.NewGuid();

        Component(await h.Add(Parking(b => b.RequestId = requestId)));

        var audit = await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitAdded);
        (audit.UserId, audit.EntityName, audit.TenantId).Should().Be(((Guid?)h.Hr, "PayComponent", (Guid?)h.Tid));
        var meta = JsonDocument.Parse(audit.Metadata!).RootElement;
        meta.GetProperty("componentCode").GetString().Should().Be("BEN_PARK");
        meta.GetProperty("category").GetString().Should().Be("Facilities");
        meta.GetProperty("policyClass").GetString().Should().Be("Discretionary");
        meta.GetProperty("requestId").GetGuid().Should().Be(requestId);
    }

    [Fact]
    public async Task ASubsetScope_WritesASkipMarkerForEachOtherCompany_FromTheEffectiveDate_InTheSameSave()
    {
        await using var h = await Bd.Create();

        var created = Component(await h.Add(Parking(b => b.Scope = new BenefitScopeInput([h.Alpha.Id]))));

        created.Definition!.OfferedCompanyIds.Should().Equal(h.Alpha.Id);
        var markers = (await h.Rows("BEN_PARK")).Where(r => r.CompanyId != null).ToList();
        markers.Select(m => m.CompanyId).Should().BeEquivalentTo([(Guid?)h.Beta.Id, h.Gamma.Id]);
        markers.Should().OnlyContain(m => !m.IsOffered && m.EffectiveFrom == Nov1 && m.ComponentType == PayComponentTypes.Benefit && m.BenefitCategory == null);
        (await h.Rows("BEN_PARK", tenantWideOnly: true)).Single().IsOffered.Should().BeTrue();
        var matrix = await h.Read(Nov1.AddDays(14), h.Beta.Id);
        matrix.Offerings.Single(o => o.ComponentCode == "BEN_PARK").Mode.Should().Be(EntitlementMatrixService.Modes.Skipped);
        (await h.Read(Nov1.AddDays(14), h.Alpha.Id)).Offerings.Single(o => o.ComponentCode == "BEN_PARK").Offered.Should().BeTrue();
    }

    [Fact]
    public async Task AScopeListingEveryCompany_IsTheSameAsAllCompanies_NoMarkers()
    {
        await using var h = await Bd.Create();

        Component(await h.Add(Parking(b => b.Scope = new BenefitScopeInput([h.Alpha.Id, h.Beta.Id, h.Gamma.Id]))));

        (await h.Rows("BEN_PARK")).Should().ContainSingle();
    }

    [Fact]
    public async Task ABenefitWithoutACode_StillGetsAPrefixedNameDerivedCode_AndBlankFieldsAreNull()
    {
        await using var h = await Bd.Create();

        var created = Component(await h.Add(Contract("Gym membership", b => { b.Category = "   "; b.PolicyClass = ""; })));

        created.Code.Should().Be("BEN_GYM_MEMBERSHIP");
        var row = (await h.Rows("BEN_GYM_MEMBERSHIP")).Single();
        (row.BenefitCategory, row.PolicyClass, row.EffectiveFrom, row.EffectiveTo).Should().Be(((string?)null, (string?)null, (DateOnly?)null, (DateOnly?)null));
    }

    [Fact]
    public async Task ACashAllowance_IsAllCompanies_Monthly_AndKeepsTheMakerCheckerRule_ForTheNewFieldsToo()
    {
        await using var h = await Bd.Create();
        CreateBenefitRequest Cash(string name, Action<CreateBenefitRequestBuilder>? more = null) => Contract(name, b =>
        {
            b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Allowance"; b.PolicyClass = "Contractual";
            more?.Invoke(b);
        });

        var pending = Component(await h.Add(Cash("Car")));
        var confirmed = Component(await h.Add(Cash("Phone"), user: h.FinanceA, finance: true));

        pending.Code.Should().Be("ALW_CAR");
        pending.CashAllowance!.FinancePending.Should().BeTrue();
        confirmed.CashAllowance!.FinancePending.Should().BeFalse("a Finance-permission creator still confirms by creating, exactly as today");
        var car = (await h.Rows("ALW_CAR")).Single();
        (car.ComponentType, car.CalcMethod, car.BenefitCategory, car.PolicyClass, car.CreatedBy, car.FinanceConfirmedAtUtc)
            .Should().Be((PayComponentTypes.Earning, PayComponentCalcMethods.GradePackage, "Allowance", "Contractual", (Guid?)h.Hr, (DateTime?)null));
        Status(await h.As(h.Hr).ConfirmBenefit(h.Service, "ALW_CAR", null, default)).Should().Be(403, "HR lacks the Finance permission");
        Error(await h.As(h.FinanceA, finance: true).ConfirmBenefit(h.Service, "ALW_CAR", null, default)).Should().BeNull();
        (await h.Rows("ALW_CAR")).Single().FinanceConfirmedByUserId.Should().Be(h.FinanceA);
        (await h.AuditCount(EntitlementMatrixService.AuditBenefitAdded)).Should().Be(2);
    }

    // ══ refusals: each is a plain code, and nothing is written ══════════════════════════════════

    public static TheoryData<string, int, string> Refusals => new()
    {
        { "mandatory", 400, "policy_class_invalid" },
        { "reimbursement", 400, "delivery_not_supported" },
        { "payrolldeduction", 400, "delivery_not_supported" },
        { "annualcash", 400, "delivery_not_supported" },
        { "onetimecash", 400, "delivery_not_supported" },
        { "enrolment", 400, "delivery_not_supported" },
        { "coveragefrequency", 400, "frequency_not_applicable" },
        { "kinddelivery", 400, "kind_delivery_mismatch" },
        { "onecompanycash", 400, "scope_not_supported_for_cash" },
        { "emptyscope", 400, "scope_empty" },
        { "foreigncompany", 400, "company_not_found" },
        { "scopenodate", 400, "effective_from_required_for_scope" },
        { "reservedgosi", 400, "code_reserved" },
        { "reservedstatutory", 400, "code_reserved" },
        { "reservedloan", 400, "code_reserved" },
        { "reservedgpssa", 400, "code_reserved" },
        { "reservedgrsia", 400, "code_reserved" },
        { "reservedsaned", 400, "code_reserved" },
        { "codedigitfirst", 400, "code_invalid" },
        { "codespace", 400, "code_invalid" },
        { "codearabic", 400, "code_invalid" },
        { "codeotherprefix", 400, "code_invalid" },
        { "codelong", 400, "code_too_long" },
        { "cashcodelong", 400, "code_too_long" },
        { "fromnotmonthstart", 400, "effective_from_not_month_start" },
        { "tonotmonthend", 400, "effective_to_not_month_end" },
        { "tobeforefrom", 400, "effective_range_invalid" },
        { "lockedperiod", 409, "effective_in_locked_period" },
        { "categorylong", 400, "category_too_long" },
        { "categorycontrol", 400, "category_invalid" },
        { "policyclassgarbage", 400, "policy_class_invalid" },
        { "unknownfield", 400, "unrecognised_field" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedCreate_SaysWhy_InAPlainCode_AndWritesNothing(string scenario, int status, string code)
    {
        await using var h = await Bd.Create();
        h.Db.PayrollRuns.Add(new PayrollRun { TenantId = h.Tid, CompanyId = h.Alpha.Id, Year = 2026, Month = 9, Status = "Approved" });
        await h.Db.SaveChangesAsync();
        var rowsBefore = await h.Db.PayComponents.CountAsync();
        var auditBefore = await h.Db.AuditLogs.CountAsync();
        var foreign = Guid.NewGuid();

        CreateBenefitRequest req = scenario switch
        {
            "mandatory" => Parking(b => b.PolicyClass = "Mandatory"),
            "reimbursement" => Parking(b => b.Delivery = "Reimbursement"),
            "payrolldeduction" => Parking(b => b.Delivery = "PayrollDeduction"),
            "annualcash" => Parking(b => { b.Delivery = "SalaryAllowance"; b.Frequency = "Annual"; b.GlDriverKey = CarDriver; }),
            "onetimecash" => Parking(b => { b.Delivery = "SalaryAllowance"; b.Frequency = "OneTime"; b.GlDriverKey = CarDriver; }),
            "enrolment" => Parking(b => b.RequiresEnrolment = true),
            "coveragefrequency" => Parking(b => { b.Delivery = "Coverage"; b.Frequency = "Monthly"; }),
            "kinddelivery" => Parking(b => { b.Kind = "Contract"; b.Delivery = "SalaryAllowance"; b.GlDriverKey = CarDriver; }),
            "onecompanycash" => Parking(b => { b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Scope = new BenefitScopeInput([h.Alpha.Id]); }),
            "emptyscope" => Parking(b => b.Scope = new BenefitScopeInput([])),
            "foreigncompany" => Parking(b => b.Scope = new BenefitScopeInput([h.Alpha.Id, foreign])),
            "scopenodate" => Contract("Parking", b => b.Scope = new BenefitScopeInput([h.Alpha.Id])),
            "reservedgosi" => Parking(b => b.Code = "GOSI_EXTRA"),
            "reservedstatutory" => Parking(b => b.Code = "STATUTORY_X"),
            "reservedloan" => Parking(b => b.Code = "LOAN_X"),
            "reservedgpssa" => Parking(b => b.Code = "GPSSA1"),
            "reservedgrsia" => Parking(b => b.Code = "GRSIA"),
            "reservedsaned" => Parking(b => b.Code = "SANED_ER"),
            "codedigitfirst" => Parking(b => b.Code = "1PARK"),
            "codespace" => Parking(b => b.Code = "PARK ING"),
            "codearabic" => Parking(b => b.Code = "موقف"),
            "codeotherprefix" => Parking(b => b.Code = "ALW_PARK"),
            "codelong" => Parking(b => b.Code = new string('A', 37)),
            "cashcodelong" => Parking(b => { b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Code = new string('A', 29); }),
            "fromnotmonthstart" => Parking(b => b.EffectiveFrom = new DateOnly(2026, 11, 15)),
            "tonotmonthend" => Parking(b => b.EffectiveTo = new DateOnly(2027, 10, 30)),
            "tobeforefrom" => Parking(b => { b.EffectiveFrom = new DateOnly(2026, 12, 1); b.EffectiveTo = new DateOnly(2026, 11, 30); }),
            "lockedperiod" => Parking(b => { b.EffectiveFrom = new DateOnly(2026, 9, 1); b.EffectiveTo = null; }),
            "categorylong" => Parking(b => b.Category = new string('c', 121)),
            "categorycontrol" => Parking(b => b.Category = "Fac\u0007ilities"),
            "policyclassgarbage" => Parking(b => b.PolicyClass = "Whatever"),
            "unknownfield" => JsonSerializer.Deserialize<CreateBenefitRequest>("""{"nameEn":"Parking","isSystem":true}""", Web)!,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var result = await h.Add(req);

        (Status(result), Error(result)).Should().Be((status, code), Describe(result));
        (await h.Db.PayComponents.CountAsync()).Should().Be(rowsBefore);
        (await h.Db.AuditLogs.CountAsync()).Should().Be(auditBefore);
    }

    [Fact]
    public async Task ACodeThatCollides_WithAnyPayComponentOrSalaryComponent_OfTheTenant_IsTaken_ButAnotherTenantMayUseIt()
    {
        await using var h = await Bd.Create();
        h.Db.PayComponents.Add(new PayComponent { TenantId = h.Tid, Code = "BEN_PARK", NameEn = "Old", NameAr = "قديم", ComponentType = PayComponentTypes.Earning });
        h.Db.SalaryComponents.Add(new SalaryComponent { TenantId = h.Tid, Code = "BEN_GYM", Name = "Gym" });
        await h.Db.SaveChangesAsync();

        var park = await h.Add(Parking());
        var gym = await h.Add(Contract("Gym", b => b.Code = "GYM"));

        (Status(park), Error(park)).Should().Be((409, "code_taken"));
        (Status(gym), Error(gym)).Should().Be((409, "code_taken"));
        h.Db.PayComponents.Add(new PayComponent { TenantId = Guid.NewGuid(), Code = "BEN_SPA", NameEn = "Spa", NameAr = "منتجع", ComponentType = PayComponentTypes.Benefit });
        await h.Db.SaveChangesAsync();
        Component(await h.Add(Contract("Spa", b => b.Code = "SPA"))).Code.Should().Be("BEN_SPA", "uniqueness is per tenant");
    }

    [Fact]
    public async Task ACodeTypedWithItsOwnPrefix_IsAccepted_WithoutDoublingIt()
    {
        await using var h = await Bd.Create();

        Component(await h.Add(Contract("Parking", b => b.Code = "ben_park"))).Code.Should().Be("BEN_PARK");
    }

    [Fact]
    public async Task ACodeAtTheLimit_IsAccepted_ThePrefixCountsTowardsIt()
    {
        await using var h = await Bd.Create();

        Component(await h.Add(Contract("Long", b => b.Code = new string('A', 36)))).Code.Should().HaveLength(40);
        Component(await h.Add(Contract("Cash long", b =>
        {
            b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Code = new string('B', 28);
        }))).Code.Should().HaveLength(32);
    }

    [Fact]
    public async Task ADuplicateName_IsStillBenefitExists_WithoutARequestId()
    {
        await using var h = await Bd.Create();
        Component(await h.Add(Parking()));

        var again = await h.Add(Parking(b => b.Code = "OTHER"));

        (Status(again), Error(again)).Should().Be((409, "benefit_exists"));
        (await h.Db.PayComponents.CountAsync(p => p.Code.StartsWith("BEN_"))).Should().Be(1);
    }

    // ══ idempotency ═════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AReplayWithTheSameRequestId_ByTheSamePerson_ReturnsTheOriginal_NotBenefitExists_AndWritesNothingNew()
    {
        await using var h = await Bd.Create();
        var requestId = Guid.NewGuid();

        var first = Component(await h.Add(Parking(b => b.RequestId = requestId)));
        var replay = Component(await h.Add(Parking(b => b.RequestId = requestId)));

        replay.Code.Should().Be(first.Code);
        replay.Definition!.Version.Should().Be(first.Definition!.Version);
        (await h.Rows("BEN_PARK")).Should().ContainSingle();
        (await h.AuditCount(EntitlementMatrixService.AuditBenefitAdded)).Should().Be(1);
    }

    [Fact]
    public async Task TheSameRequestId_FromAnotherPerson_IsNotAReplay_ItIsTheOrdinaryConflict()
    {
        await using var h = await Bd.Create();
        var requestId = Guid.NewGuid();
        Component(await h.Add(Parking(b => b.RequestId = requestId)));

        var other = await h.Add(Parking(b => b.RequestId = requestId), user: h.HrTwo);

        (Status(other), Error(other)).Should().Be((409, "benefit_exists"), "a request id is never an authority over somebody else's result");
    }

    [Fact]
    public async Task ADifferentRequestId_WithTheSameName_IsBenefitExists()
    {
        await using var h = await Bd.Create();
        Component(await h.Add(Parking(b => b.RequestId = Guid.NewGuid())));

        var again = await h.Add(Parking(b => b.RequestId = Guid.NewGuid()));

        Error(again).Should().Be("benefit_exists");
    }

    // ══ who may, and the flag ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ACompanyScopedAdmin_CannotAddABenefit_WithOrWithoutTheNewFields()
    {
        await using var h = await Bd.Create();

        var result = await h.As(h.Hr, companyScope: [h.Alpha.Id]).AddBenefit(h.Service, Parking(), default);

        (Status(result), Error(result)).Should().Be((403, "group_scope_required"));
        (await h.Db.PayComponents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WithTheFlagOff_TheOldBodyBehavesAsToday_NoDefinitionNoCapability_ButADefinitionFieldIsRefused()
    {
        await using var h = await Bd.Create(definitionFlag: false);

        var created = Component(await h.Add(Contract("Parking")));
        var matrix = await h.Read();

        created.Code.Should().Be("BEN_PARKING");
        created.Definition.Should().BeNull();
        matrix.Capabilities!.BenefitDefinition.Should().BeFalse();
        matrix.Components.Should().OnlyContain(c => c.Definition == null);
        var row = (await h.Rows("BEN_PARKING")).Single();
        (row.BenefitCategory, row.PolicyClass, row.RequiresEnrolment, row.EffectiveFrom, row.EffectiveTo).Should().Be(((string?)null, (string?)null, (bool?)null, (DateOnly?)null, (DateOnly?)null));

        var refused = await h.Add(Parking());
        (Status(refused), Error(refused)).Should().Be((403, "feature_not_enabled"));
        (await h.Rows("BEN_PARK")).Should().BeEmpty();
        var unknown = await h.Add(JsonSerializer.Deserialize<CreateBenefitRequest>("""{"nameEn":"Spa","bogus":1}""", Web)!);
        Error(unknown).Should().Be("unrecognised_field");
    }

    [Fact]
    public async Task TurningTheFlagOff_HidesTheDefinition_ButKeepsTheData()
    {
        await using var h = await Bd.Create();
        Component(await h.Add(Parking()));

        await h.SetFlag(false);

        var matrix = await h.Read(Bd.Today);
        matrix.Capabilities!.BenefitDefinition.Should().BeFalse();
        matrix.Components.Single(c => c.Code == "BEN_PARK").Definition.Should().BeNull();
        matrix.Offerings.Single(o => o.ComponentCode == "BEN_PARK").Mode.Should().Be(EntitlementMatrixService.Modes.Group, "the window is ignored while the feature is off");
        (await h.Rows("BEN_PARK")).Single().BenefitCategory.Should().Be("Facilities", "rollback is the flag; nothing is rewritten");
    }

    [Fact]
    public async Task TheFlagIsPerTenant_AnotherTenantWithoutItIsUnaffected()
    {
        await using var h = await Bd.Create();
        var other = Guid.NewGuid();
        h.Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = other, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        h.Db.Companies.Add(new Company { TenantId = other, LegalNameEn = "Other Co.", DefaultCurrency = "SAR" });
        await h.Db.SaveChangesAsync();

        var refused = await h.As(h.Hr, tenant: other).AddBenefit(h.Service, Parking(), default);

        (Status(refused), Error(refused)).Should().Be((403, "feature_not_enabled"));
    }
}
