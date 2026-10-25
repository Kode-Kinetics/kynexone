using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-B slice 4 — the Required / NotAllowed / P&amp;L-side guard and the posting-integrity checks inside
/// <c>ZayraDbContext.SaveChanges*</c> (AM-11). T-034, S-016, S-017, S-018, S-019.
/// Every behavioural test runs through BOTH the async and the sync SaveChanges, on a context built with NO interceptors.
/// </summary>
public class FinanceGlEntryGuardTests
{
    public static IEnumerable<object[]> Paths => new[] { new object[] { false }, new object[] { true } };

    private static async Task Save(ZayraDbContext db, bool sync)
    {
        if (!sync) await db.SaveChangesAsync();
        else db.SaveChanges();
    }

    private sealed class Fx
    {
        public Guid Tid; public Company Co = null!, Other = null!;
        public GlAccount Req = null!, Nope = null!, Opt = null!, Liab = null!, Asset = null!, ReqIncome = null!;
        public CostCenter Cc = null!, CcInactive = null!, CcDeleted = null!, CcOtherCompany = null!, CcGroup = null!;
    }

    private static async Task<Fx> Seed(ZayraDbContext db, bool costingOn = true)
    {
        var f = new Fx { Tid = Guid.NewGuid() };
        f.Co = await AddCompany(db, f.Tid, costingOn);
        f.Other = await AddCompany(db, f.Tid, costingOn);
        f.Req = await AddAccount(db, f.Tid, f.Co.Id, "5001", "Expense", "Required");
        f.Nope = await AddAccount(db, f.Tid, f.Co.Id, "5002", "Expense", "NotAllowed");
        f.Opt = await AddAccount(db, f.Tid, f.Co.Id, "5003", "Expense", "Optional");
        f.ReqIncome = await AddAccount(db, f.Tid, f.Co.Id, "4001", "Income", "Required");
        f.Liab = await AddAccount(db, f.Tid, f.Co.Id, "2101", "Liability", "NotAllowed");
        f.Asset = await AddAccount(db, f.Tid, f.Co.Id, "1001", "Asset", "NotAllowed");
        f.Cc = await AddCostCentre(db, f.Tid, f.Co.Id, "SALES");
        f.CcInactive = await AddCostCentre(db, f.Tid, f.Co.Id, "OLD", active: false);
        f.CcDeleted = await AddCostCentre(db, f.Tid, f.Co.Id, "DEL", deleted: true);
        f.CcOtherCompany = await AddCostCentre(db, f.Tid, f.Other.Id, "OTH");
        f.CcGroup = await AddCostCentre(db, f.Tid, null, "GRP");
        return f;
    }

    private static FinanceGlEntry Line(Fx f, string? debit, string? credit, decimal amount = 100m, CostCenter? cc = null, Guid? company = null, string period = "2026-09")
        => new()
        {
            TenantId = f.Tid, CompanyId = company ?? f.Co.Id, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), SourceEntityRef = "RUN-1",
            EventType = "PayrollAccrual", DebitAccount = debit ?? "", CreditAccount = credit ?? "", Amount = amount, Currency = "SAR",
            EntryDate = new DateOnly(2026, 9, 30), Period = period, Description = "test",
            CostCenterId = cc?.Id, CostCenterCode = cc?.Code, CostCenterName = cc?.Name, CostCenterGlSegment = cc?.GlSegment,
        };

    private static async Task ExpectRejected(ZayraDbContext db, bool sync, string code)
    {
        var act = () => Save(db, sync);
        var ex = await act.Should().ThrowAsync<GlCostCentreRuleException>();
        ex.Which.Code.Should().Be(code);
        ex.Which.Should().BeAssignableTo<InvalidOperationException>();
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.CountAsync()).Should().Be(0, "a rejected batch writes nothing");
    }

    // ── Required / NotAllowed / Optional ────────────────────────────────────

    [Theory, MemberData(nameof(Paths))]
    public async Task Required_ExpenseDebit_WithoutCostCentre_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Req), Label(f.Liab)));
        await ExpectRejected(db, sync, "cost_centre_required");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Required_IncomeCredit_WithoutCostCentre_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Asset), Label(f.ReqIncome)));
        await ExpectRejected(db, sync, "cost_centre_required");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Required_WithAValidCostCentre_Saves_AndTheLiabilitySideStaysUncosted(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Req), Label(f.Liab), 250m, f.Cc));
        await Save(db, sync);
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.SingleAsync()).CostCenterId.Should().Be(f.Cc.Id);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task OneSidedPayrollRow_OnRequiredAccount_NeedsTheCostCentre(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Req), null));
        await ExpectRejected(db, sync, "cost_centre_required");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task NotAllowed_PnlAccount_WithACostCentre_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Nope), Label(f.Liab), 100m, f.Cc));
        await ExpectRejected(db, sync, "cost_centre_not_allowed");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task CostCentre_OnARowWithNoProfitAndLossSide_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Asset), Label(f.Liab), 100m, f.Cc));
        await ExpectRejected(db, sync, "cost_centre_not_allowed");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task BothSidesProfitAndLoss_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Opt), Label(f.ReqIncome), 100m, f.Cc));
        await ExpectRejected(db, sync, "cost_centre_both_sides_pnl");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Optional_SavesWithAndWithoutACostCentre(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Opt), Label(f.Liab)));
        db.FinanceGlEntries.Add(Line(f, Label(f.Opt), Label(f.Liab), 5m, f.Cc));
        await Save(db, sync);
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.CountAsync()).Should().Be(2);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task CostingOff_NoRuleIsEnforced_ButTheRowStillNeedsACompany(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db, costingOn: false);
        db.FinanceGlEntries.Add(Line(f, Label(f.Req), Label(f.Liab)));
        await Save(db, sync);

        var bad = Line(f, Label(f.Req), Label(f.Liab));
        bad.CompanyId = null;
        db.FinanceGlEntries.Add(bad);
        var act = () => Save(db, sync);
        (await act.Should().ThrowAsync<GlCostCentreRuleException>()).Which.Code.Should().Be("finance_gl_entry_company_required");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task AccountOutsideTheChart_IsNotRuleChecked(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, "9990 - Not in chart", "9991 - Neither"));
        await Save(db, sync);
    }

    // ── the cost centre itself ──────────────────────────────────────────────

    [Theory, MemberData(nameof(Paths))]
    public async Task CostCentre_OfAnotherCompany_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Req), Label(f.Liab), 100m, f.CcOtherCompany));
        await ExpectRejected(db, sync, "cost_centre_invalid");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task CostCentre_WithNoCompany_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Req), Label(f.Liab), 100m, f.CcGroup));
        await ExpectRejected(db, sync, "cost_centre_invalid");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task CostCentre_InactiveDeletedOrUnknown_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Req), Label(f.Liab), 100m, f.CcInactive));
        await ExpectRejected(db, sync, "cost_centre_invalid");

        db.FinanceGlEntries.Add(Line(f, Label(f.Req), Label(f.Liab), 100m, f.CcDeleted));
        await ExpectRejected(db, sync, "cost_centre_invalid");

        var ghost = Line(f, Label(f.Req), Label(f.Liab));
        ghost.CostCenterId = Guid.NewGuid(); ghost.CostCenterCode = "GHOST";
        db.FinanceGlEntries.Add(ghost);
        await ExpectRejected(db, sync, "cost_centre_invalid");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task CostCentre_OfAnotherTenant_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var foreignCo = await AddCompany(db, Guid.NewGuid());
        var foreignCc = await AddCostCentre(db, foreignCo.TenantId, foreignCo.Id, "FOREIGN");
        var row = Line(f, Label(f.Req), Label(f.Liab), 100m, foreignCc);
        db.FinanceGlEntries.Add(row);
        await ExpectRejected(db, sync, "cost_centre_invalid");
    }

    // ── S-016: new rows need a company ──────────────────────────────────────

    [Theory, MemberData(nameof(Paths))]
    public async Task NewRowWithNoCompany_IsRejected_EvenOnARequiredAccount(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        db.FinanceGlEntries.Add(Line(f, Label(f.Req), Label(f.Liab), 100m, null, company: Guid.Empty));
        db.ChangeTracker.Entries<FinanceGlEntry>().Single().Entity.CompanyId = null;
        await ExpectRejected(db, sync, "finance_gl_entry_company_required");
    }

    // ── S-017 / S-018: reversals ────────────────────────────────────────────

    private static async Task<FinanceGlEntry> Original(ZayraDbContext db, Fx f, CostCenter? cc = null, decimal amount = 100m)
    {
        var o = Line(f, Label(f.Req), Label(f.Liab), amount, cc ?? f.Cc);
        db.FinanceGlEntries.Add(o);
        await db.SaveChangesAsync();
        return o;
    }

    private static FinanceGlEntry Contra(FinanceGlEntry o, decimal? amount = null) => new()
    {
        TenantId = o.TenantId, CompanyId = o.CompanyId, SourceModule = o.SourceModule, SourceEntityId = o.SourceEntityId, SourceEntityRef = o.SourceEntityRef,
        EventType = "PayrollAccrualReversal", DebitAccount = o.CreditAccount, CreditAccount = o.DebitAccount, Amount = amount ?? o.Amount, Currency = o.Currency,
        EntryDate = o.EntryDate, Period = o.Period, Description = "REVERSAL", ReversalOfEntryId = o.Id,
        CostCenterId = o.CostCenterId, CostCenterCode = o.CostCenterCode, CostCenterName = o.CostCenterName, CostCenterGlSegment = o.CostCenterGlSegment,
    };

    private static async Task ExpectReversalRejected(ZayraDbContext db, bool sync, int expectedRows)
    {
        var act = () => Save(db, sync);
        (await act.Should().ThrowAsync<GlCostCentreRuleException>()).Which.Code.Should().Be("reversal_invalid");
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.CountAsync()).Should().Be(expectedRows);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_OfAnExistingMirror_Saves_AndMayFlagTheOriginalReversedInTheSameUnit(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        o.IsReversed = true;
        db.FinanceGlEntries.Add(Contra(o));
        await Save(db, sync);
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.CountAsync()).Should().Be(2);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_OfANonexistentEntry_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var ghost = Line(f, Label(f.Req), Label(f.Liab), 100m, f.Cc);
        var c = Contra(ghost); // ghost.Id is not stored anywhere
        db.FinanceGlEntries.Add(c);
        await ExpectReversalRejected(db, sync, 0);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_OfAnotherCompanysEntry_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        var c = Contra(o); c.CompanyId = f.Other.Id;
        db.FinanceGlEntries.Add(c);
        await ExpectReversalRejected(db, sync, 1);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_OfAnotherTenantsEntry_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var foreign = await Seed(db);
        var o = await Original(db, foreign);
        var c = Contra(o); c.TenantId = f.Tid; c.CompanyId = f.Co.Id;
        db.FinanceGlEntries.Add(c);
        await ExpectReversalRejected(db, sync, 1);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_OfAReversal_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        var first = Contra(o);
        db.FinanceGlEntries.Add(first);
        await db.SaveChangesAsync();
        db.FinanceGlEntries.Add(Contra(first));
        await ExpectReversalRejected(db, sync, 2);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_WithALargerAmount_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        db.FinanceGlEntries.Add(Contra(o, 100.01m));
        await ExpectReversalRejected(db, sync, 1);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_ThatIsNotSideSwapped_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        var c = Contra(o); c.DebitAccount = o.DebitAccount; c.CreditAccount = o.CreditAccount;   // same direction = a second posting, not a reversal
        db.FinanceGlEntries.Add(c);
        await ExpectReversalRejected(db, sync, 1);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_ToADifferentCurrency_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        var c = Contra(o); c.Currency = "USD";
        db.FinanceGlEntries.Add(c);
        await ExpectReversalRejected(db, sync, 1);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_WithADifferentCostCentre_IsRejected_S018(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        var c = Contra(o);
        c.CostCenterId = f.CcInactive.Id; c.CostCenterCode = f.CcInactive.Code; c.CostCenterName = f.CcInactive.Name;
        db.FinanceGlEntries.Add(c);
        await ExpectReversalRejected(db, sync, 1);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_DroppingTheCostCentre_IsRejected_S018(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        var c = Contra(o); c.CostCenterId = null; c.CostCenterCode = null; c.CostCenterName = null;
        db.FinanceGlEntries.Add(c);
        await ExpectReversalRejected(db, sync, 1);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_OfAnEntryAlreadyReversed_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        db.FinanceGlEntries.Add(Contra(o));
        o.IsReversed = true;
        await db.SaveChangesAsync();
        db.FinanceGlEntries.Add(Contra(o));
        await ExpectReversalRejected(db, sync, 2);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_PartialContras_AreAllowed_UpToTheOriginalAmount(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f, amount: 100m);
        db.FinanceGlEntries.Add(Contra(o, 60m));
        await db.SaveChangesAsync();
        db.FinanceGlEntries.Add(Contra(o, 40m));
        await Save(db, sync);                         // 60 + 40 == 100: fine

        db.FinanceGlEntries.Add(Contra(o, 0.01m));    // one halala more than the original
        await ExpectReversalRejected(db, sync, 3);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_IsExemptFromRulesAndInactivity_SoARuleChangeNeverBlocksAVoid(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        // After posting: the account becomes NotAllowed, and the cost centre is switched off.
        f.Req.CostCenterRule = "NotAllowed"; f.Cc.IsActive = false;
        await db.SaveChangesAsync();

        db.FinanceGlEntries.Add(Contra(o));
        await Save(db, sync);
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.CountAsync()).Should().Be(2);
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task Reversal_WithNoCompany_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        var c = Contra(o); c.CompanyId = null;
        db.FinanceGlEntries.Add(c);
        await ExpectRejected2(db, sync, "finance_gl_entry_company_required", 1);
    }

    private static async Task ExpectRejected2(ZayraDbContext db, bool sync, string code, int rows)
    {
        var act = () => Save(db, sync);
        (await act.Should().ThrowAsync<GlCostCentreRuleException>()).Which.Code.Should().Be(code);
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.CountAsync()).Should().Be(rows);
    }

    // ── posted lines never change their cost centre ─────────────────────────

    [Theory, MemberData(nameof(Paths))]
    public async Task ModifyingThePostedCostCentreOrKey_IsRejected(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        o.CostCenterName = "Renamed";
        var act = () => Save(db, sync);
        (await act.Should().ThrowAsync<GlCostCentreRuleException>()).Which.Code.Should().Be("finance_gl_entry_cost_centre_immutable");
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.SingleAsync()).CostCenterName.Should().Be("CC SALES");
    }

    [Theory, MemberData(nameof(Paths))]
    public async Task OrdinaryLifecycleUpdates_Erp_Status_AreStillAllowed(bool sync)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var o = await Original(db, f);
        o.ErpPostingStatus = ErpPostingStatuses.ReadyForErp;
        await Save(db, sync);
    }

    // ── S-019: the guard lives in the context ───────────────────────────────

    [Fact]
    public void S019_Ratchet_TheGuardIsCalledFromBothSaveOverloads_AndNotRegisteredInProgramCs()
    {
        var api = Path.Combine(RepoRoot(), "backend-dotnet", "Zayra.Api");
        var ctx = File.ReadAllText(Path.Combine(api, "Data", "ZayraDbContext.cs"));
        var asyncBody = Between(ctx, "public override async Task<int> SaveChangesAsync(CancellationToken", "public override int SaveChanges(bool");
        var syncBody = ctx[ctx.IndexOf("public override int SaveChanges(bool", StringComparison.Ordinal)..];
        syncBody = syncBody[..Math.Min(syncBody.Length, 3500)];

        asyncBody.Should().Contain("EnforceFinanceGlEntryIntegrity", "the async overload must call the guard next to EnforceAuditLogAppendOnly");
        syncBody.Should().Contain("EnforceFinanceGlEntryIntegrity", "the sync overload must call it too");
        ctx.IndexOf("EnforceFinanceGlEntryIntegrity", StringComparison.Ordinal).Should().BeLessThan(ctx.IndexOf("EnforceAuditLogAppendOnly()", StringComparison.Ordinal) + 400);

        var program = File.ReadAllText(Path.Combine(api, "Program.cs"));
        program.Should().NotContain("GlEntryCostCentreInterceptor", "a Program.cs-registered interceptor is absent in tests and in services that build their own options");
        File.Exists(Path.Combine(api, "Infrastructure", "Finance", "GlEntryCostCentreInterceptor.cs")).Should().BeFalse();
    }

    [Fact]
    public void S019_Ratchet_EveryContextConstructionIsTheGuardedClass()
    {
        // `new ZayraDbContext(` can only build the guarded class, and nothing derives from it with its own SaveChanges override.
        var api = Path.Combine(RepoRoot(), "backend-dotnet", "Zayra.Api");
        foreach (var file in Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            System.Text.RegularExpressions.Regex.IsMatch(text, @"class\s+\w+\s*:\s*ZayraDbContext\b").Should().BeFalse($"{file} must not derive from ZayraDbContext and bypass the guard");
        }
    }

    private static string Between(string s, string a, string b)
    {
        var i = s.IndexOf(a, StringComparison.Ordinal); i.Should().BeGreaterThan(-1);
        var j = s.IndexOf(b, i, StringComparison.Ordinal); j.Should().BeGreaterThan(i);
        return s[i..j];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
