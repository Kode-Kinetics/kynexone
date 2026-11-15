using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// One-off backfill of the opening journals of loans and salary advances carried in by the opening-balance import before the
/// import posted them. Such a debt sits in the sub-ledger with nothing on 1400 / 1410, so the receivable never ties out and a
/// payroll recovery has no debit to relieve. Nothing runs this on deploy: Finance (or an Admin) previews a company, reviews the
/// rows, and applies.
///
/// <para><b>What is posted.</b> DR 1400 Employee Loans Receivable or 1410 Employee Salary Advances / CR 3900 Opening Balance Equity
/// for the balance the import carried in at cutover (<see cref="OpeningBalanceOrigin.CarriedAmount"/>), dated on that cutover date,
/// through the import's own writer (<see cref="OpeningBalanceJournal"/>). That is exactly the line the import would have posted
/// on the day, so a backfilled debt and a debt imported today carry the same opening journal, a later restatement compares with
/// the same figure, and applying twice posts nothing (the writer posts only the difference). The current balance is shown beside
/// it: where payroll has recovered from a debt since cutover, the recovery was booked before the receivable carried the debt
/// (to the 2107 clearing account for a loan, against the shared 1410 balance for an advance), which the preview reports as
/// recoveredSinceCutover for Finance to reconcile; the backfill does not guess at it.</para>
///
/// <para>A debt whose cutover period is closed is refused for its row, in English and Arabic; reopen the period to backfill it.
/// Finance roles or Admin only, company-scoped. No second person is required: the action restates no balance and moves no money,
/// it books a figure the import already recorded (the origin row), it is idempotent and every posting is audited. Each debt is
/// posted under its finance.loan / finance.advance lock inside one retry-safe transaction, so it cannot race the import or payroll.</para>
/// </summary>
[ApiController]
[Route("api/finance/opening-balance-journals")]
[Authorize]
public class OpeningBalanceJournalsController(ZayraDbContext db) : ControllerBase
{
    private readonly ZayraDbContext _db = db;
    private static readonly string[] Roles = { "Admin", "Finance", "Finance Controller", "Finance Approver" };

    /// <summary>Dry run: every carried-in loan and advance of the company and what applying would post, changing nothing.</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] Guid companyId, CancellationToken ct)
    {
        if (Refusal(companyId) is { } refusal) return refusal;
        var rows = await PlanAsync(this.GetTenantId()!.Value, companyId, ct);
        return Ok(new
        {
            companyId,
            rows = rows.Select(Project).ToList(),
            ready = rows.Count(r => r.Status == Ready),
        });
    }

    /// <summary>Posts every ready row's opening journal (idempotent) and refuses the others per row. Audited per posting.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] OpeningBalanceBackfillRequest req, CancellationToken ct)
    {
        if (Refusal(req.CompanyId) is { } refusal) return refusal;
        var tenantId = this.GetTenantId()!.Value;
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
            var origins = await OriginsAsync(tenantId, req.CompanyId, ct);
            await FinanceDecisionSerializer.AcquireDebtLocksAsync(_db, tenantId,
                origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Loan).Select(o => o.EntityId),
                origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Advance).Select(o => o.EntityId), ct);
            var rows = await PlanAsync(tenantId, req.CompanyId, ct);
            var posted = 0;
            foreach (var row in rows.Where(r => r.Status == Ready))
            {
                OpeningBalanceJournal.Apply(_db, row.Prepared!, row.EntityId, row.Currency, this.GetUserId(), User.Identity?.Name ?? string.Empty);
                _db.AuditLogs.Add(new AuditLog
                {
                    TenantId = tenantId, CompanyId = req.CompanyId, Action = "finance.gl.opening_balance_backfill.applied",
                    EntityName = row.EntityType == OpeningBalanceEntityTypes.Loan ? "EmployeeLoan" : "SalaryAdvance", EntityId = row.EntityId.ToString(),
                    UserId = this.GetUserId(), IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
                    Metadata = JsonSerializer.Serialize(new { row.Reference, row.CutoverDate, row.CarriedAmount, posted = row.ToPost, row.CurrentOutstanding }),
                    CreatedAtUtc = DateTime.UtcNow,
                });
                posted++;
            }
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return (IActionResult)Ok(new
            {
                companyId = req.CompanyId,
                posted,
                refused = rows.Count(r => r.Status == PeriodClosed || r.Status == Refused),
                rows = rows.Select(Project).ToList(),
            });
        });
    }

    private const string Ready = "ready";
    private const string AlreadyPosted = "already_posted";
    private const string PeriodClosed = "period_closed";
    private const string Refused = "refused";

    private sealed record Row(
        string EntityType, Guid EntityId, string Reference, string EmployeeCode, DateOnly CutoverDate, decimal CarriedAmount,
        decimal CurrentOutstanding, decimal RecoveredSinceCutover, decimal ToPost, string Currency, string Status, string? Reason,
        string? ReasonAr, OpeningBalanceJournal.Prepared? Prepared);

    private static object Project(Row r) => new
    {
        entityType = r.EntityType, entityId = r.EntityId, reference = r.Reference, employeeCode = r.EmployeeCode,
        cutoverDate = r.CutoverDate, carriedAmount = r.CarriedAmount, currentOutstanding = r.CurrentOutstanding,
        recoveredSinceCutover = r.RecoveredSinceCutover, toPost = r.ToPost, currency = r.Currency, status = r.Status,
        reason = r.Reason, reasonAr = r.ReasonAr,
    };

    private IActionResult? Refusal(Guid companyId)
    {
        if (!Roles.Any(User.IsInRole)) return Forbid();
        if (this.GetTenantId() is null) return Unauthorized();
        if (!this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        return null;
    }

    private Task<List<OpeningBalanceOrigin>> OriginsAsync(Guid tenantId, Guid companyId, CancellationToken ct) =>
        ScopedBypass.TenantWide(_db.OpeningBalanceOrigins, tenantId,
                "Opening-journal backfill: the carried-in debts of a company the caller's scope was checked for.")
            .AsNoTracking()
            .Where(o => o.CompanyId == companyId
                     && (o.EntityType == OpeningBalanceEntityTypes.Loan || o.EntityType == OpeningBalanceEntityTypes.Advance))
            .OrderBy(o => o.CutoverDate).ThenBy(o => o.EmployeeCode)
            .ToListAsync(ct);

    private async Task<List<Row>> PlanAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var origins = await OriginsAsync(tenantId, companyId, ct);
        var loanIds = origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Loan).Select(o => o.EntityId).ToList();
        var advanceIds = origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Advance).Select(o => o.EntityId).ToList();
        var loans = await ScopedBypass.TenantWide(_db.EmployeeLoans, tenantId, "Opening-journal backfill: the company's carried-in loans.")
            .AsNoTracking().Where(l => loanIds.Contains(l.Id) && !l.IsDeleted).ToDictionaryAsync(l => l.Id, ct);
        var advances = await ScopedBypass.TenantWide(_db.SalaryAdvances, tenantId, "Opening-journal backfill: the company's carried-in advances.")
            .AsNoTracking().Where(a => advanceIds.Contains(a.Id) && !a.IsDeleted).ToDictionaryAsync(a => a.Id, ct);

        var rows = new List<Row>();
        foreach (var o in origins)
        {
            OpeningBalanceDebt? debt = null;
            decimal outstanding = 0m;
            if (o.EntityType == OpeningBalanceEntityTypes.Loan && loans.TryGetValue(o.EntityId, out var loan))
            {
                debt = OpeningBalanceDebt.For(loan) with { OutstandingBalance = o.CarriedAmount };
                outstanding = loan.OutstandingBalance;
            }
            else if (o.EntityType == OpeningBalanceEntityTypes.Advance && advances.TryGetValue(o.EntityId, out var advance))
            {
                debt = OpeningBalanceDebt.For(advance) with { OutstandingBalance = o.CarriedAmount };
                outstanding = advance.OutstandingBalance;
            }
            if (debt is null) continue;   // deleted since it was carried in: nothing to book

            var recovered = Math.Max(0m, o.CarriedAmount - outstanding);
            try
            {
                var prepared = await OpeningBalanceJournal.PrepareAsync(_db, debt, o.CutoverDate, ct);
                rows.Add(new Row(o.EntityType, o.EntityId, debt.Reference, o.EmployeeCode, o.CutoverDate, o.CarriedAmount, outstanding, recovered,
                    prepared.Difference, o.Currency, prepared.Difference == 0m ? AlreadyPosted : Ready, null, null, prepared));
            }
            catch (InvalidOperationException ex)
            {
                var closed = ex.Message.StartsWith("[opening_journal_period_closed]", StringComparison.Ordinal);
                var parts = ex.Message.Split(" / ", 2);
                rows.Add(new Row(o.EntityType, o.EntityId, debt.Reference, o.EmployeeCode, o.CutoverDate, o.CarriedAmount, outstanding, recovered,
                    0m, o.Currency, closed ? PeriodClosed : Refused, parts[0], parts.Length > 1 ? parts[1] : null, null));
            }
        }
        return rows;
    }
}

/// <summary>The company whose carried-in loans and advances get their opening journals.</summary>
public record OpeningBalanceBackfillRequest(Guid CompanyId);
