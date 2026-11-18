using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Compensation;

/// <summary>
/// The merit worksheet plans pay on an appraisal rating, so it may only read a rating that is FINAL: published
/// (or acknowledged / closed) and not under an open appeal. A review an upheld appeal sent back to Final Approval
/// for revision, a review under appeal, and a review HR withdrew for correction are not final, and the
/// employee is Unrated until the corrected result is re-issued. The talent review (PerformanceBands) reads the
/// same rule from the same place.
/// </summary>
public sealed class MeritFinalRatingTests
{
    public enum AppealState { None, Open, Rejected, Upheld }

    [Theory]
    [InlineData("Published", AppealState.None, "Exceeds")]
    [InlineData("Acknowledged", AppealState.None, "Exceeds")]
    [InlineData("Published", AppealState.Rejected, "Exceeds")]    // the published result stood
    [InlineData("Acknowledged", AppealState.Rejected, "Exceeds")]
    [InlineData("Appealed", AppealState.Open, "Unrated")]          // under appeal
    [InlineData("Published", AppealState.Open, "Unrated")]         // review moved on while the appeal is still open
    [InlineData("FinalApproval", AppealState.Upheld, "Unrated")]   // withdrawn for revision, published once before
    [InlineData("FinalApproval", AppealState.None, "Unrated")]     // withdrawn by HR for correction, published once before
    public async Task MeritBasis_ReadsOnlyAFinalRating(string status, AppealState appeal, string expected)
    {
        await using var db = NewDb();
        var w = await SeedAsync(db, status, appeal);

        var ctx = await new MeritBasisLoader(db).LoadAsync(w.Cycle, default);

        ctx.Employees[w.EmployeeId].Rating.Should().Be(expected);
    }

    [Fact]
    public async Task MeritBasis_DoesNotFallBackToAnOlderRating_WhileTheLatestIsUnderRevision()
    {
        await using var db = NewDb();
        var w = await SeedAsync(db, "FinalApproval", AppealState.Upheld);
        // An older, final rating from the previous period.
        var older = new PerformanceCycle { TenantId = w.Tenant, Name = "FY2025", ReviewPeriodStart = new DateOnly(2025, 1, 1), ReviewPeriodEnd = new DateOnly(2025, 6, 30), Status = "Closed" };
        db.PerformanceCycles.Add(older);
        db.AppraisalReviews.Add(new AppraisalReview
        {
            TenantId = w.Tenant, CycleId = older.Id, EmployeeId = w.EmployeeId, FinalRating = "Outstanding", Status = "Acknowledged",
            PublishedAt = new DateTime(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        var ctx = await new MeritBasisLoader(db).LoadAsync(w.Cycle, default);

        ctx.Employees[w.EmployeeId].Rating.Should().Be("Unrated",
            "the current period's rating is being revised; last year's rating is not a stand-in for it");
    }

    [Theory]
    [InlineData("Published", AppealState.Rejected, true)]
    [InlineData("FinalApproval", AppealState.Upheld, false)]
    [InlineData("Appealed", AppealState.Open, false)]
    [InlineData("Published", AppealState.None, true)]
    public async Task TalentBands_ReadTheSameRule(string status, AppealState appeal, bool eligible)
    {
        await using var db = NewDb();
        var w = await SeedAsync(db, status, appeal);

        var latest = await PerformanceBands.LatestEligibleAsync(db, w.Tenant, w.EmployeeId, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), default);

        (latest is not null).Should().Be(eligible);
    }

    [Fact]
    public void TheRuleLivesInOnePlace()
    {
        PerformanceBands.EligibleReviewStatuses.Should().BeSameAs(FinalRatingRule.FinalStatuses);
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record World(Guid Tenant, int EmployeeId, CompCycle Cycle);

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"merit-final-{Guid.NewGuid():N}").Options);

    private static async Task<World> SeedAsync(ZayraDbContext db, string status, AppealState appeal)
    {
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "merit-final", Slug = $"mf-{Guid.NewGuid():N}"[..20] });
        var company = new Company { TenantId = tenant, LegalNameEn = "Merit Co", RegistrationNumber = $"MF-{Guid.NewGuid():N}"[..20], CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeCode = $"MF{Guid.NewGuid():N}"[..12], FullName = "Rated Person",
            Status = "Active", JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        var perf = new PerformanceCycle { TenantId = tenant, Name = "FY2026", ReviewPeriodStart = new DateOnly(2026, 1, 1), ReviewPeriodEnd = new DateOnly(2026, 6, 30), Status = "Published" };
        db.PerformanceCycles.Add(perf);
        await db.SaveChangesAsync();

        var review = new AppraisalReview
        {
            TenantId = tenant, CycleId = perf.Id, EmployeeId = employee.Id, FinalRating = "Exceeds", Status = status,
            PublishedAt = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc), IsAppealed = appeal != AppealState.None,
        };
        db.AppraisalReviews.Add(review);
        if (appeal != AppealState.None)
            db.AppraisalAppeals.Add(new AppraisalAppeal
            {
                TenantId = tenant, ReviewId = review.Id, EmployeeId = employee.Id, AppealReason = "Under-rated",
                Status = appeal switch { AppealState.Open => "Submitted", AppealState.Rejected => "Rejected", _ => "Upheld" },
            });
        await db.SaveChangesAsync();

        var cycle = new CompCycle
        {
            TenantId = tenant, CompanyId = company.Id, Name = "Merit 2026", Currency = "SAR",
            PerformancePeriodStart = new DateOnly(2025, 11, 1), PerformancePeriodEnd = new DateOnly(2026, 10, 31),
            EffectiveDate = new DateOnly(2026, 11, 1),
        };
        return new World(tenant, employee.Id, cycle);
    }
}
