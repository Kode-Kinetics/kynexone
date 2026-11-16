using Microsoft.EntityFrameworkCore;
using K = Zayra.Api.Tests.OpeningBalanceImportKit;

namespace Zayra.Api.Tests;

/// <summary>
/// A carried-in advance's schedule adds up to the cent: the instalments already repaid sum exactly to what was repaid (700 over
/// three is 233.33, 233.33, 233.34, never 699.99), and the instalments still to pay sum exactly to the outstanding balance with
/// no row of zero (200 over three at 100 is 66.66, 66.66, 66.68, never 100, 100, 0.00). The last row absorbs the remainder.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceScheduleRoundingPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ACarriedInAdvanceSchedule_SumsToTheCent_AndHasNoZeroRow()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "RND");
        await K.CommitAsync(fixture, b, "advances",
            K.AdvanceCsv(b, number, paid: 3, outstanding: 200m, cutover, original: 900m, instalment: 100m, total: 6), withCutover: true);

        var advance = await K.AdvanceAsync(fixture, b, number);
        await using var db = fixture.CreateDb();
        var rows = await db.AdvanceInstallments.AsNoTracking().Where(i => i.AdvanceId == advance.Id).OrderBy(i => i.InstallmentNumber).ToListAsync();
        Assert.Equal(6, rows.Count);
        var paid = rows.Where(i => i.Status == "Paid").ToList();
        var pending = rows.Where(i => i.Status == "Pending").ToList();
        Assert.Equal(700m, paid.Sum(i => i.AmountPaid));
        Assert.Equal(700m, paid.Sum(i => i.AmountDue));
        Assert.Equal(200m, pending.Sum(i => i.AmountDue));
        Assert.All(rows, i => Assert.True(i.AmountDue > 0m, $"instalment {i.InstallmentNumber} is {i.AmountDue}"));
        Assert.Equal(new[] { 233.33m, 233.33m, 233.34m }, paid.Select(i => i.AmountDue));
        Assert.Equal(new[] { 66.66m, 66.66m, 66.68m }, pending.Select(i => i.AmountDue));
    }
}
