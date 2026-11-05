using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.Recon;

/// <summary>The SQL the reconciliation sends, read from the real database command log.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class ManpowerReconQueryShapePostgresTests
{
    private readonly PostgresFixture _fx;
    public ManpowerReconQueryShapePostgresTests(PostgresFixture fx) => _fx = fx;

    private static int StatusOf(IActionResult r) => PayrollReconAccessTests.Status(r);

    // ── What the reconciliation reads of an employee ─────────────────────────────────────────────

    [Fact]
    public async Task TheReconciliation_ReadsOnlyTheEmployeeColumnsItClassifiesWith()
    {
        var s = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);
        var commands = new List<string>();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_fx.ConnectionString)
            .LogTo(commands.Add, new[] { Microsoft.EntityFrameworkCore.DbLoggerCategory.Database.Command.Name }, LogLevel.Information).Options);

        StatusOf(await s.Recon(db: db).Get(s.Current.Id, null, default)).Should().Be(200);

        var employeeReads = commands.Where(c => c.Contains("FROM employees ")).ToList();
        employeeReads.Should().NotBeEmpty();
        foreach (var sql in employeeReads)
            sql.Should().Contain("joining_date")
                .And.NotContain("full_name").And.NotContain("work_email").And.NotContain("personal_email").And.NotContain("phone")
                .And.NotContain("salary").And.NotContain("date_of_birth");
    }
}
