using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.ReleaseA;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The Release A shared contracts R0 fixes for slices R1–R6: the resolver/writer interfaces and their records, the
/// DI registrations, the value sets the database CHECKs spell, the block-reason catalogue, the component rules,
/// the release_a opt-in flag, and the hot-spot wiring (approval hook, activation hook, supersede guard, ESS).
/// A slice changing a pinned shape fails here and has to go through the integration owner.
/// </summary>
public class ReleaseAContractTests
{
    private static ZayraDbContext InMemory() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"release-a-{Guid.NewGuid():N}").Options);

    /// <summary>The relational design-time model — the one the migration and its CHECKs come from. Never opens a connection.</summary>
    private static IModel RelationalModel()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened").Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static string CheckSql(IModel model, Type entity, string name) =>
        model.FindEntityType(entity)!.GetCheckConstraints().Single(c => c.Name == name).Sql;

    // ── Interfaces and records (plan §1.2) ───────────────────────────────────────────────────────

    [Fact]
    public void ResolverContract_IsPinned()
    {
        Signature(typeof(IEntitlementResolver), nameof(IEntitlementResolver.ResolveAsync))
            .Should().Be("Task`1[EmployeePackage] ResolveAsync(Guid, Int32, DateOnly, CancellationToken)");
        Signature(typeof(IEntitlementResolver), nameof(IEntitlementResolver.GradeStandardAsync))
            .Should().Be("Task`1[IReadOnlyList`1] GradeStandardAsync(Guid, Guid, Guid, DateOnly, CancellationToken)");

        Properties<EmployeePackage>().Should().Equal(
            "EmployeeId:Int32", "GradeId:Nullable`1", "ContractId:Nullable`1", "TermEndsOn:Nullable`1", "AsOf:DateOnly",
            "Lines:IReadOnlyList`1", "BlockCodes:IReadOnlyList`1");
        Properties<PackageLine>().Should().Equal(
            "ComponentCode:String", "Class:String", "Floor:String", "Source:String", "Offered:Boolean", "Eligible:Boolean",
            "ValueType:String", "Amount:Nullable`1", "Rate:Nullable`1", "MonthlyCash:Nullable`1", "CoverageTier:String",
            "Quantity:Nullable`1", "DependantScope:String", "MaxDependants:Nullable`1", "DependantsCovered:Int32",
            "LimitPeriod:String", "GradeEntitlementId:Nullable`1", "EmployeeEntitlementId:Nullable`1", "IsCompanyOverride:Boolean",
            "GradeStandardDiffers:Boolean", "ReasonCode:String", "MaxOutstandingAmount:Nullable`1", "ResolvedAmount:Nullable`1",
            "EligibleFrom:Nullable`1", "StandardValue:GradeStandardLine");
        // Rates are compared at the grade cell's 4 dp, so salary-row precision never reads as a difference.
        EntitlementRates.Same(0.250000m, 0.2500m).Should().BeTrue();
        EntitlementRates.Same(0.25004m, 0.2500m).Should().BeTrue();
        EntitlementRates.Same(0.2501m, 0.2500m).Should().BeFalse();
        EntitlementRates.Same(null, null).Should().BeTrue();
        PackageLineSources.All.Should().Equal("Salary", "ContractFrozen", "GradeStandard", "Facility");
    }

    [Fact]
    public void WriterLifecycleCalculatorAndStatementContracts_ArePinned()
    {
        typeof(IEntitlementWriter).GetMethods().Select(m => m.Name).Should()
            .BeEquivalentTo("FreezeTermAsync", "ApplyRenewalAsync", "CarryToProvisionalAsync");
        Signature(typeof(IEntitlementWriter), nameof(IEntitlementWriter.FreezeTermAsync))
            .Should().Be("Task`1[FreezeResult] FreezeTermAsync(Guid, Guid, CancellationToken)");
        Signature(typeof(IEntitlementWriter), nameof(IEntitlementWriter.ApplyRenewalAsync))
            .Should().Be("Task ApplyRenewalAsync(Guid, RenewalApplyPlan, CancellationToken)");
        Signature(typeof(IEntitlementWriter), nameof(IEntitlementWriter.CarryToProvisionalAsync))
            .Should().Be("Task CarryToProvisionalAsync(Guid, Guid, Guid, CancellationToken)");
        Signature(typeof(IContractTermLifecycle), nameof(IContractTermLifecycle.OnActivatedAsync))
            .Should().Be("Task OnActivatedAsync(EmployeeContract, CancellationToken)");
        Signature(typeof(IContractTermLifecycle), nameof(IContractTermLifecycle.OnEndedAsync))
            .Should().Be("Task OnEndedAsync(EmployeeContract, String, CancellationToken)");
        ContractEndReasons.All.Should().Equal("Terminated", "Expired", "Superseded", "Separated");
        // CTO decision: an expired term worked on renews by law (Art. 74(2)) — Expired never cancels a case.
        ContractEndReasons.CancelOpenCase.Should().BeEquivalentTo("Terminated", "Separated", "Superseded");
        ContractEndReasons.CancelOpenCase.Should().NotContain(ContractEndReasons.Expired);
        Signature(typeof(IRenewalDeadlineCalculator), nameof(IRenewalDeadlineCalculator.ComputeAsync))
            .Should().Be("Task`1[RenewalDeadlines] ComputeAsync(Guid, EmployeeContract, CancellationToken)");
        Signature(typeof(IDeductionStatementService), nameof(IDeductionStatementService.ForSlipAsync))
            .Should().Be("Task`1[DeductionStatement] ForSlipAsync(Guid, Guid, DeductionAudience, CancellationToken)");
        Signature(typeof(ITenantClock), nameof(ITenantClock.TodayAsync))
            .Should().Be("Task`1[DateOnly] TodayAsync(Guid, CancellationToken)");
        RenewalLineActions.All.Should().Equal("Keep", "Remove", "Lower", "Raise");
        DeductionCategories.All.Should().HaveCount(7);
    }

    [Fact]
    public void EveryReleaseAService_IsRegisteredOnce_WithTheContractedLifetime()
    {
        var services = new ServiceCollection().AddReleaseA();
        Registered<IEntitlementResolver, EntitlementResolver>(services);
        Registered<IEntitlementWriter, EntitlementWriter>(services);
        Registered<IRenewalDeadlineCalculator, RenewalDeadlineCalculator>(services);
        Registered<IDeductionStatementService, DeductionStatementService>(services);
        Registered<ITenantClock, TenantClock>(services);
        Registered<IContractTermLifecycleDispatcher, ContractTermLifecycleDispatcher>(services);
        // Activation hooks: chain first, then the package freeze for that term.
        services.Where(d => d.ServiceType == typeof(IContractTermLifecycle)).Select(d => d.ImplementationType)
            .Should().Equal(typeof(ContractChainStamper), typeof(PackageFreezeOnActivation));
        foreach (var concrete in new[]
                 {
                     typeof(EntitlementMatrixService), typeof(ContractChainCensus), typeof(AllowedActionsDeriver),
                     typeof(RenewalOfferService), typeof(RenewalOfferValidator), typeof(RenewalResponseService),
                     typeof(QiwaEvidenceService), typeof(RenewalApplyService), typeof(RenewalHoldoverStep),
                     typeof(PackageFreezeJobHandler), typeof(RenewalCaseJobHandler),
                 })
            services.Should().ContainSingle(d => d.ServiceType == concrete && d.Lifetime == ServiceLifetime.Scoped, concrete.Name);
        services.Select(d => d.ImplementationInstance).OfType<Zayra.Api.Infrastructure.Jobs.BackgroundJobTypeDescriptor>()
            .Select(d => d.JobType).Should().BeEquivalentTo("entitlements.package-freeze", "contracts.renewal-cases");
    }

    [Fact]
    public async Task Stubs_ThrowUntilTheirSliceLands_ExceptTheActivationHooks_WhichNeverBlockActivation()
    {
        // R2 landed: the resolver and writer are real (Release A R2 tests). Its activation hook must still never block
        // activation, even when the writer fails.
        var contract = new EmployeeContract { TenantId = Guid.NewGuid() };
        await new ContractChainStamper().Invoking(h => h.OnActivatedAsync(contract, default)).Should().NotThrowAsync();
        await new PackageFreezeOnActivation(new ThrowingWriter()).Invoking(h => h.OnActivatedAsync(contract, default)).Should().NotThrowAsync();
    }

    // ── Value sets the database spells (CHECK literals == C# constants) ──────────────────────────

    [Fact]
    public void CheckConstraintLiterals_AgreeWithTheConstantsClasses()
    {
        var model = RelationalModel();
        CheckSql(model, typeof(ContractRenewalCase), "ck_contract_renewal_cases__state").Should().ContainAll(RenewalStates.All.Select(Q));
        CheckSql(model, typeof(ContractRenewalCase), "ck_contract_renewal_cases__closed_iff_terminal").Should().ContainAll(RenewalStates.Terminal.Select(Q));
        CheckSql(model, typeof(ContractRenewalCase), "ck_contract_renewal_cases__hold_reason").Should().ContainAll(RenewalHoldReasons.All.Select(Q));
        CheckSql(model, typeof(EmployeeEntitlement), "ck_employee_entitlements__source").Should().ContainAll(EntitlementSources.All.Select(Q));
        CheckSql(model, typeof(EmployeeEntitlement), "ck_employee_entitlements__value_type").Should().ContainAll(GradeEntitlementValueTypes.All.Select(Q));
        CheckSql(model, typeof(GradeEntitlement), "ck_grade_entitlements__value_type").Should().ContainAll(GradeEntitlementValueTypes.All.Select(Q));
        CheckSql(model, typeof(GradeEntitlement), "ck_grade_entitlements__coverage_tier").Should().ContainAll(CoverageTiers.All.Select(Q));
        CheckSql(model, typeof(GradeEntitlement), "ck_grade_entitlements__dependant_scope").Should().ContainAll(DependantScopes.All.Select(Q));
        CheckSql(model, typeof(GradeEntitlement), "ck_grade_entitlements__limit_period").Should().ContainAll(EntitlementLimitPeriods.All.Select(Q));
        CheckSql(model, typeof(GradeEntitlement), "ck_grade_entitlements__nationality_scope").Should().ContainAll(NationalityScopes.All.Select(Q));
        CheckSql(model, typeof(EmployeeContract), "ck_employee_contracts__provisional_basis").Should().ContainAll(ProvisionalBases.All.Select(Q));
        CheckSql(model, typeof(EmployeeSalaryStructure), "ck_employee_salary_structures__housing_basis").Should().ContainAll(AllowanceBases.All.Select(Q));
        // The array CHECK lives in the migration's PostgreSQL-only DDL; it must name the same four actions.
        Zayra.Api.Migrations.ReleaseAEntitlementsAndRenewals.AddPostgresOnlyChecksSql.Should().ContainAll(ContractActions.All.Select(Q));
        // Every pinned literal set in the whole model stays portable to the SQLite/InMemory schemas the suite builds.
        foreach (var entity in new[] { typeof(EmployeeEntitlement), typeof(ContractRenewalCase), typeof(GradeEntitlement),
                     typeof(EmployeeContract), typeof(EmployeeSalaryStructure), typeof(ApprovalRequest), typeof(PayComponent), typeof(EmployeeLoan) })
            foreach (var check in model.FindEntityType(entity)!.GetCheckConstraints())
                check.Sql.Should().NotMatchRegex(@"~|<@|ANY\(|LEAST|::|btrim", $"{check.Name} must stay portable; PostgreSQL-only CHECKs go in AddPostgresOnlyChecksSql");
    }

    private static string Q(string value) => $"'{value}'";

    // ── Block reasons and component rules ────────────────────────────────────────────────────────

    [Fact]
    public void BlockReasonCatalogue_IsCompleteInEnglishAndArabic()
    {
        var codes = typeof(ReleaseABlockReasons).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        codes.Should().HaveCount(51); // 37 from R0 + 14 from R2 (review rounds 2–4)
        ReleaseABlockReasons.All.Keys.Should().BeEquivalentTo(codes);
        var arabic = new Regex(@"\p{IsArabic}");
        foreach (var reason in ReleaseABlockReasons.All.Values)
        {
            new[] { reason.TitleEn, reason.WhyEn, reason.FixEn, reason.OwnerRole }.Should().OnlyContain(s => s.Trim().Length > 0, reason.Code);
            new[] { reason.TitleAr, reason.WhyAr, reason.FixAr }.Should().OnlyContain(s => arabic.IsMatch(s), reason.Code);
            (reason.TitleEn + reason.WhyEn + reason.FixEn).Should().NotContain(reason.Code, "a person never reads a raw code");
        }
        var unknown = () => ReleaseABlockReasons.Get("NOT_A_CODE");
        unknown.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void ComponentCatalogue_MatchesThePlanTable()
    {
        EntitlementComponentRules.Catalogue.Select(r => (r.Code, r.Class, r.Floor, r.ComponentType)).Should().Equal(
            ("HOUSING", "QiwaWage", "Housing", "Earning"),
            ("TRANSPORT", "QiwaWage", "Transport", "Earning"),
            ("OTHER_ALLOWANCES", "QiwaWage", "None", "Earning"),
            ("AIR_TICKET", "Contractual", "None", "Benefit"),
            ("MEDICAL", "Contractual", "Medical", "Benefit"),
            ("EDUCATION", "Contractual", "None", "Benefit"),
            ("PER_DIEM", "Facility", "None", "Facility"),
            ("LOAN_HOUSING_ADVANCE", "Facility", "None", "Facility"));
        EntitlementComponentRules.For("LOAN_PERSONAL")!.Should().Match<EntitlementComponentRule>(r =>
            r.IsLoanFacility && r.Class == "Facility" && !r.AllowedValueTypes.Contains(GradeEntitlementValueTypes.MultipleOfHousing));
        EntitlementComponentRules.For("LOAN_HOUSING_ADVANCE")!.AllowedValueTypes.Should().Contain(GradeEntitlementValueTypes.MultipleOfHousing);
        EntitlementComponentRules.For("UNKNOWN").Should().BeNull();
        EntitlementComponentRules.BannedCriteria.Should().BeEquivalentTo("age", "gender", "maritalStatus", "disability");
    }

    [Theory]
    [InlineData("HOUSING", false, "InKind", null, null, null, "None", false, "Any", "ENTITLEMENT_FLOOR_HOUSING")]   // not eligible
    [InlineData("HOUSING", true, "Amount", "0", null, null, "None", false, "Any", "ENTITLEMENT_FLOOR_HOUSING")]      // zero cash
    [InlineData("HOUSING", true, "InKind", null, null, null, "None", false, "Any", null)]                            // in kind = provided
    [InlineData("TRANSPORT", true, "PercentOfBasic", null, "0.10", null, "None", false, "Any", null)]
    [InlineData("TRANSPORT", true, "EligibilityOnly", null, null, null, "None", false, "Any", "ENTITLEMENT_FLOOR_TRANSPORT")]
    [InlineData("MEDICAL", true, "CoverageTier", null, null, "CchiBasic", "Family", false, "Any", null)]
    [InlineData("MEDICAL", true, "CoverageTier", null, null, "VIP", "Family", false, "Any", null)]
    [InlineData("MEDICAL", true, "CoverageTier", null, null, "B", "Family", true, "Any", "ENTITLEMENT_FLOOR_MEDICAL")]     // after probation
    [InlineData("MEDICAL", true, "CoverageTier", null, null, "B", "Family", false, "NonSaudi", "ENTITLEMENT_FLOOR_MEDICAL")] // nationality
    [InlineData("MEDICAL", true, "CoverageTier", null, null, "B", "None", false, "Any", "ENTITLEMENT_FLOOR_MEDICAL")]     // employee only
    [InlineData("MEDICAL", true, "CoverageTier", null, null, "Economy", "Family", false, "Any", "ENTITLEMENT_FLOOR_MEDICAL")] // not a medical tier
    [InlineData("AIR_TICKET", false, "EligibilityOnly", null, null, null, "None", false, "NonSaudi", null)]            // no floor
    public void FloorViolations_FollowArticles61AndCchi(string code, bool eligible, string valueType, string? amount, string? rate,
        string? tier, string dependants, bool afterProbation, string nationality, string? expected)
    {
        var codes = EntitlementComponentRules.FloorViolations(EntitlementComponentRules.For(code)!, eligible, valueType,
            amount is null ? null : decimal.Parse(amount), rate is null ? null : decimal.Parse(rate), tier, dependants, afterProbation, nationality);
        if (expected is null) codes.Should().BeEmpty();
        else codes.Should().Equal(expected);
    }

    [Fact]
    public void FloorComponents_CanNeverBeSkippedByACompany()
    {
        foreach (var rule in EntitlementComponentRules.Catalogue)
            EntitlementComponentRules.CanBeSkipped(rule).Should()
                .Be(!rule.IsFloor && !rule.IsLoanFacility && rule.Class != PayEntitlementClasses.QiwaWage, rule.Code);
        // Cash wage that payroll reads is never skippable, floor or not.
        EntitlementComponentRules.CanBeSkipped(EntitlementComponentRules.For("OTHER_ALLOWANCES")!).Should().BeFalse();
        EntitlementComponentRules.Catalogue.Where(EntitlementComponentRules.CanBeSkipped).Select(r => r.Code)
            .Should().BeEquivalentTo("AIR_TICKET", "EDUCATION", "PER_DIEM");
    }

    [Fact]
    public void ComponentsEndpoint_ServesTheCatalogue()
    {
        var ok = new EntitlementMatrixController().Components().Result.Should().BeOfType<OkObjectResult>().Subject;
        var rows = ((IEnumerable<EntitlementMatrixController.EntitlementComponentDto>)ok.Value!).ToList();
        rows.Should().HaveCount(EntitlementComponentRules.Catalogue.Count);
        rows.Single(r => r.Code == "MEDICAL").Should().Match<EntitlementMatrixController.EntitlementComponentDto>(r => r.IsFloor && !r.CanBeSkipped);
        rows.Single(r => r.Code == "EDUCATION").Should().Match<EntitlementMatrixController.EntitlementComponentDto>(r => r.CanBeSkipped && r.AllowsDependants);
        typeof(EntitlementMatrixController).GetMethod(nameof(EntitlementMatrixController.Components))!
            .GetCustomAttributes<Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute>().Should().ContainSingle();
    }

    // ── The release_a opt-in flag ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReleaseA_IsOffUnlessExplicitlyEnabled_AndChangesNoModule()
    {
        var off = TenantModuleService.Build([], "SA");
        off.IsEnabled(FeatureKeys.ReleaseA).Should().BeFalse("an opt-in feature is off when no row exists");
        off.IsEnabled(ModuleKeys.Payroll).Should().BeTrue("modules keep their absent-row-means-on default");
        TenantModuleService.Build([], "SA", [FeatureKeys.ReleaseA]).IsEnabled(FeatureKeys.ReleaseA).Should().BeTrue();
        TenantModuleService.Build([FeatureKeys.ReleaseA], "SA").IsEnabled(FeatureKeys.ReleaseA).Should().BeFalse();
        ModuleCatalog.NonModuleKeys.Should().ContainKey(FeatureKeys.ReleaseA, "tenant admins cannot switch it");
        ModuleCatalog.TryGet(FeatureKeys.ReleaseA).Should().BeNull();
    }

    [Theory]
    [InlineData("/api/entitlements", true)]
    [InlineData("/api/entitlements/components", true)]
    [InlineData("/api/contracts/renewals/radar", true)]
    [InlineData("/api/contracts/0c4f/chain", true)]
    [InlineData("/api/ess/package", true)]
    [InlineData("/api/ess/deductions", true)]
    [InlineData("/api/ess/renewal-offer/abc/accept", true)]
    [InlineData("/api/ess/packages", false)]
    [InlineData("/api/compliance/contracts", false)]
    [InlineData("/api/ess/benefits", false)]
    public void OptInPrefixes_ClaimOnlyReleaseARoutes(string path, bool releaseA) =>
        (OptInFeatures.ResolveApiPath(path) == FeatureKeys.ReleaseA).Should().Be(releaseA);

    [Theory]
    [InlineData(null, 403)]
    [InlineData(false, 403)]
    [InlineData(true, 200)]
    public async Task Guard_ClosesReleaseARoutes_UntilThePlatformEnablesTheFlag(bool? row, int expected)
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        if (row is { } enabled)
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = enabled });
            await db.SaveChangesAsync();
        }
        var filter = new FeatureFlagGuardFilter(new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())),
            NullLogger<FeatureFlagGuardFilter>.Instance);
        var (ctx, passed) = await RunAsync(filter, "/api/entitlements/components", tenantId);
        var status = passed() ? 200 : ((ObjectResult)ctx.Result!).StatusCode;
        status.Should().Be(expected);
        if (expected == 403) ((ObjectResult)ctx.Result!).Value!.ToString().Should().Contain("feature_not_enabled");
    }

    [Fact]
    public async Task OptInAttribute_GatesARouteOutsideThePrefixes()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var services = new ServiceCollection()
            .AddSingleton<ITenantModuleService>(new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())))
            .BuildServiceProvider();
        var attribute = new RequireOptInFeatureAttribute(FeatureKeys.ReleaseA);
        var (ctx, passed) = await RunAsync(attribute, "/api/payroll/slips/1/deduction-statement", tenantId, services);
        passed().Should().BeFalse();
        ((ObjectResult)ctx.Result!).StatusCode.Should().Be(403);

        var notOptIn = () => new RequireOptInFeatureAttribute(FeatureKeys.Payroll);
        notOptIn.Should().Throw<ArgumentException>();
    }

    private static async Task<(ActionExecutingContext Context, Func<bool> Passed)> RunAsync(
        IAsyncActionFilter filter, string path, Guid tenantId, IServiceProvider? services = null)
    {
        var http = new DefaultHttpContext { RequestServices = services! };
        http.Request.Path = path;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenantId.ToString())], "Test"));
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), null!);
        var passed = false;
        await filter.OnActionExecutionAsync(ctx, () =>
        {
            passed = true;
            return Task.FromResult(new ActionExecutedContext(action, [], null!));
        });
        return (ctx, () => passed);
    }

    // ── Hot-spot wiring ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenewalApprovalHook_IsANoOpForEveryOtherApproval_AndLoudForARenewalBeforeR5()
    {
        await using var db = InMemory();
        var other = () => ContractRenewalApprovalSync.ApplyAsync(db, new ApprovalRequest { EntityName = nameof(LeaveRequest) }, "Approved", null, default);
        await other.Should().NotThrowAsync();
        var renewal = () => ContractRenewalApprovalSync.ApplyAsync(db, new ApprovalRequest { EntityName = "contractrenewal" }, "Approved", null, default);
        await renewal.Should().ThrowAsync<NotImplementedException>();
        ApprovalEntities.HasProducer(ContractRenewalApprovalSync.ApprovalEntityName).Should()
            .BeFalse("R5 registers the producer together with the code that creates renewal approvals");
    }

    [Fact]
    public void RenewalApprovalChains_MustNameARoleOnEveryStep()
    {
        var controller = new ApprovalWorkflowsController(null!);
        var open = new ApprovalWorkflowRequest("CR", "Renewal", "ContractRenewal", true,
        [
            new ApprovalWorkflowStepRequest(1, "Manager", "HR Manager", "Role"),
            new ApprovalWorkflowStepRequest(2, "Anyone", "Any", "HR", IsFinalStep: true),
        ]);
        var refused = controller.Create(open, default).Result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        refused.Value!.ToString().Should().Contain("approval_renewal_step_needs_role");

        var named = open with
        {
            Steps =
            [
                new ApprovalWorkflowStepRequest(1, "Manager", "HR Manager", "Role"),
                new ApprovalWorkflowStepRequest(2, "Director", "HR Director", "Role", IsFinalStep: true),
            ],
        };
        // Until R5 registers the producer, a well-formed chain is refused for the honest reason.
        controller.Create(named, default).Result.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("approval_entity_has_no_producer");
    }

    [Fact]
    public async Task RenewalApprovalDefaults_AreNotInstalledBeforeTheirProducerExists()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        await TenantProvisioningBundle.InstallDefaultApprovalWorkflowsAsync(db, tenantId, default);
        db.ApprovalWorkflows.Local.Select(w => w.EntityName).Should().NotContain(ContractRenewalApprovalSync.EntityNames);
        TenantProvisioningBundle.ReleaseAApprovalDefaults.Select(d => d.EntityName).Should().BeEquivalentTo(ContractRenewalApprovalSync.EntityNames);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ContractActivation_RunsTheTermHooks_OnlyForReleaseATenants(bool releaseA, int expectedCalls)
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        if (releaseA) db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        var contract = new EmployeeContract
        {
            TenantId = tenantId, EmployeeId = Guid.NewGuid(), ContractNumber = "CON-1", Status = "PendingApproval",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31),
        };
        db.EmployeeContracts.Add(contract);
        await db.SaveChangesAsync();
        var hook = new RecordingHook();
        var dispatcher = new ContractTermLifecycleDispatcher([hook], new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())));
        var controller = Bind(new ContractsController(db, dispatcher), tenantId);

        var result = await controller.UpdateStatus(contract.Id, new UpdateContractStatusRequest("Active", "HR Lead"), default);

        result.Should().BeOfType<OkObjectResult>();
        hook.Calls.Should().Be(expectedCalls);
    }

    [Fact]
    public async Task Supersede_IsRefusedWhileARenewalCaseIsOpen()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var contract = new EmployeeContract
        {
            TenantId = tenantId, CompanyId = Guid.NewGuid(), EmployeeId = Guid.NewGuid(), ContractNumber = "CON-2", Status = "Active",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31),
        };
        db.EmployeeContracts.Add(contract);
        db.ContractRenewalCases.Add(new ContractRenewalCase
        {
            TenantId = tenantId, CompanyId = contract.CompanyId, EmployeeId = contract.EmployeeId, ExpiringContractId = contract.Id,
            ExpiringEndDate = contract.EndDate!.Value, WorkerNationalityClass = WorkerNationalityClasses.NonSaudi,
            AllowedActions = [ContractActions.RenewAsIs], State = RenewalStates.Open, NoticeDueOn = new DateOnly(2026, 11, 1),
        });
        await db.SaveChangesAsync();
        var controller = Bind(new ContractsController(db), tenantId);
        var replacement = new CreateContractRequest(contract.EmployeeId, null, null, null, new DateOnly(2026, 6, 1), null, 9000m, null, null, null, null);

        var refused = await controller.Supersede(contract.Id, replacement, default);
        refused.Should().BeOfType<ConflictObjectResult>().Which.Value!.ToString().Should().Contain("renewal_case_open");

        var openCase = await db.ContractRenewalCases.SingleAsync();
        openCase.State = RenewalStates.Cancelled;
        openCase.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        (await controller.Supersede(contract.Id, replacement, default)).Should().NotBeOfType<ConflictObjectResult>();
    }

    /// <summary>
    /// The migration import is the other writer of Active contracts: it must not re-import a term under renewal review,
    /// and a contract it creates as Active goes through the same activation hooks as the contract screen.
    /// </summary>
    [Fact]
    public async Task ContractImport_RefusesATermUnderReview_AndRunsTheActivationHooks()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Masar", RegistrationNumber = "CR-1", IsActive = true };
        var employee = new Employee { TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E-1", FullName = "Mohammed", Status = "Active" };
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        db.AddRange(company, employee);
        var underReview = new EmployeeContract
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.PublicId, ContractNumber = "CON-REVIEW", Status = "Active",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31),
        };
        db.EmployeeContracts.Add(underReview);
        db.ContractRenewalCases.Add(new ContractRenewalCase
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.PublicId, ExpiringContractId = underReview.Id,
            ExpiringEndDate = new DateOnly(2026, 12, 31), AllowedActions = [ContractActions.RenewAsIs], NoticeDueOn = new DateOnly(2026, 11, 1),
        });
        await db.SaveChangesAsync();
        var hook = new RecordingHook();
        var controller = new MigrationImportController(db, new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(),
            new Zayra.Api.Infrastructure.Audit.AuditService(db),
            new ContractTermLifecycleDispatcher([hook], new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions()))));
        Bind(controller, tenantId);
        const string header = "EmployeeCode,ContractNumber,ContractType,Status,StartDate,EndDate,BasicSalary,CurrencyCode\n";

        var result = await controller.Commit(new MigrationPackageRequest("ra-import", new Dictionary<string, string>
        {
            ["contracts"] = header + "E-1,CON-REVIEW,Employment,Active,2026-01-01,2027-06-30,9000,SAR\n"
                                   + "E-1,CON-NEW,Employment,Active,2027-01-01,2027-12-31,9000,SAR\n",
        }), default);

        var dto = (MigrationReconciliationDto)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        dto.Errors.Should().ContainSingle(e => e.Contains("open renewal review"));
        hook.Calls.Should().Be(1, "only the newly imported Active contract is activated");
        (await db.EmployeeContracts.AsNoTracking().SingleAsync(c => c.Id == underReview.Id)).EndDate.Should().Be(new DateOnly(2026, 12, 31));
    }

    private sealed class ThrowingWriter : IEntitlementWriter
    {
        public Task<FreezeResult> FreezeTermAsync(Guid tenantId, Guid contractId, CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task ApplyRenewalAsync(Guid tenantId, RenewalApplyPlan plan, CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task CarryToProvisionalAsync(Guid tenantId, Guid fromContractId, Guid provisionalContractId, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class RecordingHook : IContractTermLifecycle
    {
        public int Calls { get; private set; }
        public List<string> Ended { get; } = [];
        public Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct) { Calls++; return Task.CompletedTask; }
        public Task OnEndedAsync(EmployeeContract contract, string reason, CancellationToken ct) { Ended.Add(reason); return Task.CompletedTask; }
    }

    [Fact]
    public async Task EndingATerm_RunsTheEndHook_ForExpiryTerminationAndSupersede()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        EmployeeContract Active(string number) => new()
        {
            TenantId = tenantId, CompanyId = Guid.NewGuid(), EmployeeId = Guid.NewGuid(), ContractNumber = number, Status = "Active",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31),
        };
        var expiring = Active("CON-E");
        var terminating = Active("CON-T");
        var superseded = Active("CON-S");
        db.EmployeeContracts.AddRange(expiring, terminating, superseded);
        await db.SaveChangesAsync();
        var hook = new RecordingHook();
        var controller = Bind(new ContractsController(db,
            new ContractTermLifecycleDispatcher([hook], new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())))), tenantId);

        (await controller.UpdateStatus(expiring.Id, new UpdateContractStatusRequest("Expired", null), default)).Should().BeOfType<OkObjectResult>();
        (await controller.UpdateStatus(terminating.Id, new UpdateContractStatusRequest("Terminated", null), default)).Should().BeOfType<OkObjectResult>();
        await controller.Supersede(superseded.Id, new CreateContractRequest(superseded.EmployeeId, null, null, null, new DateOnly(2025, 6, 1),
            null, 9000m, null, null, null, null), default);

        hook.Ended.Should().Equal(ContractEndReasons.Expired, ContractEndReasons.Terminated, ContractEndReasons.Superseded);
        var dispatcher = new ContractTermLifecycleDispatcher([hook], new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())));
        await dispatcher.Invoking(d => d.OnEndedAsync(expiring, "Retired", default)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static T Bind<T>(T controller, Guid tenantId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "HR Manager"),
                ], "Test")),
            },
        };
        return controller;
    }

    // ── Payroll stays blind to non-paying components; the catalogue seeds are flag-gated ─────────

    [Fact]
    public void BenefitAndFacilityComponents_AreNonPaying_AndNeverReachTheRunCatalogue()
    {
        var tenantId = Guid.NewGuid();
        var seeds = PayComponentCatalog.EntitlementComponentSeeds(tenantId);
        seeds.Should().OnlyContain(c => PayComponentEngine.IsNonPaying(c));
        var resolved = PayComponentEngine.ResolveInEffect(PayComponentCatalog.SystemComponentSeeds(tenantId).Concat(seeds), tenantId,
            new DateOnly(2026, 10, 1));
        resolved.Select(c => c.Code).Should().NotContain(["AIR_TICKET", "MEDICAL", "EDUCATION", "PER_DIEM"]);
        resolved.Should().HaveCount(PayComponentCatalog.SystemComponentSeeds(tenantId).Count);
        PayComponentCatalog.SystemComponentSeeds(tenantId).Should().OnlyContain(c => c.EntitlementClass == PayEntitlementClasses.None,
            "the default catalogue of tenants without release_a must not move");
    }

    [Fact]
    public async Task EnsureEntitlementCatalog_ClassifiesTheWageRows_AddsTheBenefits_AndIsIdempotent()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var first = await PayComponentSeeder.EnsureEntitlementCatalogAsync(db, tenantId, default);
        await db.SaveChangesAsync();
        first.Added.Should().Be(4);
        first.Classified.Should().Be(3);
        var housing = await db.PayComponents.SingleAsync(c => c.TenantId == tenantId && c.Code == "HOUSING");
        (housing.EntitlementClass, housing.StatutoryFloor, housing.IsOffered).Should().Be((PayEntitlementClasses.QiwaWage, PayStatutoryFloors.Housing, true));
        (await db.PayComponents.SingleAsync(c => c.TenantId == tenantId && c.Code == "MEDICAL")).StatutoryFloor.Should().Be(PayStatutoryFloors.Medical);

        var second = await PayComponentSeeder.EnsureEntitlementCatalogAsync(db, tenantId, default);
        await db.SaveChangesAsync();
        second.Should().Be(new PayComponentSeeder.EntitlementCatalogResult(0, 0));
    }

    [Fact]
    public void StatutoryRules_SeedEveryRenewalKey_ForSaudi()
    {
        var keys = StatutoryRuleSeeder.BuildRules().Where(r => r.CountryCode == Zayra.Api.Application.CountryPack.CountryCodes.Saudi)
            .Select(r => r.RuleKey).ToHashSet();
        keys.Should().Contain(RenewalRuleKeys.All);
        var rules = StatutoryRuleSeeder.BuildRules();
        rules.Single(r => r.RuleKey == RenewalRuleKeys.Art55Reading).RuleValue.Should().Be("conservative");
        rules.Single(r => r.RuleKey == RenewalRuleKeys.Art55MaxConsecutiveRenewals).RuleValue.Should().Be("3");
        rules.Single(r => r.RuleKey == RenewalRuleKeys.AsIsRequiresEmployeeAcceptance).RuleValue.Should().Be("true");
        new RenewalDeadlineRules().Should().Be(new RenewalDeadlineRules(
            int.Parse(rules.Single(r => r.RuleKey == RenewalRuleKeys.RenewalLeadDays).RuleValue),
            int.Parse(rules.Single(r => r.RuleKey == RenewalRuleKeys.OfferLeadDays).RuleValue),
            int.Parse(rules.Single(r => r.RuleKey == RenewalRuleKeys.QiwaSubmitLeadDays).RuleValue),
            int.Parse(rules.Single(r => r.RuleKey == RenewalRuleKeys.QiwaGateLeadDays).RuleValue),
            int.Parse(rules.Single(r => r.RuleKey == RenewalRuleKeys.DefaultNonRenewalNoticeDays).RuleValue),
            int.Parse(rules.Single(r => r.RuleKey == RenewalRuleKeys.QiwaContractResponseDays).RuleValue)),
            "the formula defaults and the seeded platform rows are one fact");
    }

    [Fact]
    public async Task HrDirectorAndHrManager_HoldTheFourReleaseAKeys_OthersDoNot()
    {
        var (db, tenantId) = await Security.SeededRoleBundles.NewTenantAsync("release-a");
        await using var _ = db;
        string[] keys = ["entitlements.read", "entitlements.manage", "contracts.renewal.read", "contracts.renewal.manage"];
        foreach (var role in new[] { "Admin", "HR Director", "HR Manager" })
            (await Security.SeededRoleBundles.PermissionsOfAsync(db, tenantId, role)).Should().Contain(keys, role);
        foreach (var role in new[] { "Payroll Manager", "HR Officer", "Finance", "Employee" })
            (await Security.SeededRoleBundles.PermissionsOfAsync(db, tenantId, role)).Should().NotContain(keys, role);
    }

    private static string Signature(Type type, string method)
    {
        var m = type.GetMethod(method)!;
        static string Name(Type t) => t.IsGenericType ? $"{t.Name}[{string.Join(",", t.GetGenericArguments().Select(a => a.Name))}]" : t.Name;
        var ret = m.ReturnType.IsGenericType && m.ReturnType.GetGenericArguments()[0].IsGenericType
            ? $"{m.ReturnType.Name}[{m.ReturnType.GetGenericArguments()[0].Name}]"
            : Name(m.ReturnType);
        return $"{ret} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})";
    }

    private static string[] Properties<T>() =>
        typeof(T).GetConstructors().Single().GetParameters().Select(p => $"{p.Name}:{p.ParameterType.Name}").ToArray();

    private static void Registered<TService, TImpl>(IServiceCollection services) =>
        services.Should().ContainSingle(d => d.ServiceType == typeof(TService))
            .Which.Should().Match<ServiceDescriptor>(d => d.ImplementationType == typeof(TImpl) && d.Lifetime == ServiceLifetime.Scoped);
}
