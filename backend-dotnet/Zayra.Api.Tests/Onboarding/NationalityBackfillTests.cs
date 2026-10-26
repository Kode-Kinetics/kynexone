using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class NationalityBackfillTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Employee Emp(Guid tenant, string code, string nationality) =>
        new() { TenantId = tenant, EmployeeCode = code, FullName = code, EnglishName = code, Status = "Active", Nationality = nationality };

    [Fact]
    public async Task NationalityBackfill_SameClassAutoConfirmed_ClassChangeProposed_BlankUnresolved()
    {
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        var rows = new[]
        {
            Emp(t, "SAME-SA", "Saudi"), Emp(t, "SAME-IN", "Indian"), Emp(t, "SAME-AE", "UAE"),
            Emp(t, "CHANGE", "الإمارات العربية المتحدة"),      // legacy reads this as expat, the catalog as GCC
            Emp(t, "BLANK", ""), Emp(t, "TYPO", "Suadi"),
        };
        db.Employees.AddRange(rows); await db.SaveChangesAsync();

        (await NationalityBackfill.RunAsync(db, t, default)).Should().Be(6);

        db.ChangeTracker.Clear();
        var by = await db.Employees.IgnoreQueryFilters().ToDictionaryAsync(e => e.EmployeeCode);
        by["SAME-SA"].NationalityMappingState.Should().Be(NationalityMappingStates.Confirmed);
        by["SAME-SA"].NationalityCode.Should().Be("SA");
        by["SAME-IN"].NationalityMappingState.Should().Be(NationalityMappingStates.Confirmed);
        by["SAME-AE"].NationalityCode.Should().Be("AE");
        by["CHANGE"].NationalityMappingState.Should().Be(NationalityMappingStates.ProposedAuto);
        by["BLANK"].NationalityMappingState.Should().Be(NationalityMappingStates.Unresolved);
        by["BLANK"].NationalityCode.Should().BeNull();
        by["TYPO"].NationalityMappingState.Should().Be(NationalityMappingStates.Unresolved);
        by["TYPO"].Nationality.Should().Be("Suadi", "the free text is never overwritten by the backfill");
        by.Values.Should().OnlyContain(e => e.Nationality == rows.First(r => r.EmployeeCode == e.EmployeeCode).Nationality);
    }

    [Fact]
    public async Task Backfill_Idempotent()
    {
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        db.Employees.AddRange(Emp(t, "A", "Saudi"), Emp(t, "B", "Suadi")); await db.SaveChangesAsync();
        (await NationalityBackfill.RunAsync(db, t, default)).Should().Be(2);
        (await NationalityBackfill.RunAsync(db, t, default)).Should().Be(0);
    }

    [Fact]
    public async Task Backfill_NeverTouchesAnotherTenant_AndRunAllCoversEveryTenant()
    {
        await using var db = CreateDb();
        var t1 = Guid.NewGuid(); var t2 = Guid.NewGuid();
        db.Employees.AddRange(Emp(t1, "A", "Saudi"), Emp(t2, "B", "Indian")); await db.SaveChangesAsync();
        await NationalityBackfill.RunAsync(db, t1, default);
        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.EmployeeCode == "B")).NationalityMappingState.Should().Be(NationalityMappingStates.Unset);
        (await NationalityBackfill.RunAllAsync(db, default)).Should().Be(1);
    }

    [Fact]
    public async Task Readiness_ExistingActiveWithBlankNationality_NotBlocked()
    {
        // The nationality requirement is activate-gated: it is consulted on the Draft-to-Active transition only.
        var guard = new Zayra.Api.Infrastructure.Employees.EmployeeActivationGuard(CreateDb());
        guard.ShouldGate("Active", "Active").Should().BeFalse();
        guard.ShouldGate("Draft", "Active").Should().BeTrue();
    }
}
