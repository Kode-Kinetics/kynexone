using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Controllers.Ess;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Release A slice R2 — the package resolver and the writer's rules, on EF InMemory (the trigger-level proofs are in
/// <see cref="PackageFreezePostgresTests"/>). Mohammed is the storyline's G3: housing 25% = SAR 2,000, medical B with
/// three dependants, one Economy ticket, housing advance 3 × housing = SAR 6,000.
/// </summary>
public sealed class PackageResolverTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static ZayraDbContext InMemory() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"release-a-r2-{Guid.NewGuid():N}").Options);

    private static async Task<(ZayraDbContext Db, PackageSeed Seed)> SeededAsync(Action<PackageSeed>? tweak = null)
    {
        var db = InMemory();
        var seed = new PackageSeed();
        tweak?.Invoke(seed);
        await seed.SaveAsync(db);
        return (db, seed);
    }

    /// <summary>The writer, by default on a day before the term starts (a running term's package is a proposal instead).</summary>
    private static EntitlementWriter Writer(ZayraDbContext db, DateOnly? today = null) =>
        new(db, new FixedTenantClock(today ?? PackageSeed.FreezeDay), new EntitlementResolver(db));

    private static PackageLine Line(EmployeePackage p, string code) => p.Lines.Single(l => l.ComponentCode == code);

    // ── Precedence: one source per class ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolver_ReadsCashFromSalary_FrozenFromTheTerm_PreviewFromTheGrade_AndFacilitiesFromPolicy()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var resolver = new EntitlementResolver(db);

        // Before freezing, contract benefits are a preview of the grade standard.
        var preview = await resolver.ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
        Line(preview, "MEDICAL").Source.Should().Be(PackageLineSources.GradeStandard);

        (await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default)).Should().Be(new FreezeResult(true, false, 2));
        await db.SaveChangesAsync();
        var package = await resolver.ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);

        package.ContractId.Should().Be(s.Term.Id);
        package.TermEndsOn.Should().Be(PackageSeed.TermEnd);
        package.BlockCodes.Should().BeEmpty();

        var housing = Line(package, "HOUSING");
        housing.Source.Should().Be(PackageLineSources.Salary);
        (housing.ValueType, housing.Rate, housing.MonthlyCash).Should().Be((GradeEntitlementValueTypes.PercentOfBasic, 0.25m, 2000m));
        housing.GradeStandardDiffers.Should().BeFalse();
        housing.GradeEntitlementId.Should().Be(s.Cells["HOUSING"].Id, "the 'Why?' popover cites the grade cell");

        // The salary pays transport as a fixed SAR 800; the grade standard says 10% of 8,000 = SAR 800. Same cash, so it is
        // NOT "reviewed at renewal" — the badge compares what the employee gets, not how it is written.
        var transport = Line(package, "TRANSPORT");
        (transport.Source, transport.MonthlyCash, transport.GradeStandardDiffers).Should().Be((PackageLineSources.Salary, 800m, false));

        var medical = Line(package, "MEDICAL");
        (medical.Source, medical.CoverageTier, medical.DependantsCovered).Should().Be((PackageLineSources.ContractFrozen, CoverageTiers.B, 3));
        medical.EmployeeEntitlementId.Should().NotBeNull();
        var ticket = Line(package, "AIR_TICKET");
        (ticket.Source, ticket.Quantity, ticket.CoverageTier, ticket.DependantsCovered).Should().Be((PackageLineSources.ContractFrozen, (short)1, CoverageTiers.Economy, 0));

        var education = Line(package, "EDUCATION");
        (education.Source, education.Eligible, education.ReasonCode).Should().Be((PackageLineSources.GradeStandard, false, PackageReasons.NotInGrade));

        var perDiem = Line(package, "PER_DIEM");
        (perDiem.Source, perDiem.Amount, perDiem.LimitPeriod).Should().Be((PackageLineSources.Facility, 250m, EntitlementLimitPeriods.PerDay));

        var advance = Line(package, "LOAN_HOUSING_ADVANCE");
        (advance.Source, advance.Eligible, advance.ResolvedAmount, advance.Rate).Should().Be((PackageLineSources.Facility, true, 6000m, 3m));
        advance.StandardValue!.GradeEntitlementId.Should().Be(s.Cells["LOAN_HOUSING_ADVANCE"].Id);
        housing.ResolvedAmount.Should().Be(2000m);
        medical.StandardValue!.CoverageTier.Should().Be(CoverageTiers.B, "the grade's standard rides beside the frozen value");
        package.Lines.Where(l => !l.Eligible || !l.Offered).Should().OnlyContain(l => l.ReasonCode != null, "a line that is not given always says why");

        var order = new[] { PayEntitlementClasses.QiwaWage, PayEntitlementClasses.Contractual, PayEntitlementClasses.Facility };
        package.Lines.Select(l => Array.IndexOf(order, l.Class)).Should().BeInAscendingOrder("pay, then contract benefits, then facilities");
    }

    [Fact]
    public async Task CompanyCell_BeatsTheTenantCell_AndACompanySkip_HidesTheBenefitForThatCompanyOnly()
    {
        var (db, s) = await SeededAsync(seed =>
        {
            seed.Cell("PER_DIEM", PayEntitlementClasses.Facility, GradeEntitlementValueTypes.Amount, amount: 200m,
                period: EntitlementLimitPeriods.PerDay, companyId: seed.Company.Id);
        });
        await using var _ = db;
        db.PayComponents.Add(new PayComponent { TenantId = s.TenantId, CompanyId = s.Company.Id, Code = "AIR_TICKET", NameEn = "Annual air ticket",
            ComponentType = PayComponentTypes.Benefit, EntitlementClass = PayEntitlementClasses.Contractual, IsOffered = false });
        await db.SaveChangesAsync();
        var resolver = new EntitlementResolver(db);

        var package = await resolver.ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
        var perDiem = Line(package, "PER_DIEM");
        (perDiem.Amount, perDiem.IsCompanyOverride).Should().Be((200m, true));
        var ticket = Line(package, "AIR_TICKET");
        (ticket.Offered, ticket.Eligible, ticket.ReasonCode).Should().Be((false, false, ReleaseABlockReasons.EntitlementNotOfferedByCompany));

        // The colleague's company does not skip it and has no override.
        var colleague = await resolver.ResolveAsync(s.TenantId, s.Colleague.Id, Today, default);
        (Line(colleague, "AIR_TICKET").Offered, Line(colleague, "PER_DIEM").Amount).Should().Be((true, 250m));

        // A skipped benefit is not frozen into the term.
        await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        db.ChangeTracker.Entries<EmployeeEntitlement>().Select(e => e.Entity.PayComponentCode).Should().BeEquivalentTo("MEDICAL");
    }

    [Fact]
    public async Task ACellOutsideItsDates_IsNotTheStandard_AndAMissingMedicalCell_IsAVisibleGap()
    {
        var (db, s) = await SeededAsync(seed => seed.Cells.Remove("MEDICAL"));
        await using var _ = db;
        var package = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, new DateOnly(2025, 12, 31), default);
        package.Lines.Should().NotContain(l => l.ComponentCode == "PER_DIEM", "the cells start on 1 Jan 2026");
        Line(package, "MEDICAL").ReasonCode.Should().Be(ReleaseABlockReasons.EntitlementCellMissing);
        package.BlockCodes.Should().Contain(ReleaseABlockReasons.EntitlementCellMissing);
    }

    [Fact]
    public async Task NoGrade_IsReportedAsABlock_NotAsAnEmptyPackage()
    {
        var (db, s) = await SeededAsync(seed => seed.Mohammed.GradeId = null);
        await using var _ = db;
        var package = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
        package.BlockCodes.Should().Contain(ReleaseABlockReasons.GradeMissing);
        Line(package, "HOUSING").MonthlyCash.Should().Be(2000m, "cash still comes from the salary row");
        (await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default)).Should().Be(new FreezeResult(false, false, 0));
    }

    [Fact]
    public async Task Criteria_NationalityAndTime_AreExplained_NeverSilentlyDropped()
    {
        var (db, s) = await SeededAsync(seed =>
        {
            seed.Term.WorkerNationalityClass = WorkerNationalityClasses.Saudi; // the term's own stamp, never the free-text nationality
            seed.Cells["PER_DIEM"].MinServiceMonths = 24;
        });
        await using var _ = db;
        var detailed = await new EntitlementResolver(db).ResolveDetailedAsync(s.TenantId, s.Mohammed.Id, Today, default);
        var package = detailed.Package;
        Line(package, "AIR_TICKET").ReasonCode.Should().Be(ReleaseABlockReasons.EntitlementNotEligibleCriteria);
        detailed.Reasons["AIR_TICKET"].Criterion.Should().Be(PackageCriteria.Nationality);
        var perDiem = Line(package, "PER_DIEM");
        (perDiem.ReasonCode, perDiem.EligibleFrom).Should().Be((ReleaseABlockReasons.EntitlementNotEligibleCriteria, (DateOnly?)new DateOnly(2027, 2, 1)));
        detailed.Reasons["PER_DIEM"].Criterion.Should().Be(PackageCriteria.ServiceMonths);
        (await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, new DateOnly(2027, 2, 1), default))
            .Lines.Single(l => l.ComponentCode == "PER_DIEM").Eligible.Should().BeTrue("24 months after 1 Feb 2025");
        // A nationality-excluded benefit is not frozen; the medical floor always is.
        await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        db.ChangeTracker.Entries<EmployeeEntitlement>().Select(e => e.Entity.PayComponentCode).Should().BeEquivalentTo("MEDICAL");
    }

    [Theory]
    [InlineData(DependantScopes.Family, null, 3)]
    [InlineData(DependantScopes.Family, (short)2, 2)]
    [InlineData(DependantScopes.Spouse, null, 1)]
    [InlineData(DependantScopes.Children, null, 2)]
    [InlineData(DependantScopes.None, null, 0)]
    public void DependantsCovered_RespectsScopeAndMaximum_AndIgnoresParents(string scope, short? max, int expected)
    {
        var dependants = new[]
        {
            new EmployeeDependent { Relationship = "Wife" }, new EmployeeDependent { Relationship = " son " },
            new EmployeeDependent { Relationship = "ابنة" }, new EmployeeDependent { Relationship = "Father" },
            new EmployeeDependent { Relationship = "Child", DateOfBirth = new DateOnly(2027, 1, 1) }, // not born yet
        };
        PackageRules.DependantsCovered(scope, max, dependants, Today).Should().Be(expected);
    }

    [Fact]
    public async Task HousingAdvance_IsRefusedWithItsOwnReason_WhenHousingIsInKind()
    {
        var (db, s) = await SeededAsync(seed =>
        {
            seed.Salary.HousingBasis = AllowanceBases.InKind;
            seed.Salary.HousingRate = null;
            seed.Salary.HousingAllowance = 0m;
            seed.Cells["HOUSING"].ValueType = GradeEntitlementValueTypes.InKind;
            seed.Cells["HOUSING"].Rate = null;
        });
        await using var _ = db;
        var package = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
        var advance = Line(package, "LOAN_HOUSING_ADVANCE");
        (advance.Eligible, advance.ResolvedAmount, advance.ReasonCode).Should().Be((false, (decimal?)null, PackageReasons.HousingInKind));
        var housing = Line(package, "HOUSING");
        (housing.ValueType, housing.MonthlyCash, housing.ReasonCode).Should().Be((GradeEntitlementValueTypes.InKind, 0m, (string?)null),
            "in kind is provision under Art. 61, not a floor breach");
    }

    [Fact]
    public async Task HousingWithNeitherCashNorKind_IsAFloorBreach()
    {
        var (db, s) = await SeededAsync(seed =>
        {
            seed.Salary.HousingBasis = AllowanceBases.Amount;
            seed.Salary.HousingRate = null;
            seed.Salary.HousingAllowance = 0m;
        });
        await using var _ = db;
        var package = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
        Line(package, "HOUSING").ReasonCode.Should().Be(ReleaseABlockReasons.EntitlementFloorHousing);
        package.BlockCodes.Should().Contain(ReleaseABlockReasons.EntitlementFloorHousing);
    }

    // ── The writer ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Freeze_WritesOnlyContractualRows_IsIdempotent_AndFixesThemFromTheStartToTheTermEnd()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var first = await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        // Staged but not saved: a second call in the same unit of work sees them.
        (await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default)).Should().Be(new FreezeResult(false, true, 0));
        await db.SaveChangesAsync();
        (await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default)).Should().Be(new FreezeResult(false, true, 0));

        first.Should().Be(new FreezeResult(true, false, 2));
        var rows = await db.EmployeeEntitlements.ToListAsync();
        rows.Select(r => r.PayComponentCode).Should().BeEquivalentTo("MEDICAL", "AIR_TICKET");
        rows.Should().OnlyContain(r => r.EntitlementClass == PayEntitlementClasses.Contractual && r.Source == EntitlementSources.GradeDefault
            && r.VerificationState == EntitlementVerificationStates.Verified && r.EffectiveFrom == PackageSeed.TermStart && r.EffectiveTo == PackageSeed.TermEnd
            && r.EmployeeId == s.Mohammed.PublicId && r.CompanyId == s.Company.Id && r.GradeEntitlementId != null);
    }

    [Fact]
    public async Task Freeze_BeforeTheTermStarts_FixesItFromTheStart_AndAProposalStagesNothing()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        await Writer(db, new DateOnly(2026, 1, 20)).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        db.ChangeTracker.Entries<EmployeeEntitlement>().Should().OnlyContain(e => e.Entity.EffectiveFrom == PackageSeed.TermStart);
        await db.SaveChangesAsync();

        // The bulk path proposes: nothing is staged until HR confirms against the signed contract.
        var proposal = await Writer(db).ProposeAsync(s.TenantId, s.ColleagueTerm.Id, default);
        proposal!.Rows.Select(r => r.ComponentCode).Should().BeEquivalentTo("MEDICAL", "AIR_TICKET");
        db.ChangeTracker.Entries<EmployeeEntitlement>().Should().NotContain(e => e.State == EntityState.Added);
        (await Writer(db).ProposeAsync(s.TenantId, s.Term.Id, default)).Should().BeNull("Mohammed's term is already frozen");

        var written = await Writer(db).WriteConfirmedProposalAsync(s.TenantId, proposal, default);
        await db.SaveChangesAsync();
        written.Should().HaveCount(2).And.OnlyContain(r => r.Source == EntitlementSources.Migrated && r.VerificationState == EntitlementVerificationStates.Verified);
        await Writer(db).Invoking(w => w.WriteConfirmedProposalAsync(s.TenantId, proposal, default))
            .Should().ThrowAsync<EntitlementWriteRefusedException>().Where(e => e.Code == PackageReasons.ProposalClosed);
    }

    [Fact]
    public async Task UnverifiedRows_AreNeverPresentedAsFixed()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        db.EmployeeEntitlements.Add(new EmployeeEntitlement { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId,
            ContractId = s.Term.Id, PayComponentCode = "MEDICAL", ValueType = GradeEntitlementValueTypes.CoverageTier, CoverageTier = CoverageTiers.Vip,
            DependantScope = DependantScopes.Family, Source = EntitlementSources.Migrated, VerificationState = EntitlementVerificationStates.Unverified,
            EffectiveFrom = PackageSeed.TermStart, EffectiveTo = PackageSeed.TermEnd });
        await db.SaveChangesAsync();
        var medical = Line(await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default), "MEDICAL");
        (medical.Source, medical.CoverageTier, medical.EmployeeEntitlementId).Should().Be((PackageLineSources.GradeStandard, CoverageTiers.B, (Guid?)null));
    }

    [Fact]
    public async Task NationalityScopedBenefit_WithNoClassOnTheTerm_NeedsConfirmation_AndIsNotFrozen()
    {
        var (db, s) = await SeededAsync(seed => seed.Term.WorkerNationalityClass = null);
        await using var _ = db;
        var detailed = await new EntitlementResolver(db).ResolveDetailedAsync(s.TenantId, s.Mohammed.Id, Today, default);
        Line(detailed.Package, "AIR_TICKET").ReasonCode.Should().Be(PackageReasons.NationalityUnconfirmed);
        detailed.Package.BlockCodes.Should().Contain(PackageReasons.NationalityUnconfirmed);
        var writer = Writer(db);
        await writer.FreezeTermAsync(s.TenantId, s.Term.Id, default);
        writer.LastSkips.Should().ContainSingle(x => x.ComponentCode == "AIR_TICKET" && x.Code == PackageReasons.NationalityUnconfirmed);
        db.ChangeTracker.Entries<EmployeeEntitlement>().Select(e => e.Entity.PayComponentCode).Should().BeEquivalentTo("MEDICAL");
    }

    [Fact]
    public void EveryReasonCode_IsDescribed_InEnglishAndArabic()
    {
        foreach (var code in PackageReasons.All)
        {
            var reason = PackageReasons.Describe(code);
            reason.Should().NotBeNull(code);
            new[] { reason!.TitleEn, reason.TitleAr, reason.WhyEn, reason.WhyAr, reason.FixEn, reason.FixAr }.Should().OnlyContain(t => t.Length > 3, code);
        }
        PackageReasons.Pending.Keys.Should().NotIntersectWith(ReleaseABlockReasons.All.Keys, "a code R0 adds must be deleted from Pending");
        PackageReasons.All.Where(c => !PackageReasons.Pending.ContainsKey(c))
            .Should().OnlyContain(code => ReleaseABlockReasons.All.ContainsKey(code), "every other R2 code resolves from the R0 catalogue");
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Superseded")]
    [InlineData("PendingApproval")]
    public async Task Writer_RefusesATermThatIsNotInForce(string status)
    {
        var (db, s) = await SeededAsync(seed => seed.Term.Status = status);
        await using var _ = db;
        var refused = await Writer(db).Invoking(w => w.FreezeTermAsync(s.TenantId, s.Term.Id, default))
            .Should().ThrowAsync<EntitlementWriteRefusedException>();
        refused.Which.Code.Should().Be(PackageReasons.ContractNotInForce);
        db.ChangeTracker.Entries<EmployeeEntitlement>().Should().BeEmpty();
    }

    [Fact]
    public async Task AFrozenPackage_IsUnchanged_WhenTheMatrixChangesMidYear_ButIsFlaggedForRenewal()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        await db.SaveChangesAsync();

        // HR publishes medical A for G3 from 1 Nov (close-and-insert, as R1 does).
        var medical = s.Cells["MEDICAL"];
        medical.EffectiveTo = new DateOnly(2026, 10, 31);
        db.GradeEntitlements.Add(new GradeEntitlement { TenantId = s.TenantId, GradeId = s.Grade.Id, PayComponentCode = "MEDICAL",
            EntitlementClass = PayEntitlementClasses.Contractual, Eligible = true, ValueType = GradeEntitlementValueTypes.CoverageTier,
            CoverageTier = CoverageTiers.A, DependantScope = DependantScopes.Family, LimitPeriod = EntitlementLimitPeriods.PerTerm,
            EffectiveFrom = new DateOnly(2026, 11, 1) });
        await db.SaveChangesAsync();

        var after = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, new DateOnly(2026, 12, 1), default);
        var line = Line(after, "MEDICAL");
        (line.Source, line.CoverageTier, line.GradeStandardDiffers).Should().Be((PackageLineSources.ContractFrozen, CoverageTiers.B, true));
        line.GradeEntitlementId.Should().Be(medical.Id, "the frozen row still cites the cell it was fixed from");
        (await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default)).AlreadyFrozen.Should().BeTrue("a re-freeze never rewrites the year");
    }

    [Fact]
    public async Task ApplyRenewal_ClosesTheOldTerm_WritesTheNewOne_SkipsRemoved_AndIsIdempotentPerCase()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        await Writer(db, PackageSeed.TermStart).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        var next = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, ContractNumber = "CON-1b",
            Status = "Active", StartDate = new DateOnly(2027, 2, 1), EndDate = new DateOnly(2028, 1, 31) };
        db.EmployeeContracts.Add(next);
        await db.SaveChangesAsync();
        var plan = new RenewalApplyPlan(Guid.NewGuid(), s.Mohammed.PublicId, s.Company.Id, s.Term.Id, next.Id, next.StartDate, next.EndDate, Guid.NewGuid(),
        [
            new("MEDICAL", RenewalLineActions.Raise, PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.CoverageTier, null, null, null,
                CoverageTiers.A, null, DependantScopes.Family, null, EntitlementLimitPeriods.PerTerm, EntitlementSources.Exception, null, null, null),
            new("AIR_TICKET", RenewalLineActions.Remove, PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.Quantity, null, null, null,
                CoverageTiers.Economy, 1, DependantScopes.None, null, EntitlementLimitPeriods.Annual, EntitlementSources.GradeDefault, s.Cells["AIR_TICKET"].Id, null, null),
            new("HOUSING", RenewalLineActions.Raise, PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.PercentOfBasic, null, 0.30m, null,
                null, null, DependantScopes.None, null, EntitlementLimitPeriods.Monthly, EntitlementSources.Exception, null, null, null),
        ]);

        await Writer(db).ApplyRenewalAsync(s.TenantId, plan, default);
        await db.SaveChangesAsync();
        await Writer(db).ApplyRenewalAsync(s.TenantId, plan, default);
        await db.SaveChangesAsync();

        var rows = await db.EmployeeEntitlements.ToListAsync();
        rows.Where(r => r.ContractId == s.Term.Id).Should().OnlyContain(r => r.EffectiveTo == PackageSeed.TermEnd);
        var renewed = rows.Where(r => r.ContractId == next.Id).ToList();
        renewed.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            PayComponentCode = "MEDICAL", CoverageTier = CoverageTiers.A, Source = EntitlementSources.Exception,
            ApprovalRequestId = plan.ApprovalRequestId, RenewalCaseId = plan.CaseId, EffectiveFrom = next.StartDate, EffectiveTo = next.EndDate,
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task CarryToProvisional_CopiesTheLastDayRows_AsCarried_Once()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        await Writer(db, PackageSeed.TermStart).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        var provisional = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, ContractNumber = "CON-1p",
            Status = "Active", StartDate = new DateOnly(2027, 2, 1), EndDate = null, ProvisionalBasis = ProvisionalBases.DeemedRenewal };
        db.EmployeeContracts.Add(provisional);
        await db.SaveChangesAsync();

        await Writer(db).CarryToProvisionalAsync(s.TenantId, s.Term.Id, provisional.Id, default);
        await db.SaveChangesAsync();
        await Writer(db).CarryToProvisionalAsync(s.TenantId, s.Term.Id, provisional.Id, default);
        await db.SaveChangesAsync();

        var carried = await db.EmployeeEntitlements.Where(x => x.ContractId == provisional.Id).ToListAsync();
        carried.Select(c => c.PayComponentCode).Should().BeEquivalentTo("MEDICAL", "AIR_TICKET");
        carried.Should().OnlyContain(c => c.Source == EntitlementSources.Carried && c.CarriedFromEntitlementId != null
            && c.EffectiveFrom == provisional.StartDate && c.EffectiveTo == null);
    }

    // ── Activation and the employee's own view ──────────────────────────────────────────────────────

    [Fact]
    public async Task ActivatingATerm_FreezesItsPackage_InTheSameSaveChanges()
    {
        var (db, s) = await SeededAsync(seed => seed.Term.Status = "PendingApproval");
        await using var _ = db;
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = s.TenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        var hook = new PackageFreezeOnActivation(Writer(db));
        var dispatcher = new ContractTermLifecycleDispatcher([hook], new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())));
        var controller = Bind(new ContractsController(db, dispatcher), s.TenantId, s.UserId, "HR Manager");

        (await controller.UpdateStatus(s.Term.Id, new UpdateContractStatusRequest("Active", "HR Lead"), default)).Should().BeOfType<OkObjectResult>();

        (await db.EmployeeEntitlements.CountAsync(x => x.ContractId == s.Term.Id)).Should().Be(2);
    }

    [Fact]
    public async Task EssPackage_ShowsOnlyTheCallersOwnPackage_AndCarriesNoIds()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var controller = Bind(new EssPackageController(db, new EntitlementResolver(db), new FixedTenantClock(Today), new NoDeadlines()),
            s.TenantId, s.UserId, "Employee", ("permission", "ess.read"));

        var result = (await controller.Mine(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EssPackageDto>().Subject;
        result.Lines.Single(l => l.ComponentCode == "HOUSING").MonthlyCash.Should().Be(2000m, "Mohammed's, not the colleague's 1,500");
        (result.FixedUntil, result.DependantsOnFile, result.GradeNameAr).Should().Be((PackageSeed.TermEnd, 3, "مشرف"));
        result.Lines.Single(l => l.ComponentCode == "MEDICAL").DependantsCovered.Should().Be(3);
        result.Lines.Single(l => l.ComponentCode == "MEDICAL").LabelAr.Should().Be("التأمين الطبي", "names come from the catalogue, never a raw code");
        result.Lines.Single(l => l.ComponentCode == "LOAN_HOUSING_ADVANCE").Should().BeEquivalentTo(
            new { LabelAr = "سلفة السكن", Group = "facility", ResolvedAmount = 6000m }, o => o.ExcludingMissingMembers());

        // The colleague's login sees the colleague's package; nothing in the route can point elsewhere.
        var other = Bind(new EssPackageController(db, new EntitlementResolver(db), new FixedTenantClock(Today), new NoDeadlines()),
            s.TenantId, s.ColleagueUserId, "Employee", ("permission", "ess.read"));
        var theirs = (await other.Mine(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EssPackageDto>().Subject;
        theirs.Lines.Single(l => l.ComponentCode == "HOUSING").MonthlyCash.Should().Be(1500m);
        typeof(EssPackageController).GetMethod(nameof(EssPackageController.Mine))!.GetParameters()
            .Should().ContainSingle(p => p.ParameterType == typeof(CancellationToken), "no employee id is accepted from the request");

        foreach (var type in new[] { typeof(EssPackageDto), typeof(EssPackageLineDto), typeof(EssPackageWhyDto) })
            type.GetProperties().Should().NotContain(p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?),
                $"{type.Name} must not leak cell, row, grade or employee ids");

        // A login linked to no employee gets a plain refusal, not someone's package.
        var stranger = Bind(new EssPackageController(db, new EntitlementResolver(db), new FixedTenantClock(Today), new NoDeadlines()),
            s.TenantId, Guid.NewGuid(), "Employee", ("permission", "ess.read"));
        (await stranger.Mine(default)).Result.Should().BeOfType<ConflictObjectResult>();
    }

    private sealed class NoDeadlines : IRenewalDeadlineCalculator
    {
        public Task<RenewalDeadlines> ComputeAsync(Guid tenantId, EmployeeContract expiring, CancellationToken ct) =>
            throw new NotImplementedException("R4");
    }

    private static T Bind<T>(T controller, Guid tenantId, Guid userId, string role, params (string Type, string Value)[] extra) where T : ControllerBase
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Role, role),
        };
        claims.AddRange(extra.Select(e => new Claim(e.Type, e.Value)));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    // ── Review round 2 ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewedAtRenewal_ComparesCash_NotHowTheValueIsWritten()
    {
        var (db, s) = await SeededAsync(seed => seed.Cells["TRANSPORT"].Rate = 0.12m); // 12% of 8,000 = 960 ≠ 800
        await using var _ = db;
        var package = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
        Line(package, "TRANSPORT").GradeStandardDiffers.Should().BeTrue();
        Line(package, "HOUSING").GradeStandardDiffers.Should().BeFalse("25% of 8,000 is the 2,000 paid");
    }

    [Fact]
    public async Task ARunningTerm_IsNotFrozenFromTheGradeTable_Directly_ItIsProposed()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var writer = Writer(db, Today); // the term started on 1 Feb
        (await writer.FreezeTermAsync(s.TenantId, s.Term.Id, default)).Should().Be(new FreezeResult(false, false, 0));
        writer.LastSkips.Where(x => x.Code == PackageReasons.TermRunningNeedsProposal).Select(x => x.ComponentCode)
            .Should().BeEquivalentTo("MEDICAL", "AIR_TICKET");
        var proposal = await Writer(db, Today).ProposeAsync(s.TenantId, s.Term.Id, default);
        proposal!.Rows.Should().HaveCount(2).And.OnlyContain(r => r.VerificationState == EntitlementVerificationStates.Unverified,
            "a proposal is unverified until a second HR user confirms it");
    }

    [Fact]
    public async Task FreezingIsIdempotentPerBenefit_ASkippedBenefitIsFrozenOnceItsBlockerClears()
    {
        var (db, s) = await SeededAsync(seed => seed.Term.WorkerNationalityClass = null);
        await using var _ = db;
        (await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default)).RowsWritten.Should().Be(1, "only medical; the ticket waits for the class");
        await db.SaveChangesAsync();
        (await db.EmployeeContracts.SingleAsync(x => x.Id == s.Term.Id)).WorkerNationalityClass = WorkerNationalityClasses.NonSaudi;
        await db.SaveChangesAsync();

        var again = await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        again.Should().Be(new FreezeResult(true, false, 1));
        await db.SaveChangesAsync();
        (await db.EmployeeEntitlements.Select(x => x.PayComponentCode).ToListAsync()).Should().BeEquivalentTo("MEDICAL", "AIR_TICKET");
        (await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default)).Should().Be(new FreezeResult(false, true, 0));
    }

    [Fact]
    public async Task ATerminatedTerm_CarriesNoPackage_AfterTheDayItWasTerminated()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        await Writer(db).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        await db.SaveChangesAsync();
        var term = await db.EmployeeContracts.SingleAsync(x => x.Id == s.Term.Id);
        (term.Status, term.UpdatedAtUtc) = ("Terminated", new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        Line(await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, new DateOnly(2026, 9, 30), default), "MEDICAL")
            .Source.Should().Be(PackageLineSources.ContractFrozen);
        var after = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, new DateOnly(2026, 11, 1), default);
        after.ContractId.Should().BeNull("rows still open past a termination are ignored");
        after.Lines.Should().NotContain(l => l.Source == PackageLineSources.ContractFrozen);
    }

    [Fact]
    public async Task TheHousingAdvanceHonoursTheDateAsked_ThroughTheLoanFormsOwnRules()
    {
        var (db, s) = await SeededAsync(seed => seed.HousingAdvance.MinServiceMonths = 24); // joined 1 Feb 2025
        await using var _ = db;
        var october = Line(await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default), "LOAN_HOUSING_ADVANCE");
        (october.Eligible, october.ReasonCode).Should().Be((false, ReleaseABlockReasons.EntitlementNotEligibleCriteria));
        var march = Line(await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, new DateOnly(2027, 3, 1), default), "LOAN_HOUSING_ADVANCE");
        (march.Eligible, march.ResolvedAmount).Should().Be((true, 6000m));
    }

    [Fact]
    public void DatabaseRefusals_MapToSpecificReasonCodes()
    {
        static DbUpdateException Refusal(string sqlState, string message, string? constraint = null) =>
            new("x", new Npgsql.PostgresException(message, "ERROR", "ERROR", sqlState, constraintName: constraint));
        PackageReasons.FromDatabase(Refusal("23P01", "conflicting key value", "ex_employee_entitlements__no_overlap")).Should().Be(PackageReasons.TermOverlap);
        foreach (var code in new[] { PackageReasons.OutsideTerm, PackageReasons.CompanyMismatch, PackageReasons.BasisNotOwnSalary,
                     PackageReasons.CarriedDiffers, PackageReasons.CarriedOverlaps, PackageReasons.CloseOnly, PackageReasons.ContractNotInForce })
            PackageReasons.FromDatabase(Refusal("23514", $"{code}: entitlement 1 something")).Should().Be(code);
        PackageReasons.FromDatabase(Refusal("23514", "new row violates check", "ck_employee_entitlements__value_shape"))
            .Should().Be(PackageReasons.ContractNotInForce);
        PackageReasons.FromDatabase(Refusal("23505", "dup")).Should().BeNull();
    }

    [Fact]
    public void Money_RoundsHalfAwayFromZero_LikePostgres()
    {
        EntitlementMoney.PercentOf(8000.10m, 0.25m).Should().Be(2000.03m, "round(2000.025, 2) in PostgreSQL; banker's rounding would give 2000.02");
        EntitlementMoney.Round(-0.005m).Should().Be(-0.01m);
    }
}
