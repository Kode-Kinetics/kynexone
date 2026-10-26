using System.Reflection;
using FluentAssertions;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C2, property style (no new dependency: seeded loops, the seed is in every failure message).
/// T-014 / T-069: the cost-centre lines add up to the run's component totals to the last minor unit and create no ROUNDING line.
/// T-068: debits equal credits exactly. T-032: the same at the 3-dp scale of KWD/BHD/OMR. The bonus clearing remainder is divided by
/// largest remainder and never leaks a halala.
/// </summary>
public class PayrollCostingPropertyTests
{
    private static readonly MethodInfo Build = typeof(PayrollController).GetMethod("BuildPayrollGlEntries", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static (List<FinanceGlEntry> Lines, int Seed) Run(int seed, string currency, out decimal componentTotal, out Dictionary<string, decimal> byComponent, out decimal bonusRows)
    {
        var rng = new Random(seed);
        var scale = currency is "KWD" or "BHD" or "OMR" ? 3 : 2;
        decimal Money(int max) => Math.Round((decimal)(rng.NextDouble() * max), scale);

        var ccs = Enumerable.Range(0, rng.Next(1, 13)).Select(i => (Id: Guid.NewGuid(), Code: $"CC-{i:00}")).ToList();
        var info = ccs.ToDictionary(c => c.Id, c => new GlCostCentreInfo(c.Code, c.Code, null, null, null, true));
        var runId = Guid.NewGuid();
        var earnings = new List<PayrollEarning>();
        var deductions = new List<PayrollDeduction>();
        byComponent = new Dictionary<string, decimal>();
        bonusRows = 0m;
        decimal net = 0m;
        var employees = rng.Next(1, 201);
        for (var e = 1; e <= employees; e++)
        {
            var cc = ccs[rng.Next(ccs.Count)].Id;
            foreach (var code in new[] { "BASIC", "HOUSING", "TRANSPORT", "OVERTIME" })
            {
                var amount = Money(code == "BASIC" ? 20_000 : 3_000);
                if (amount == 0m) continue;
                earnings.Add(new PayrollEarning { Id = Guid.NewGuid(), EmployeeId = e, ComponentCode = code, Amount = amount, Source = "Salary", CostCenterId = cc });
                byComponent[code] = byComponent.GetValueOrDefault(code) + amount;
                net += amount;
            }
            if (rng.Next(4) == 0)
            {
                var bonus = Money(2_000);
                if (bonus > 0m)
                {
                    earnings.Add(new PayrollEarning { Id = Guid.NewGuid(), EmployeeId = e, ComponentCode = "BONUS_PERF", Amount = bonus, Source = "Bonus", CostCenterId = cc });
                    bonusRows += bonus; net += bonus;
                }
            }
            var gosi = Money(1_500);
            deductions.Add(new PayrollDeduction { Id = Guid.NewGuid(), EmployeeId = e, ComponentCode = "GOSI-ANN-EE", Source = "Statutory", Amount = gosi });
            deductions.Add(new PayrollDeduction { Id = Guid.NewGuid(), EmployeeId = e, ComponentCode = "GOSI-ANN-ER", Source = "Statutory", Amount = gosi, IsEmployerContribution = true, CostCenterId = cc });
            var absence = rng.Next(5) == 0 ? Money(300) : 0m;
            if (absence > 0m)
                deductions.Add(new PayrollDeduction { Id = Guid.NewGuid(), EmployeeId = e, ComponentCode = "ATTENDANCE", Source = "Attendance", Amount = absence, CostCenterId = cc });
            net -= gosi + absence;
        }

        var ctx = new GlResolutionContext(new Dictionary<string, (string, string)>(), new Dictionary<string, (string, string)>(), Array.Empty<GlDriverRow>())
        {
            CostAccountingEnabled = true, CostCentres = info, CompanyId = Guid.NewGuid(),
            BonusClearings = bonusRows > 0m && rng.Next(2) == 0
                ? new[] { new BonusAccrualClearing(Guid.NewGuid(), "BON", "2300 - Bonus Payable", Math.Round(bonusRows * (decimal)rng.NextDouble(), scale), Guid.NewGuid()) }
                : Array.Empty<BonusAccrualClearing>(),
        };
        componentTotal = earnings.Where(e => e.Source != "Bonus").Sum(e => e.Amount);
        var result = Build.Invoke(null, new object?[] { Guid.NewGuid(), runId, "2026-09", earnings, deductions, net, null, "t", currency, ctx })!;
        var lines = (List<FinanceGlEntry>)result.GetType().GetField("Item1")!.GetValue(result)!;
        return (lines, seed);
    }

    [Theory]
    [InlineData("SAR")]
    [InlineData("KWD")]
    public void T014_T068_T069_CostCentreLinesAddUpExactly_AndTheJournalBalances_ForSeededRandomRuns(string currency)
    {
        var scale = currency == "KWD" ? 3 : 2;
        for (var seed = 1; seed <= 1000; seed++)
        {
            var (lines, _) = Run(20_261_025 + seed, currency, out _, out var byComponent, out var bonusRows);
            var because = $"seed {20_261_025 + seed}, {currency}";

            foreach (var (code, total) in byComponent)
                lines.Where(l => l.Description == $"Payroll earning: {code}").Sum(l => l.Amount).Should().Be(total, because + " component " + code);
            // bonus: the payable clearing plus the un-accrued remainder cover every bonus earning, whatever the split
            if (bonusRows > 0m)
                lines.Where(l => l.EventType == GlEventTypes.BonusPayrollClearing || l.Description.StartsWith("Payroll earning: BONUS_PERF"))
                    .Sum(l => l.Amount).Should().Be(bonusRows, because + " bonus");
            lines.Should().OnlyContain(l => decimal.Round(l.Amount, scale) == l.Amount, because + " scale");
            lines.Should().NotContain(l => l.Description == PayrollGlDescriptions.Rounding, because);
            lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount), because + " DR == CR");
            lines.Select(l => l.PostingKey).Should().OnlyHaveUniqueItems(because).And.NotContainNulls();
            // liability lines are never costed, expense lines always are
            lines.Where(l => (l.DebitAccount + l.CreditAccount).Contains("2100") || (l.DebitAccount + l.CreditAccount).Contains("2101")
                          || (l.DebitAccount + l.CreditAccount).Contains("2106") || (l.DebitAccount + l.CreditAccount).Contains("2300"))
                .Should().OnlyContain(l => l.CostCenterId == null, because);
            lines.Where(l => (l.DebitAccount + l.CreditAccount).Contains("5001") || (l.DebitAccount + l.CreditAccount).Contains("5101"))
                .Should().OnlyContain(l => l.CostCenterId != null, because);
        }
    }

    [Fact]
    public void ACostingOffContext_ProducesOneLinePerComponent_AndNoKey()
    {
        var earnings = new List<PayrollEarning>
        {
            new() { EmployeeId = 1, ComponentCode = "BASIC", Amount = 100m, CostCenterId = Guid.NewGuid() },   // a stale stamp must be ignored
            new() { EmployeeId = 2, ComponentCode = "BASIC", Amount = 50m, CostCenterId = Guid.NewGuid() },
        };
        var result = Build.Invoke(null, new object?[] { Guid.NewGuid(), Guid.NewGuid(), "2026-09", earnings, new List<PayrollDeduction>(), 150m, null, "t", "SAR", GlResolutionContext.Empty })!;
        var lines = (List<FinanceGlEntry>)result.GetType().GetField("Item1")!.GetValue(result)!;

        lines.Should().HaveCount(2);
        lines.Single(l => l.DebitAccount != "").Amount.Should().Be(150m);
        lines.Should().OnlyContain(l => l.CostCenterId == null && l.PostingKey == null);
    }

    [Fact]
    public void T021_TheEosbProvisionPlanner_SplitsTheExpensePerCostCentre_AndLeavesTheProvisionUncosted()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var gl = new GlResolutionContext(new Dictionary<string, (string, string)>(), new Dictionary<string, (string, string)>(), Array.Empty<GlDriverRow>())
        {
            CostAccountingEnabled = true,
            CostCentres = new Dictionary<Guid, GlCostCentreInfo> { [a] = new("CC-A", "A", "4100", null, null, true), [b] = new("CC-B", "B", null, null, null, true) },
        };
        var obligations = new[]
        {
            new EosbObligation(1, "E1", 1000m, 0m, true, 8000m, 2m, "Art84", a),
            new EosbObligation(2, "E2", 500m, 0m, true, 4000m, 1m, "Art84", b),
            new EosbObligation(3, "E3", 250m, 0m, true, 2000m, 1m, "Art84", a),
        };

        var (lines, snapshots) = EosbProvisionPlanner.Build(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2026-09", new DateOnly(2026, 9, 30), "SAR",
            "5110 - End of Service Benefit Expense", "2310 - End of Service Benefit Provision", obligations, null, "t", new DateOnly(2026, 9, 30), new EosbCostContext(gl));

        var expense = lines.Where(l => l.DebitAccount.StartsWith("5110")).OrderBy(l => l.CostCenterCode).ToList();
        expense.Select(l => (l.CostCenterCode, l.Amount)).Should().Equal(("CC-A", 1250m), ("CC-B", 500m));
        expense.First().CostCenterGlSegment.Should().Be("4100");
        lines.Where(l => l.CreditAccount.StartsWith("2310")).Should().HaveCount(3).And.OnlyContain(l => l.CostCenterId == null);
        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        lines.Select(l => l.PostingKey).Should().OnlyHaveUniqueItems().And.NotContainNulls();
        snapshots.Select(s => s.CostCenterId).Should().Equal(a, b, a);
    }
}
