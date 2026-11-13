using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// M6 — the go-live catch-up of the end-of-service and annual-leave provisions can be booked to OPENING BALANCE EQUITY instead of P&amp;L.
/// Company policy payparameter.provision_catchup_route: Expense (default, the journal as it always was) | OpeningBalance. Only the
/// catch-up of a person whose service began before go-live moves; the month's own accrual, a new joiner's first measurement and every
/// later movement stay expense. The per-employee credit to the provision (2310 / 2330) is identical either way.
/// </summary>
public class ProvisionOpeningRouteTests
{
    private const string EosbProvision = "2310 - End of Service Benefit Provision";
    private const string EosbExpense = "5110 - End of Service Benefit Expense";
    private const string LeaveProvision = "2330 - Accrued Annual Leave Provision";
    private const string LeaveExpense = "5115 - Annual Leave Provision Expense";
    private const string Equity = "3900 - Opening Balance Equity";
    private static readonly DateOnly June = new(2026, 6, 30);
    private static readonly Guid Tenant = Guid.NewGuid(), Company = Guid.NewGuid(), Run = Guid.NewGuid();

    private static EosbObligation E(int id, decimal obligation, decimal carried, bool catchUp, bool opening) =>
        new(id, $"E{id}", obligation, carried, catchUp, 13_500m, 3m, "KSA-LaborLaw-Art84", null, opening);

    private static List<FinanceGlEntry> Eosb(string? equity, params EosbObligation[] o) =>
        EosbProvisionPlanner.Build(Tenant, Company, Run, "2026-06", June, "SAR", EosbExpense, EosbProvision, o, null, "system", June,
            cost: null, openingBalanceAccount: equity).Lines;

    [Fact]
    public void Eosb_OpeningRoute_BooksTheLegacyCatchUpToEquity_AndNothingElse()
    {
        var lines = Eosb(Equity,
            E(1, 20_250m, 0m, catchUp: true, opening: true),     // 3 years of service at go-live
            E(2, 562.50m, 0m, catchUp: true, opening: false),    // joined this month: its first measurement is a real cost
            E(3, 300m, 0m + 0m, catchUp: false, opening: false)); // an ordinary month

        lines.Single(l => l.DebitAccount == Equity).Amount.Should().Be(20_250m);
        lines.Single(l => l.DebitAccount == Equity).Description.Should().Be(EosbProvisionPlanner.OpeningBalanceLabel);
        lines.Single(l => l.DebitAccount == EosbExpense && l.Description == EosbProvisionPlanner.CatchUpLabel).Amount.Should().Be(562.50m);
        lines.Single(l => l.DebitAccount == EosbExpense && l.Description == EosbProvisionPlanner.MonthlyLabel).Amount.Should().Be(300m);
        lines.Where(l => l.CreditAccount == EosbProvision).Select(l => (l.SourceEntityRef, l.Amount))
            .Should().Equal(new[] { ("1", 20_250m), ("2", 562.50m), ("3", 300m) });   // the provision credit per employee is the same either way
        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
    }

    [Fact]
    public void Eosb_WithTheRouteOff_EverythingStaysExpense_ByteForByte()
    {
        var obligations = new[] { E(1, 20_250m, 0m, true, true), E(2, 562.50m, 0m, true, false) };
        var off = Eosb(null, obligations);
        off.Should().NotContain(l => l.DebitAccount == Equity);
        off.Single(l => l.DebitAccount == EosbExpense).Amount.Should().Be(20_812.50m);
        off.Single(l => l.DebitAccount == EosbExpense).Description.Should().Be(EosbProvisionPlanner.CatchUpLabel);
    }

    [Fact]
    public void Eosb_OpeningLine_CarriesNoCostCentre_BecauseEquityIsNotProfitAndLoss()
    {
        var lines = Eosb(Equity, E(1, 1_000m, 0m, true, true));
        lines.Single(l => l.DebitAccount == Equity).CostCenterId.Should().BeNull();
    }

    private static LeaveObligation L(int id, decimal days, decimal rate, decimal carried, bool catchUp, bool opening) =>
        new(id, $"E{id}", days, rate, "Gross", Math.Round(days * rate, 2), carried, catchUp, null, opening);

    private static List<FinanceGlEntry> Leave(string? equity, params LeaveObligation[] o) =>
        LeaveProvisionPlanner.Build(Tenant, Company, Run, "2026-06", June, "SAR", LeaveExpense, LeaveProvision, o, null, "system", June,
            cost: null, openingBalanceAccount: equity).Lines;

    [Fact]
    public void Leave_OpeningRoute_BooksTheLegacyCatchUpToEquity_AndNothingElse()
    {
        var lines = Leave(Equity,
            L(1, 10.5m, 300m, 0m, true, true),    // 3,150 of unused leave at go-live
            L(2, 1.75m, 250m, 0m, true, false),   // joined this month
            L(3, 2m, 200m, 0m, false, false));

        lines.Single(l => l.DebitAccount == Equity).Amount.Should().Be(3_150m);
        lines.Single(l => l.DebitAccount == LeaveExpense && l.Description == LeaveProvisionPlanner.CatchUpLabel).Amount.Should().Be(437.50m);
        lines.Single(l => l.DebitAccount == LeaveExpense && l.Description == LeaveProvisionPlanner.MonthlyLabel).Amount.Should().Be(400m);
        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
    }

    [Fact]
    public void Leave_WithTheRouteOff_IsUnchanged()
    {
        var off = Leave(null, L(1, 10.5m, 300m, 0m, true, true));
        off.Should().NotContain(l => l.DebitAccount == Equity);
        off.Single(l => l.DebitAccount == LeaveExpense).Amount.Should().Be(3_150m);
    }

    // ── policy key + chart ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("OpeningBalance", true)]
    [InlineData(" openingbalance ", true)]
    [InlineData("Expense", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Policy_OnlyTheExactValueSwitchesTheRouteOn(string? value, bool on) =>
        ProvisionCatchUpRoute.IsOpeningBalance(value).Should().Be(on);

    [Fact]
    public void Policy_IsRegisteredAsAnAllowListedAndApplied_CompanyRate()
    {
        StatutoryRateGuard.DefaultClientRateDefinitions(Guid.NewGuid()).Should().Contain(d => d.RateKey == ProvisionCatchUpRoute.Key && d.DataType == "string");
        StatutoryRateGuard.IsAppliedByPayroll(ProvisionCatchUpRoute.Key).Should().BeTrue("Lock reads it, so it must not be refused as record-only");
    }

    [Fact]
    public void TheEquityAccount_IsInTheCatalog_AsAnEquityBalancingDriver()
    {
        var d = PayrollGlCatalog.Drivers.Single(x => x.Key == ProvisionCatchUpRoute.EquityDriverKey);
        (d.DefaultCode, d.AccountType).Should().Be(("3900", "Equity"));
        var seed = PayrollGlCatalog.SystemDriverSeeds(Guid.NewGuid()).Single(x => x.Key == ProvisionCatchUpRoute.EquityDriverKey);
        (seed.Category, seed.PostingSide, seed.AccountType, seed.IsSystem).Should().Be((GlDriverCategories.Balancing, "DR", "Equity", true));
    }
}
