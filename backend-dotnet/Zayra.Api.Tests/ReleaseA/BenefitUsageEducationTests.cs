using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Entitlements.Usage;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The education claim, through the same request → Finance decision flow (no payment). The frozen EDUCATION row is an amount per
/// child per contract year; the request names the child (up to the grade's child limit, max_dependants); each child's requests stay
/// within that child's amount; the employee it is for never decides.
/// </summary>
public sealed class BenefitUsageEducationTests
{
    private static async Task<BenefitUsageRefusedException> Refused(Func<Task> act) =>
        (await act.Should().ThrowAsync<BenefitUsageRefusedException>()).Which;

    /// <summary>SAR 20,000 a child a year for at most two children; Mohammed has a son, a daughter (seeded) and a younger son added here.</summary>
    private static async Task<(BenefitUsageKit Kit, Guid Son, Guid Daughter, Guid Youngest, Guid Wife)> SeededAsync(short? cap = 2)
    {
        var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("EDUCATION", GradeEntitlementValueTypes.Amount, amount: 20_000m, scope: DependantScopes.Children, maxDependants: cap);
        var youngest = new EmployeeDependent { TenantId = k.Seed.TenantId, EmployeeId = k.Seed.Mohammed.Id, FullName = "Youngest", Relationship = "Child",
            DateOfBirth = new DateOnly(2020, 3, 1) };
        k.Db.EmployeeDependents.Add(youngest);
        await k.Db.SaveChangesAsync();
        var deps = await k.Db.EmployeeDependents.AsNoTracking().Where(x => x.EmployeeId == k.Seed.Mohammed.Id).ToListAsync();
        return (k, deps.Single(d => d.FullName == "Son").Id, deps.Single(d => d.FullName == "Daughter").Id, youngest.Id, deps.Single(d => d.FullName == "Wife").Id);
    }

    private static BenefitUsageRequestInput Claim(BenefitUsageKit k, Guid child, decimal amount, string? year = "2026/2027") =>
        k.Money("EDUCATION", amount, dependantId: child) with { AcademicYear = year, ProviderName = "Al Noor International School" };

    [Fact]
    public async Task TheEntitlement_IsTheFrozenAmountPerChild_ForUpToTheChildLimit()
    {
        var (k, son, daughter, youngest, _) = await SeededAsync();
        await using var owned = k;

        var row = await k.RowAsync("EDUCATION");

        (row.Measure, row.Currency, row.PerBeneficiary, row.MaxDependants, row.CanRequest).Should().Be((BenefitUsageMeasures.Amount, "SAR", 20_000m, (short?)2, true));
        row.Entitlement.Should().Be(40_000m, "two children are covered, whatever the number on file");
        row.Beneficiaries.Select(b => b.DependantId).Should().BeEquivalentTo(new Guid?[] { son, daughter, youngest });
        row.Beneficiaries.Should().OnlyContain(b => b.Cap == 20_000m && b.Remaining == 20_000m && !b.Named);
    }

    [Fact]
    public async Task AClaim_RequestedByTheEmployee_ApprovedByFinance_NoMoneyMoves()
    {
        var (k, son, _, _, _) = await SeededAsync();
        await using var owned = k;

        var claim = await k.Service.RequestOwnAsync(k.Seed.TenantId, k.Seed.UserId, k.Seed.Mohammed.Id, Claim(k, son, 12_500m), default);
        var approved = await k.ApproveAsync(claim.Id);

        (approved.Status, approved.DependantName, approved.AcademicYear, approved.ProviderName, approved.Amount, approved.Currency)
            .Should().Be((BenefitUsageStatuses.Approved, "Son", "2026/2027", "Al Noor International School", 12_500m, "SAR"));
        var row = await k.RowAsync("EDUCATION");
        (row.Approved, row.Remaining).Should().Be((12_500m, 27_500m));
        row.Beneficiaries.Single(b => b.DependantId == son).Should().Match<BenefitUsageBeneficiaryDto>(b => b.Used == 12_500m && b.Remaining == 7_500m && b.Named);
        (await k.Db.PayrollRuns.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Refused_OverOneChildsAmount_EvenWhenTheFamilyTotalHasRoom()
    {
        var (k, son, _, _, _) = await SeededAsync();
        await using var owned = k;
        await k.Service.RequestAsync(k.Hr(), Claim(k, son, 15_000m), default);

        (await Refused(() => k.Service.RequestAsync(k.Hr(), Claim(k, son, 5_000.01m), default))).Code.Should().Be(BenefitUsageErrors.OverEntitlement);
        (await k.Service.RequestAsync(k.Hr(), Claim(k, son, 5_000m), default)).Status.Should().Be(BenefitUsageStatuses.Pending, "partial claims up to the cap");
    }

    [Fact]
    public async Task Refused_ForAThirdChild_WhenTheLimitIsTwo_UntilANamedChildsRequestIsWithdrawn()
    {
        var (k, son, daughter, youngest, _) = await SeededAsync();
        await using var owned = k;
        await k.Service.RequestAsync(k.Hr(), Claim(k, son, 1_000m), default);
        var daughters = await k.Service.RequestAsync(k.Hr(), Claim(k, daughter, 1_000m), default);

        (await Refused(() => k.Service.RequestAsync(k.Hr(), Claim(k, youngest, 1_000m), default))).Code.Should().Be(BenefitUsageErrors.DependantCap);
        (await k.Service.RequestAsync(k.Hr(), Claim(k, son, 1_000m), default)).Status.Should().Be(BenefitUsageStatuses.Pending, "a named child can claim again");

        await k.Service.CancelAsync(k.Hr(), daughters.Id, "Wrong child", default);
        (await k.Service.RequestAsync(k.Hr(), Claim(k, youngest, 1_000m), default)).Status.Should().Be(BenefitUsageStatuses.Pending);
    }

    [Fact]
    public async Task Refused_WithoutAChild_ForTheSpouse_ForAnotherEmployeesChild_OrARemovedChild()
    {
        var (k, _, _, _, wife) = await SeededAsync();
        await using var owned = k;
        var stranger = new EmployeeDependent { TenantId = k.Seed.TenantId, EmployeeId = k.Seed.Colleague.Id, FullName = "Colleague's son", Relationship = "Child" };
        var removed = new EmployeeDependent { TenantId = k.Seed.TenantId, EmployeeId = k.Seed.Mohammed.Id, FullName = "Removed", Relationship = "Child", IsDeleted = true };
        k.Db.EmployeeDependents.AddRange(stranger, removed);
        await k.Db.SaveChangesAsync();

        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Money("EDUCATION", 100m) with { AcademicYear = "2026/2027" }, default))).Code
            .Should().Be(BenefitUsageErrors.DependantRequired);
        foreach (var other in new[] { wife, stranger.Id, removed.Id })
            (await Refused(() => k.Service.RequestAsync(k.Hr(), Claim(k, other, 100m), default))).Code.Should().Be(BenefitUsageErrors.DependantInvalid);
    }

    [Fact]
    public async Task Refused_WithoutTheAcademicYear()
    {
        var (k, son, _, _, _) = await SeededAsync();
        await using var owned = k;

        (await Refused(() => k.Service.RequestAsync(k.Hr(), Claim(k, son, 100m, year: null), default))).Code.Should().Be(BenefitUsageErrors.AcademicYearRequired);
    }

    [Fact]
    public async Task TheSubjectCannotDecideTheirOwnChildsClaim()
    {
        var (k, son, _, _, _) = await SeededAsync();
        await using var owned = k;
        var claim = await k.Service.RequestAsync(k.Hr(), Claim(k, son, 500m), default);

        (await Refused(() => k.Service.DecideAsync(k.Subject(), claim.Id, "Approved", null, default))).Code.Should().Be(SubjectDecisionBar.ErrorCode);
    }

    [Fact]
    public async Task WithNoChildLimit_EveryChildOnFileIsCovered()
    {
        var (k, _, _, _, _) = await SeededAsync(cap: null);
        await using var owned = k;

        (await k.RowAsync("EDUCATION")).Entitlement.Should().Be(60_000m);
    }

    [Fact]
    public async Task TheEmployeeSees_EachChildsAmountUsedAndLeft()
    {
        var (k, son, _, _, _) = await SeededAsync();
        await using var owned = k;
        await k.ApproveAsync((await k.Service.RequestAsync(k.Hr(), Claim(k, son, 8_000m), default)).Id);

        var mine = await k.Service.OwnAsync(k.Seed.TenantId, k.Seed.Mohammed.Id, k.Seed.UserId, default);

        var education = mine.Benefits.Single(b => b.BenefitCode == "EDUCATION");
        (education.Entitlement, education.Approved, education.Remaining).Should().Be((40_000m, 8_000m, 32_000m));
        education.Beneficiaries.Single(b => b.Name == "Son").Should().Match<BenefitUsageBeneficiaryDto>(b => b.Used == 8_000m && b.Remaining == 12_000m);
        mine.Requests.Single().Should().Match<EssBenefitUsageRequestDto>(u => u.Status == BenefitUsageStatuses.Approved && u.ForName == "Son");
    }
}
