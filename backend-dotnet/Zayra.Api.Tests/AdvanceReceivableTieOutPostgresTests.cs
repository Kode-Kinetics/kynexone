using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Review P2-4: the advance receivable tie-out read every 1410 journal line of a company into memory and summed there, a load that
/// grows with the ledger. On PostgreSQL the debits, credits and the settlement reclass are now summed by the database and one row
/// comes back per company; the figures are the ones the in-memory sum gave (<see cref="AdvanceReceivableTieOutTests"/> holds the
/// behaviour on the SQLite harness, which cannot sum a decimal column and takes the rows).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AdvanceReceivableTieOutPostgresTests(PostgresFixture fixture)
{
    private const string Adv1410 = "1410 - Employee Salary Advances";
    private const string Cash1000 = "1000 - Cash/Bank";
    private const string Emp1420 = "1420 - Employee Overpayment Receivable";

    [Fact]
    public async Task TheGlBalanceAndTheReclass_AreSummedInTheDatabase_WithoutReadingTheLinesBack_AndTheFiguresAreUnchanged()
    {
        Guid tenant, companyA, companyB;
        await using (var seed = fixture.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            var a = NewCompany(tenant, "Tie Out A");
            var b = NewCompany(tenant, "Tie Out B");
            seed.Companies.AddRange(a, b);
            companyA = a.Id; companyB = b.Id;
            await seed.SaveChangesAsync();
            seed.SalaryAdvances.Add(new SalaryAdvance
            {
                TenantId = tenant, CompanyId = companyA, EmployeeId = Guid.NewGuid(), EmployeeIntId = 1, EmployeeName = "E",
                AdvanceNumber = $"ADV-T-{Guid.NewGuid():N}"[..16], RequestedAmount = 700m, ApprovedAmount = 700m,
                OutstandingBalance = 700m, Status = "Active",
            });
            seed.FinanceGlEntries.AddRange(
                Gl(tenant, companyA, Adv1410, Cash1000, 1_000m, "Disbursement"),
                Gl(tenant, companyA, "2107 - Loan & Advance Deductions Payable", Adv1410, 300m, "PayrollLock"),
                Gl(tenant, companyA, Emp1420, Adv1410, 250m, GlEventTypes.SettlementResidualReclass),
                Gl(tenant, companyA, Emp1420, Adv1410, 0.10m, GlEventTypes.SettlementResidualReclass),
                Gl(tenant, companyA, "2100 - Salaries Payable", Cash1000, 9_999m, "NetSettlement"),   // not this account
                Gl(tenant, companyB, Adv1410, Cash1000, 5_000m, "Disbursement"));                     // not this company
            await seed.SaveChangesAsync();
        }

        var statements = new SqlCapture();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(fixture.ConnectionString, PostgresFixture.ProductionProviderOptions).AddInterceptors(statements).Options);

        var a1 = (await AdvanceReceivableTieOut.ComputeAsync(db, tenant, new[] { companyA }, CancellationToken.None)).Single();

        Assert.Equal((700m, 449.90m, -250.10m, 250.10m), (a1.SubLedgerOutstanding, a1.GlBalance, a1.Difference, a1.ReclassedTo1420));
        Assert.False(a1.IsTiedOut);
        Assert.True(a1.ExplainedByReclass);
        Assert.False(a1.Flagged);
        var ledgerReads = statements.Commands.Where(c => c.Contains("finance_gl_entries", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(ledgerReads);
        Assert.All(ledgerReads, c => Assert.Contains("SUM(", c, StringComparison.OrdinalIgnoreCase));

        // A company with no line on the account still reports (zero), and the unattributed bucket is skipped when empty.
        var all = await AdvanceReceivableTieOut.ComputeAsync(db, tenant, null, CancellationToken.None);
        Assert.Equal(5_000m, all.Single(r => r.CompanyId == companyB).GlBalance);
        Assert.DoesNotContain(all, r => r.CompanyId is null);
    }

    private static Company NewCompany(Guid tenant, string name) => new()
    {
        TenantId = tenant, LegalNameEn = name, CountryCode = "SAU", Jurisdiction = "KSA-mainland",
        RegistrationNumber = $"TO-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static FinanceGlEntry Gl(Guid tenant, Guid company, string dr, string cr, decimal amount, string eventType) => new()
    {
        TenantId = tenant, CompanyId = company, SourceModule = "Advance", SourceEntityId = Guid.NewGuid(), EventType = eventType,
        DebitAccount = dr, CreditAccount = cr, Amount = amount, Currency = "SAR",
        EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = DateTime.UtcNow.ToString("yyyy-MM"),
    };

    private sealed class SqlCapture : DbCommandInterceptor
    {
        private readonly List<string> _commands = new();
        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        { lock (_commands) _commands.Add(command.CommandText); return result; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        { lock (_commands) _commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
