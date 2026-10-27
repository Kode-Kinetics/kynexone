using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>The independent oracle (AM-6): hand-computed inputs and expected journals, copied verbatim from /tmp/kynexone-cc/oracle.</summary>
internal static class OracleFiles
{
    public static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CostAccountingOracle");

    public static List<Dictionary<string, string>> Csv(string relative)
    {
        var lines = File.ReadAllLines(Path.Combine(Root, relative));
        var header = Fields(lines[0]);
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = Fields(line);
            var row = new Dictionary<string, string>();
            for (var i = 0; i < header.Count; i++) row[header[i]] = i < f.Count ? f[i] : string.Empty;
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> Fields(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    public static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
}

/// <summary>One ledger line reduced to what the oracle states: journal, account, side, cost centre, amount.</summary>
internal sealed record OracleLine(string Journal, string Account, string Side, string CostCentre, decimal Amount)
{
    public string Key => $"{Journal}|{Account}|{Side}|{CostCentre}";
}

/// <summary>
/// The oracle company ACME-SA built into the app through the real services where practical: org and cost centres from the inputs,
/// the bonus accrual through the real BonusesController, the leaver's settlement accrual through the real ApproveFinalSettlement, the
/// advance through the real AdvancesController posting helper, every payroll run through the real Approve and Lock, the class re-route
/// through the real FinanceGlController mapping save, and the void through the real PayrollVoidService. The payroll REGISTERS
/// (earnings and deductions) are seeded from the oracle's register files; the GOSI register is reconciled separately.
/// </summary>
internal sealed class OracleWorld : IAsyncDisposable
{
    public CostingLockKit K { get; private set; } = null!;
    public Guid TenantId => K.TenantId;
    public PayrollRun Reg1 { get; private set; } = null!;
    public PayrollRun Oc1 { get; private set; } = null!;
    public PayrollRun Reg2 { get; private set; } = null!;
    public PayrollRun? Repl { get; private set; }
    public EmployeeFinalSettlement Settlement { get; private set; } = null!;
    public BonusBatch Batch { get; private set; } = null!;
    public Guid AdvanceId { get; } = Guid.NewGuid();
    public Guid MappingUser { get; } = Guid.NewGuid();
    private PostgresFixture _fx = null!;

    public static async Task<OracleWorld> CreateAsync(PostgresFixture fx, bool preset = true, bool defaultCc = true)
    {
        var w = new OracleWorld { _fx = fx };
        Guid tenantId;
        await using (var db = fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        w.K = await CostingLockKit.CreateAsync(costing: true, preset: preset, fx.CreateDb, tenantId);
        await w.SeedOrgAsync(defaultCc);
        return w;
    }

    private async Task SeedOrgAsync(bool defaultCc)
    {
        foreach (var r in OracleFiles.Csv("inputs/02_cost_centres.csv"))
            await K.AddCc(r["code"], r["functional_class"], r["parent_code"] == "" ? null : r["parent_code"], r["gl_segment"]);
        foreach (var r in OracleFiles.Csv("inputs/03_departments.csv"))
            await K.AddDept(r["code"], r["cost_centre_code"] == "" ? null : r["cost_centre_code"], r["parent_department_code"] == "" ? null : r["parent_department_code"]);
        foreach (var r in OracleFiles.Csv("inputs/04_positions.csv"))
            await K.AddPosition(r["code"], r["cost_centre_code"] == "" ? null : r["cost_centre_code"]);
        foreach (var r in OracleFiles.Csv("inputs/05_employees.csv"))
        {
            var e = await K.AddEmployee(r["employee_code"], r["department"], r["position"], r["override_cost_centre"] == "" ? null : r["override_cost_centre"]);
            e.Nationality = r["nationality"];
        }
        K.Touch();
        await K.Db.SaveChangesAsync();
        if (defaultCc) await K.SetDefaultCc("CC-100");
        // the oracle's two extra accounts (M2 only) exist from the start; nothing routes to them until the class mapping is saved
        foreach (var (code, name) in new[] { ("5011", "Direct Labour - Basic (COGS)"), ("5012", "Direct Labour - Housing (COGS)") })
            K.Db.GlAccounts.Add(new GlAccount { TenantId = TenantId, CompanyId = K.CompanyId, Code = code, Name = name, AccountType = "Expense", CostCenterRule = "Required" });
        await K.Db.SaveChangesAsync();
    }

    // ── registers ────────────────────────────────────────────────────────────

    public void Reg1Seed(PayrollRun r) => Reg1 = r;
    public void Oc1Seed(PayrollRun r) => Oc1 = r;
    public void Reg2Seed(PayrollRun r) => Reg2 = r;
    public void ReplSeed(PayrollRun r) => Repl = r;

    /// <summary>The re-post of the voided settlement run: the same gratuity and leave, without the 300 recovery (README: net 2100 = 33,156.16).</summary>
    public async Task<PayrollRun> SeedReplacementAsync()
    {
        var run = await K.SeedRunAsync(
            new[] { E("E009", "EOSB_GRATUITY", 31156.16m, "Settlement"), E("E009", "LEAVE_ENCASHMENT", 2000m, "Settlement") },
            Array.Empty<(string, string, string, decimal, bool)>(), month: 9, runType: "Replacement");
        var tracked = await K.Db.PayrollRuns.SingleAsync(r => r.Id == run.Id);
        tracked.GlPostingPeriod = "2026-10"; tracked.ParentRunId = Oc1.Id;
        await K.Db.SaveChangesAsync();
        await K.Db.EmployeeFinalSettlements.Where(x => x.Id == Settlement.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.PayrollRunId, (Guid?)run.Id));
        K.Db.ChangeTracker.Clear();
        return run;
    }

    public async Task<IActionResult> TryApproveBonusBatchAsync(string employeeCode, decimal gross)
    {
        var db = K.Db;
        var type = new BonusType { TenantId = TenantId, Code = "T2", NameEn = "Trial", TaxRegion = "GCC", IsActive = true };
        db.BonusTypes.Add(type);
        var batch = new BonusBatch
        {
            TenantId = TenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-9", BatchName = "Trial", PaymentPeriod = "2026-09",
            PaymentDate = new DateOnly(2026, 9, 25), Status = "PendingApproval", EmployeeCount = 1, TotalAmount = gross,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = TenantId, CompanyId = K.CompanyId, BonusBatchId = batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = K.Emp[employeeCode].Id,
            EmployeeName = employeeCode, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m, CalculationMethod = "Fixed",
            CalculationValue = gross, GrossBonusAmount = gross, TaxWithheld = 0m, BonusAmount = gross, PaymentPeriod = "2026-09", Status = "Draft", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var ctrl = new BonusesController(db, new AllowAll()) { ControllerContext = ControllerFor("payroll.write", "payroll.approve") };
        return await ctrl.ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default);
    }

    public async Task<PayrollRun> SeedRunAsync(string registerCsv, string runType, int month, string? glPeriod = null, Guid? parent = null, string[]? exclude = null)
    {
        var rows = OracleFiles.Csv(registerCsv).Where(r => exclude is null || !exclude.Contains(r["employee_code"])).ToList();
        var earnings = rows.Where(r => r["kind"] == "Earning").Select(r => (Emp: r["employee_code"], Code: ComponentOf(r), Source: SourceOf(r), Amount: OracleFiles.D(r["amount"]))).ToList();
        var deductions = rows.Where(r => r["kind"] == "Deduction").Select(r => (Emp: r["employee_code"], Code: r["component_code"], Source: SourceOf(r), Amount: OracleFiles.D(r["amount"]), Employer: r["is_employer_contribution"] == "true")).ToList();
        var run = await K.SeedRunAsync(earnings, deductions, month: month, runType: runType);
        if (glPeriod is not null || parent is not null)
        {
            var tracked = await K.Db.PayrollRuns.SingleAsync(r => r.Id == run.Id);
            tracked.GlPostingPeriod = glPeriod; tracked.ParentRunId = parent;
            await K.Db.SaveChangesAsync();
        }
        return run;
    }

    private static string ComponentOf(Dictionary<string, string> r) => r["component_code"] == "BONUS" ? "BONUS_PERFORMANCE" : r["component_code"];
    private static string SourceOf(Dictionary<string, string> r) => r["source"] switch
    {
        "Earning" => "Salary", "Bonus" => "Bonus", "Statutory" => "Statutory", "Loan" => "Loan", "Settlement" => "Settlement", var s => s,
    };

    // ── the other modules ────────────────────────────────────────────────────

    public async Task ApproveBonusBatchAsync()
    {
        var db = K.Db;
        var type = new BonusType { TenantId = TenantId, Code = "PERF", NameEn = "Performance", TaxRegion = "GCC", IsActive = true };
        db.BonusTypes.Add(type);
        // The oracle accrued 2,899.99 for three bonuses of 1,000 (A-BON-1). Gross amounts of 966.67 / 966.66 / 966.66 make the real
        // ApproveBatch book exactly that, split per cost centre; the payroll register still pays 1,000 each.
        var gross = new Dictionary<string, decimal> { ["E001"] = 966.67m, ["E002"] = 966.66m, ["E005"] = 966.66m };
        Batch = new BonusBatch
        {
            TenantId = TenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-1", BatchName = "Batch B1",
            PaymentPeriod = "2026-09", PaymentDate = new DateOnly(2026, 9, 25), Status = "PendingApproval", EmployeeCount = 3, TotalAmount = gross.Values.Sum(),
        };
        db.BonusBatches.Add(Batch);
        await db.SaveChangesAsync();
        foreach (var (emp, g) in gross)
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = TenantId, CompanyId = K.CompanyId, BonusBatchId = Batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = K.Emp[emp].Id,
                EmployeeName = emp, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m, CalculationMethod = "Fixed",
                CalculationValue = g, GrossBonusAmount = g, TaxWithheld = 0m, BonusAmount = g, PaymentPeriod = "2026-09", Status = "Draft", TaxRegion = "GCC",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var ctrl = new BonusesController(db, new AllowAll()) { ControllerContext = ControllerFor("payroll.write", "payroll.approve") };
        (await ctrl.ApproveBatch(Batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    public async Task ConsumeBonusesAsync(Guid runId)
    {
        await K.Db.EmployeeBonuses.Where(b => b.BonusBatchId == Batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "PaidInPayroll").SetProperty(b => b.PayrollRunId, (Guid?)runId));
    }

    public async Task ApproveSettlementAsync()
    {
        var db = K.Db;
        var employee = K.Emp["E009"];
        var lastDay = new DateOnly(2026, 9, 20);
        var offboarding = new EmployeeOffboarding
        {
            TenantId = TenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName, SeparationType = "Termination",
            Status = "InProgress", NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        Settlement = new EmployeeFinalSettlement
        {
            TenantId = TenantId, CompanyId = K.CompanyId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
            OffboardingId = offboarding.Id, LastWorkingDay = lastDay, ServiceStartDate = new DateOnly(2020, 5, 1), SettlementDueDate = lastDay.AddDays(7),
            TerminationReason = "Termination", ServiceYears = 6.394521m, Currency = "SAR",
            GratuityAmount = 31156.16m, LeaveEncashmentAmount = 2000m, GrossPayable = 33156.16m, TotalDeductions = 300m, NetPayable = 32856.16m,
            WagesPaidByRunId = Reg1.Id, WageBaseDeltaAmount = 0m, Status = FinalSettlementStatuses.PendingApproval,
            CreatedByUserId = Guid.NewGuid(), CreatedByName = "Maker", SubmittedByUserId = Guid.NewGuid(), SubmittedByName = "Submitter", SubmittedAtUtc = DateTime.UtcNow,
        };
        db.EmployeeOffboardings.Add(offboarding);
        db.EmployeeFinalSettlements.Add(Settlement);
        db.FinalSettlementLines.AddRange(
            new FinalSettlementLine { TenantId = TenantId, SettlementId = Settlement.Id, ComponentCode = FinalSettlementComponents.Gratuity, ComponentName = "Gratuity", LineType = FinalSettlementLineTypes.Earning, Source = FinalSettlementComponents.SettlementSource, Amount = 31156.16m, SortOrder = 0 },
            new FinalSettlementLine { TenantId = TenantId, SettlementId = Settlement.Id, ComponentCode = FinalSettlementComponents.LeaveEncashment, ComponentName = "Leave", LineType = FinalSettlementLineTypes.Earning, Source = FinalSettlementComponents.SettlementSource, Amount = 2000m, SortOrder = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var body = JsonSerializer.Deserialize<ApproveFinalSettlementRequest>(
            """{"confirmTerminationReason":"Termination","accrualDate":"2026-09-21"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var result = await FinalSettlementMakerCheckerTests.Controller(db, TenantId, Guid.NewGuid()).ApproveFinalSettlement(Settlement.Id, body, default);
        result.Should().BeOfType<OkObjectResult>(Json(result).ToString());
        db.ChangeTracker.Clear();
    }

    public async Task IssueAdvanceAsync()
    {
        var ctrl = new AdvancesController(K.Db, new AllowAll()) { ControllerContext = ControllerFor("payroll.write", "payroll.approve") };
        var post = typeof(AdvancesController).GetMethod("PostGlEntry", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)post.Invoke(ctrl, new object?[]
        {
            TenantId, Guid.NewGuid(), K.CompanyId, AdvanceId, "ADV-1", "Advance", "Disbursement", "ADVANCE_RECEIVABLE", "CASH_BANK", 1200m, null, CancellationToken.None,
        })!;
        await K.Db.SaveChangesAsync();
    }

    public async Task ClosePeriodAsync(string period)
    {
        K.Db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = TenantId, CompanyId = K.CompanyId, Period = period, Status = GlPeriodStatuses.Closed });
        await K.Db.SaveChangesAsync();
    }

    // ── comparison ───────────────────────────────────────────────────────────

    public async Task<List<FinanceGlEntry>> AllLedgerAsync()
    {
        await using var read = K.K.NewContext();
        return await read.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(l => l.TenantId == TenantId).ToListAsync();
    }

    public static List<OracleLine> Expand(IEnumerable<FinanceGlEntry> entries, Func<FinanceGlEntry, string?> journalOf)
    {
        var lines = new List<OracleLine>();
        foreach (var e in entries)
        {
            var journal = journalOf(e);
            if (journal is null) continue;
            // A row's cost centre belongs to its Expense / Revenue side (contract finding 3); the other side of a two-sided row is uncosted.
            static string Cc(FinanceGlEntry e, string account) => account.StartsWith('5') || account.StartsWith('6') ? e.CostCenterCode ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(e.DebitAccount)) { var a = e.DebitAccount.Split(' ')[0]; lines.Add(new(journal, a, "DR", Cc(e, a), e.Amount)); }
            if (!string.IsNullOrWhiteSpace(e.CreditAccount)) { var a = e.CreditAccount.Split(' ')[0]; lines.Add(new(journal, a, "CR", Cc(e, a), e.Amount)); }
        }
        return lines;
    }

    /// <summary>The oracle's expected lines, aggregated by (journal, account, side, cost centre) exactly as the produced lines are.</summary>
    public static List<OracleLine> Expected(string csv, Func<string, bool>? journalFilter = null) =>
        OracleFiles.Csv(csv).Where(r => journalFilter?.Invoke(r["journal_id"]) ?? true)
            .Select(r => new OracleLine(r["journal_id"], r["account_code"], r["side"], r["cost_centre_code"], OracleFiles.D(r["amount"]))).ToList();

    public static Dictionary<string, decimal> Aggregate(IEnumerable<OracleLine> lines) =>
        lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));

    public System.Security.Claims.ClaimsPrincipal PrincipalFor(params string[] permissions) => new(new System.Security.Claims.ClaimsIdentity(
        new[]
        {
            new System.Security.Claims.Claim("tenant_id", TenantId.ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "oracle"),
        }.Concat(permissions.Select(p => new System.Security.Claims.Claim("permission", p))), "test"));

    private ControllerContext ControllerFor(params string[] permissions) =>
        new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = PrincipalFor(permissions) } };

    private sealed class AllowAll : Zayra.Api.Application.Common.IDataScopeService
    {
        public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization });
    }

    public ValueTask DisposeAsync() => K.DisposeAsync();
}
