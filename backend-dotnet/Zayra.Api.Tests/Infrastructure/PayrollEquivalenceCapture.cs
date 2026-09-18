using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Infrastructure;

/// <summary>
/// F2 — SUITE-WIDE payroll equivalence capture. Test-only, OFF unless the environment variable
/// <c>ZAYRA_PAYROLL_EQUIV_DUMP</c> names an output file.
///
/// <para>The golden master pins six hand-picked scenarios. A change to the money path must also leave
/// every OTHER payroll run the suite drives untouched — proration, arrears, sibling/off-cycle runs,
/// settlements, voids, recovery, GOSI tie-out. Rather than re-seed each of those, this hooks EF Core's
/// global <see cref="DiagnosticListener"/> (so it sees every <c>ZayraDbContext</c> in the process —
/// Postgres, SQLite and InMemory alike) and, at every <c>SaveChangesStarting</c>, records a canonical,
/// id-free line for each ADDED payslip, earning line, deduction line, run-employee row, validation result
/// and GL entry. The sorted multiset is written on process exit.</para>
///
/// <para>Usage: run the suite on the baseline commit and on the candidate with the variable set, then
/// diff the two files. Deterministic by construction (no ids, timestamps or descriptions), which is
/// verified empirically by diffing two baseline runs against each other.</para>
/// </summary>
internal static class PayrollEquivalenceCapture
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();
    private static string? _path;

    [ModuleInitializer]
    internal static void Init()
    {
        _path = Environment.GetEnvironmentVariable("ZAYRA_PAYROLL_EQUIV_DUMP");
        if (string.IsNullOrWhiteSpace(_path)) return;
        DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    private static void Flush()
    {
        List<string> snapshot;
        lock (Gate) snapshot = Lines.OrderBy(l => l, StringComparer.Ordinal).ToList();
        File.WriteAllLines(_path!, snapshot);
    }

    private static string D(decimal v) => v.ToString("0.00####", CultureInfo.InvariantCulture);

    private static void Capture(DbContext ctx)
    {
        var added = ctx.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        var batch = new List<string>();
        foreach (var o in added)
        {
            switch (o)
            {
                case PayrollSlip s:
                    batch.Add($"S|{s.EmployeeCode}|{D(s.BasicSalary)}|{D(s.HousingAllowance)}|{D(s.TransportAllowance)}|{D(s.OtherAllowances)}|" +
                              $"{D(s.GrossSalary)}|{D(s.Deductions)}|{D(s.NetSalary)}|{D(s.EmployeeStatutoryTotal)}|{D(s.EmployerStatutoryTotal)}|" +
                              $"{D(s.LoanDeductions)}|{D(s.YtdGross)}|{D(s.YtdDeductions)}|{D(s.YtdNet)}|{s.PaidDays}|{s.ProrationFactor}|{D(s.ArrearsAmount)}");
                    break;
                case PayrollEarning e:
                    batch.Add($"E|{e.ComponentCode}|{e.ComponentName}|{D(e.Amount)}|{e.Source}");
                    break;
                case PayrollDeduction d:
                    batch.Add($"D|{d.ComponentCode}|{d.ComponentName}|{D(d.Amount)}|{d.Source}|{d.IsEmployerContribution}");
                    break;
                case PayrollRunEmployee r:
                    batch.Add($"R|{D(r.GrossEarnings)}|{D(r.TotalDeductions)}|{D(r.NetPay)}");
                    break;
                case PayrollValidationResult v:
                    batch.Add($"V|{v.Severity}|{v.Code}");
                    break;
                case FinanceGlEntry g when g.SourceModule == "Payroll":
                    batch.Add($"G|{g.EventType}|{g.DebitAccount}|{g.CreditAccount}|{D(g.Amount)}|{g.Currency}");
                    break;
            }
        }
        if (batch.Count == 0) return;
        lock (Gate) Lines.AddRange(batch);
    }

    private sealed class ListenerObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == DbLoggerCategory.Name)
                listener.Subscribe(new EventObserver());
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> kv)
        {
            if (kv.Key == CoreEventId.SaveChangesStarting.Name && kv.Value is DbContextEventData data && data.Context is not null)
            {
                try { Capture(data.Context); } catch { /* capture must never affect the suite */ }
            }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
