using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// The end-of-service provision panel and the two maker-checker finance journals behind it.
///
/// <list type="bullet">
/// <item><b>GET eosb/provision</b> — is monthly provisioning on for the company, what 2310 carries, and the
///   per-employee obligations (from the Lock snapshots) that explain it.</item>
/// <item><b>EOSB actuarial true-up</b> — a signed adjustment of the company's provision from an actuarial
///   report. Requested PENDING; a SECOND person approves (never the requester); only then does
///   DR/CR 5110 / 2310 post.</item>
/// <item><b>Legacy absence reclassification</b> — attendance/leave deductions used to credit 2104/2105,
///   liabilities nothing ever cleared; they now credit contra-expenses 5008/5009. The balance already
///   sitting in 2104/2105 is moved only when an accountant asks and a second person approves — never
///   automatically, never into a closed period, never for a zero balance.</item>
/// </list>
///
/// <para>Permissions: reads need finance.gl.read or finance.gl.manage; requests and decisions need
/// finance.gl.manage. Every company id is authorised against the caller's entity scope. Every
/// transition is written to the audit log with the figures it acted on.</para>
/// </summary>
[ApiController]
[Route("api/finance/gl")]
[Authorize]
public class GlAdjustmentsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public GlAdjustmentsController(ZayraDbContext db) => _db = db;

    public const string SourceModule = "GlAdjustment";
    public const decimal MaxAdjustment = 1_000_000_000m;
    private static readonly Regex PeriodPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant);

    /// <summary>The two legacy liability codes and the contra-expense driver each now routes to.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyAbsenceAccounts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["2104"] = "DED:ATTENDANCE",
        ["2105"] = "DED:LEAVE",
    };

    // ═══ End-of-service provision ═══════════════════════════════════════════════════════════════

    [HttpGet("eosb/provision")]
    public async Task<IActionResult> GetProvision([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (await CompanyError(tid.Value, companyId, ct) is { } err) return err;
        var cid = companyId!.Value;

        var gl = await GlAccountResolver.LoadAsync(_db, tid.Value, cid, ct);
        var provisionAccount = GlAccountResolver.AccountLabel(EosbProvisionLedger.ProvisionDriverKey, gl);
        var expenseAccount = GlAccountResolver.AccountLabel("EARN:EOSB", gl);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var mode = await new CompanyRatePolicyResolver(_db).ResolveAsync(tid.Value, cid, EosbProvisioningRateKeys.Mode, today, ct);
        // The latest row for the key, whatever its start — so a switch scheduled for next month is visible.
        var latestRow = await ScopedBypass.TenantWide(_db.CompanyRatePolicies, tid.Value,
                "company scope authorised above; the tenant-default row is shared by every company")
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status == CompanyPolicyStatuses.Active && p.RateKey == EosbProvisioningRateKeys.Mode
                     && (p.CompanyId == cid || p.CompanyId == null))
            .OrderByDescending(p => p.CompanyId != null).ThenByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

        // 2310 as the GL carries it for this company: Σ CR − Σ DR over every row, reversed ones included
        // (a contra is a real posting; see GlControlAccounts for why IsReversed is not a balance filter).
        var provisionRows = await _db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tid && x.CompanyId == cid
                     && (x.DebitAccount == provisionAccount || x.CreditAccount == provisionAccount))
            .Select(x => new { x.DebitAccount, x.EventType, x.Amount })
            .ToListAsync(ct);
        var glBalance = Math.Round(provisionRows.Sum(r => r.DebitAccount == provisionAccount ? -r.Amount : r.Amount), 2);
        var trueUpLayer = Math.Round(provisionRows.Where(r => r.EventType == GlEventTypes.EosbActuarialTrueUp)
            .Sum(r => r.DebitAccount == provisionAccount ? -r.Amount : r.Amount), 2);

        var snapshots = await _db.EosbProvisionSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tid && x.CompanyId == cid)
            .ToListAsync(ct);
        var latest = snapshots.GroupBy(x => x.EmployeeId)
            .Select(g => g.OrderByDescending(x => x.Period, StringComparer.Ordinal).First())
            .ToList();
        var employeeIds = latest.Select(x => x.EmployeeId).ToList();
        var people = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tid && employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName })
            .ToDictionaryAsync(e => e.Id, ct);
        var positions = await EosbProvisionLedger.LoadPositionsAsync(_db, tid.Value, employeeIds, ct);
        var employees = latest
            .Select(x => new
            {
                employeeId = x.EmployeeId,
                employeeCode = people.TryGetValue(x.EmployeeId, out var p) ? p.EmployeeCode : string.Empty,
                employeeName = people.TryGetValue(x.EmployeeId, out var q) ? q.FullName : string.Empty,
                lastMeasuredPeriod = x.Period,
                obligation = x.Obligation,
                lastMovement = x.Movement,
                wageBase = x.WageBase,
                serviceYears = x.ServiceYears,
                rule = x.Rule,
                // What the ledger still carries for the person (accruals + carried opening − consumed).
                provisionCarried = Math.Round(positions
                    .Where(pp => pp.EmployeeId == x.EmployeeId && (pp.CompanyId == cid || pp.CompanyId is null))
                    .Sum(pp => pp.Remaining), 2),
            })
            .OrderBy(x => x.employeeCode, StringComparer.Ordinal)
            .ToList();

        var history = snapshots.GroupBy(x => x.Period)
            .Select(g => new
            {
                period = g.Key,
                employees = g.Count(),
                obligation = Math.Round(g.Sum(x => x.Obligation), 2),
                movement = Math.Round(g.Sum(x => x.Movement), 2),
                catchUp = Math.Round(g.Where(x => x.IsCatchUp).Sum(x => x.Movement), 2),
            })
            .OrderByDescending(x => x.period, StringComparer.Ordinal)
            .Take(24)
            .ToList();

        return Ok(new
        {
            companyId = cid,
            provisionAccount,
            expenseAccount,
            provisioning = new
            {
                enabled = mode is not null && EosbProvisioningRateKeys.IsOn(mode.RateValue),
                value = mode?.RateValue ?? EosbProvisioningRateKeys.Off,
                effectiveFrom = mode?.EffectiveFrom,
                rateKey = EosbProvisioningRateKeys.Mode,
                // The row the panel changes (PUT /api/finance/rates/company/{id}) — or none yet (POST).
                currentRateId = latestRow?.CompanyId == cid ? latestRow?.Id : null,
                latestValue = latestRow?.RateValue,
                latestEffectiveFrom = latestRow?.EffectiveFrom,
                inheritedFromTenant = latestRow is not null && latestRow.CompanyId is null,
                scenario = "Employer termination / end of contract (full Art. 84 award), measured at each payroll month end",
            },
            glBalance,
            employeeObligation = Math.Round(employees.Sum(e => e.obligation), 2),
            employeeProvisionCarried = Math.Round(employees.Sum(e => e.provisionCarried), 2),
            trueUpLayer,
            employees,
            history,
            trueUps = await ListRequestsAsync(tid.Value, cid, GlAdjustmentKinds.EosbTrueUp, ct),
        });
    }

    [HttpGet("eosb/true-up")]
    public async Task<IActionResult> ListTrueUps([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (await CompanyError(tid.Value, companyId, ct) is { } err) return err;
        return Ok(await ListRequestsAsync(tid.Value, companyId!.Value, GlAdjustmentKinds.EosbTrueUp, ct));
    }

    [HttpPost("eosb/true-up")]
    public async Task<IActionResult> RequestTrueUp([FromBody] EosbTrueUpRequest req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (await CompanyError(tid.Value, req.CompanyId, ct) is { } err) return err;
        var cid = req.CompanyId!.Value;
        var period = (req.Period ?? string.Empty).Trim();
        if (!PeriodPattern.IsMatch(period))
            return BadRequest(new { code = "GL_PERIOD_INVALID", message = "Period must be in YYYY-MM format." });
        if (req.Amount == 0m || Math.Abs(req.Amount) > MaxAdjustment || decimal.Round(req.Amount, 2) != req.Amount)
            return BadRequest(new { code = "GL_ADJUSTMENT_AMOUNT_INVALID",
                message = "Enter a non-zero amount with at most two decimals: positive raises the provision, negative lowers it." });
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { code = "GL_ADJUSTMENT_REASON_REQUIRED", message = "Say why the provision is being adjusted." });
        if (string.IsNullOrWhiteSpace(req.ReportReference))
            return BadRequest(new { code = "GL_ADJUSTMENT_REFERENCE_REQUIRED",
                message = "Name the actuarial report the figure comes from (its reference or date)." });
        if (await PeriodCloseGuard.IsClosedAsync(_db, tid.Value, cid, period, ct))
            return UnprocessableEntity(new { error = "gl_period_closed", message = $"GL period {period} is closed. Choose an open period.", period });
        if (await _db.GlAdjustmentRequests.AnyAsync(x => x.TenantId == tid && x.CompanyId == cid
                && x.Kind == GlAdjustmentKinds.EosbTrueUp && x.Status == GlAdjustmentStatuses.Pending && x.Period == period, ct))
            return Conflict(new { code = "GL_ADJUSTMENT_ALREADY_PENDING",
                message = $"A true-up for {period} is already waiting for approval. Approve or reject it first." });

        var gl = await GlAccountResolver.LoadAsync(_db, tid.Value, cid, ct);
        var row = new GlAdjustmentRequest
        {
            TenantId = tid.Value, CompanyId = cid, Kind = GlAdjustmentKinds.EosbTrueUp,
            Period = period, AccountingDate = PeriodEnd(period), Amount = req.Amount,
            Currency = await CompanyCurrencyAsync(tid.Value, cid, ct),
            SourceAccount = GlAccountResolver.AccountLabel("EARN:EOSB", gl),
            TargetAccount = GlAccountResolver.AccountLabel(EosbProvisionLedger.ProvisionDriverKey, gl),
            Reason = req.Reason.Trim(), ReportReference = req.ReportReference.Trim(),
            RequestedByUserId = this.GetUserId(), RequestedByName = UserName(),
        };
        _db.GlAdjustmentRequests.Add(row);
        WriteAudit("finance.gl.eosb_true_up.requested", row, new { row.Period, row.Amount, row.Reason, row.ReportReference });
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(row));
    }

    // ═══ Legacy 2104 / 2105 absence-deduction balances ══════════════════════════════════════════

    [HttpGet("reclassifications/legacy-absence")]
    public async Task<IActionResult> GetLegacyAbsence([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (await CompanyError(tid.Value, companyId, ct) is { } err) return err;
        var cid = companyId!.Value;
        var gl = await GlAccountResolver.LoadAsync(_db, tid.Value, cid, ct);
        var balances = await LegacyBalancesAsync(tid.Value, cid, ct);
        return Ok(new
        {
            companyId = cid,
            accounts = balances.Attributed.Select(b => new
            {
                account = b.Account, code = b.Code, debits = b.Debits, credits = b.Credits,
                // Credit-positive: what the old liability still says is "owed".
                balance = b.Balance,
                suggestedTarget = GlAccountResolver.AccountLabel(LegacyAbsenceAccounts[b.Code], gl),
                pendingRequestId = b.PendingRequestId,
            }),
            // Rows with no legal entity (posted before company attribution existed) are reported, not moved:
            // a company's reclassification must not empty a pool other entities share.
            unattributed = balances.Unattributed.Select(b => new { account = b.Account, code = b.Code, balance = b.Balance }),
            requests = await ListRequestsAsync(tid.Value, cid, GlAdjustmentKinds.LegacyAbsenceReclass, ct),
        });
    }

    [HttpPost("reclassifications/legacy-absence")]
    public async Task<IActionResult> RequestLegacyReclass([FromBody] LegacyAbsenceReclassRequest req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (await CompanyError(tid.Value, req.CompanyId, ct) is { } err) return err;
        var cid = req.CompanyId!.Value;
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { code = "GL_ADJUSTMENT_REASON_REQUIRED", message = "Say why the balance is being moved." });
        if (req.AccountingDate == default)
            return BadRequest(new { code = "GL_ADJUSTMENT_DATE_REQUIRED", message = "Choose the accounting date to post on." });
        var period = req.AccountingDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (await PeriodCloseGuard.IsClosedAsync(_db, tid.Value, cid, period, ct))
            return UnprocessableEntity(new { error = "gl_period_closed",
                message = $"The accounting date falls in {period}, which is closed. Choose a date in an open period.", period });

        var balances = await LegacyBalancesAsync(tid.Value, cid, ct);
        var source = balances.Attributed.FirstOrDefault(b => string.Equals(b.Account, req.SourceAccount?.Trim(), StringComparison.Ordinal));
        if (source is null || source.Balance == 0m)
            return UnprocessableEntity(new { error = "legacy_balance_zero",
                message = "There is no balance on that account for this company, so there is nothing to move." });
        if (source.PendingRequestId is not null)
            return Conflict(new { code = "GL_ADJUSTMENT_ALREADY_PENDING",
                message = "A move of this account's balance is already waiting for approval. Approve or reject it first." });

        var gl = await GlAccountResolver.LoadAsync(_db, tid.Value, cid, ct);
        string target;
        if (string.IsNullOrWhiteSpace(req.TargetAccountCode))
            target = GlAccountResolver.AccountLabel(LegacyAbsenceAccounts[source.Code], gl);
        else
        {
            var code = req.TargetAccountCode.Trim();
            var account = await ScopedBypass.TenantWide(_db.GlAccounts, tid.Value,
                    "company scope authorised above; the tenant-default chart is shared by every company")
                .AsNoTracking()
                .Where(a => a.Code == code && a.IsActive && (a.CompanyId == cid || a.CompanyId == null))
                .OrderByDescending(a => a.CompanyId != null)
                .FirstOrDefaultAsync(ct);
            if (account is null)
                return BadRequest(new { code = "GL_ACCOUNT_NOT_IN_CHART",
                    message = $"Account {code} is not an active account in this company's chart." });
            if (account.AccountType is not ("Expense" or "Equity"))
                return BadRequest(new { code = "GL_ADJUSTMENT_TARGET_INVALID",
                    message = $"Account {code} is a {account.AccountType} account. Move the balance to the contra-expense, " +
                              "or to a retained-earnings (Equity) account for a prior, closed financial year." });
            target = $"{account.Code} - {account.Name}";
            if (target == source.Account)
                return BadRequest(new { code = "GL_ADJUSTMENT_TARGET_INVALID", message = "Choose a different account to move the balance to." });
        }

        var row = new GlAdjustmentRequest
        {
            TenantId = tid.Value, CompanyId = cid, Kind = GlAdjustmentKinds.LegacyAbsenceReclass,
            Period = period, AccountingDate = req.AccountingDate, Amount = source.Balance,
            Currency = await CompanyCurrencyAsync(tid.Value, cid, ct),
            SourceAccount = source.Account, TargetAccount = target, Reason = req.Reason.Trim(),
            RequestedByUserId = this.GetUserId(), RequestedByName = UserName(),
        };
        _db.GlAdjustmentRequests.Add(row);
        WriteAudit("finance.gl.legacy_absence_reclass.requested", row,
            new { row.SourceAccount, row.TargetAccount, row.Amount, row.AccountingDate, row.Reason });
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(row));
    }

    // ═══ Decisions (both kinds) ═════════════════════════════════════════════════════════════════

    /// <summary>Approves a pending request and posts its journal in the same transaction. Refused to the
    /// person who requested it (strict maker ≠ checker), into a closed period, and — for a reclass —
    /// when the balance has moved since the request (the approver must approve the figure that posts).</summary>
    [HttpPost("adjustments/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] GlAdjustmentDecisionRequest? req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        return await InTransactionAsync(() => ApproveCoreAsync(tid.Value, id, req?.Note, ct), ct);
    }

    [HttpPost("adjustments/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] GlAdjustmentDecisionRequest? req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req?.Note))
            return BadRequest(new { code = "GL_ADJUSTMENT_REASON_REQUIRED", message = "Say why the request is rejected." });
        return await InTransactionAsync(() => RejectCoreAsync(tid.Value, id, req.Note.Trim(), ct), ct);
    }

    /// <summary>
    /// Takes the same row lock as <see cref="ApproveCoreAsync"/>, so a concurrent approve and reject are
    /// serialised: whichever commits second re-reads the decided status and gets 409. Without the lock a
    /// reject could read the stale Pending row and overwrite an approval whose journal had already posted.
    /// </summary>
    private async Task<IActionResult> RejectCoreAsync(Guid tid, Guid id, string note, CancellationToken ct)
    {
        var row = await _db.GlAdjustmentRequests
            .TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (row is null) return NotFound();
        if (await CompanyError(tid, row.CompanyId, ct) is { } err) return err;
        if (row.Status != GlAdjustmentStatuses.Pending)
            return Conflict(new { code = "GL_ADJUSTMENT_NOT_PENDING", message = $"This request is already {row.Status.ToLowerInvariant()}." });
        row.Status = GlAdjustmentStatuses.Rejected;
        row.DecidedByUserId = this.GetUserId();
        row.DecidedByName = UserName();
        row.DecidedAtUtc = DateTime.UtcNow;
        row.DecisionNote = note;
        row.UpdatedAtUtc = DateTime.UtcNow;
        WriteAudit(AuditPrefix(row) + ".rejected", row, new { row.Amount, note = row.DecisionNote, requestedBy = row.RequestedByUserId });
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(row));
    }

    private async Task<IActionResult> ApproveCoreAsync(Guid tid, Guid id, string? note, CancellationToken ct)
    {
        var row = await _db.GlAdjustmentRequests
            .TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (row is null) return NotFound();
        if (await CompanyError(tid, row.CompanyId, ct) is { } err) return err;
        if (row.Status != GlAdjustmentStatuses.Pending)
            return Conflict(new { code = "GL_ADJUSTMENT_NOT_PENDING", message = $"This request is already {row.Status.ToLowerInvariant()}." });
        var me = this.GetUserId();
        if (me is null || me == row.RequestedByUserId)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "GL_ADJUSTMENT_SOD",
                message = "You requested this journal, so someone else must approve it.",
            });
        var cid = row.CompanyId!.Value;
        if (await PeriodCloseGuard.IsClosedAsync(_db, tid, cid, row.Period, ct))
            return UnprocessableEntity(new { error = "gl_period_closed",
                message = $"GL period {row.Period} closed after this was requested. Reopen it, or reject this and request it again in an open period.",
                period = row.Period });

        var lines = new List<FinanceGlEntry>();
        FinanceGlEntry Line(string debit, string credit, string eventType, string description) => new()
        {
            TenantId = tid, CompanyId = cid, SourceModule = SourceModule, SourceEntityId = row.Id,
            SourceEntityRef = row.ReportReference ?? row.Kind, EventType = eventType,
            DebitAccount = debit, CreditAccount = credit, Amount = Math.Abs(row.Amount), Currency = row.Currency,
            EntryDate = row.AccountingDate, Period = row.Period, Description = description,
            PostedBy = me, PostedByName = UserName() ?? string.Empty,
        };

        if (row.Kind == GlAdjustmentKinds.EosbTrueUp)
        {
            var expense = row.SourceAccount!;
            var provision = row.TargetAccount!;
            var balance = await ProvisionBalanceAsync(tid, cid, provision, ct);
            if (balance + row.Amount < 0m)
                return UnprocessableEntity(new { error = "provision_would_go_negative",
                    message = $"The provision carries {balance:N2}; lowering it by {-row.Amount:N2} would leave a debit balance on a liability. Reject this and request a smaller reduction.",
                    provisionBalance = balance });
            var text = $"EOSB actuarial true-up ({row.ReportReference}): {row.Reason}";
            if (row.Amount > 0m)
            {
                lines.Add(Line(expense, string.Empty, GlEventTypes.EosbActuarialTrueUp, text));
                lines.Add(Line(string.Empty, provision, GlEventTypes.EosbActuarialTrueUp, text));
            }
            else
            {
                lines.Add(Line(provision, string.Empty, GlEventTypes.EosbActuarialTrueUp, text));
                lines.Add(Line(string.Empty, expense, GlEventTypes.EosbActuarialTrueUp, text));
            }
        }
        else
        {
            var balances = await LegacyBalancesAsync(tid, cid, ct);
            var now = balances.Attributed.FirstOrDefault(b => b.Account == row.SourceAccount)?.Balance ?? 0m;
            if (now != row.Amount)
                return Conflict(new
                {
                    code = "GL_ADJUSTMENT_BALANCE_CHANGED",
                    message = $"{row.SourceAccount} now carries {now:N2}, not the {row.Amount:N2} that was requested. " +
                              "Reject this request and ask again so the figure approved is the figure that posts.",
                    requested = row.Amount, current = now,
                });
            var text = $"Legacy absence-deduction balance moved from {row.SourceAccount}: {row.Reason}";
            // A credit balance (the normal case) is emptied by a debit; a debit balance by a credit.
            if (row.Amount > 0m)
            {
                lines.Add(Line(row.SourceAccount!, string.Empty, GlEventTypes.LegacyAbsenceReclass, text));
                lines.Add(Line(string.Empty, row.TargetAccount!, GlEventTypes.LegacyAbsenceReclass, text));
            }
            else
            {
                lines.Add(Line(row.TargetAccount!, string.Empty, GlEventTypes.LegacyAbsenceReclass, text));
                lines.Add(Line(string.Empty, row.SourceAccount!, GlEventTypes.LegacyAbsenceReclass, text));
            }
        }

        row.Status = GlAdjustmentStatuses.Approved;
        row.DecidedByUserId = me;
        row.DecidedByName = UserName();
        row.DecidedAtUtc = DateTime.UtcNow;
        row.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        row.UpdatedAtUtc = DateTime.UtcNow;
        _db.FinanceGlEntries.AddRange(lines);
        WriteAudit(AuditPrefix(row) + ".approved", row, new
        {
            row.Amount, row.SourceAccount, row.TargetAccount, row.Period, row.Reason,
            requestedBy = row.RequestedByUserId, approvedBy = me, note = row.DecisionNote,
            postedLines = lines.Select(l => new { l.DebitAccount, l.CreditAccount, l.Amount }),
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { request = ToDto(row), postedLines = lines.Count });
    }

    // ═══ Helpers ════════════════════════════════════════════════════════════════════════════════

    private sealed record LegacyBalance(Guid? CompanyId, string Account, string Code, decimal Debits, decimal Credits, Guid? PendingRequestId)
    {
        public decimal Balance => Math.Round(Credits - Debits, 2);
    }

    /// <summary>Balances of every account label that starts with 2104/2105 for one company (and, separately,
    /// the unattributed pool). Computed from the ledger exactly as <see cref="GlControlAccounts"/> does:
    /// Σ over every row naming the label, reversed rows included.</summary>
    private async Task<(List<LegacyBalance> Attributed, List<LegacyBalance> Unattributed)> LegacyBalancesAsync(
        Guid tid, Guid cid, CancellationToken ct)
    {
        var rows = await _db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tid && (x.CompanyId == cid || x.CompanyId == null)
                     && (x.DebitAccount.StartsWith("2104 - ") || x.DebitAccount.StartsWith("2105 - ")
                      || x.CreditAccount.StartsWith("2104 - ") || x.CreditAccount.StartsWith("2105 - ")))
            .Select(x => new { x.CompanyId, x.DebitAccount, x.CreditAccount, x.Amount })
            .ToListAsync(ct);
        static string? Legacy(string label) =>
            label.StartsWith("2104 - ", StringComparison.Ordinal) || label.StartsWith("2105 - ", StringComparison.Ordinal) ? label : null;
        var flat = rows.SelectMany(r => new[]
            {
                (r.CompanyId, Account: Legacy(r.DebitAccount), Dr: r.Amount, Cr: 0m),
                (r.CompanyId, Account: Legacy(r.CreditAccount), Dr: 0m, Cr: r.Amount),
            })
            .Where(x => x.Account is not null)
            .GroupBy(x => (x.CompanyId, x.Account!))
            .Select(g => (g.Key.CompanyId, Account: g.Key.Item2, Dr: g.Sum(x => x.Dr), Cr: g.Sum(x => x.Cr)))
            .ToList();
        var pending = await _db.GlAdjustmentRequests.AsNoTracking()
            .Where(x => x.TenantId == tid && x.CompanyId == cid && x.Kind == GlAdjustmentKinds.LegacyAbsenceReclass
                     && x.Status == GlAdjustmentStatuses.Pending)
            .Select(x => new { x.Id, x.SourceAccount })
            .ToListAsync(ct);
        LegacyBalance Make((Guid? CompanyId, string Account, decimal Dr, decimal Cr) x) => new(
            x.CompanyId, x.Account, x.Account[..4], Math.Round(x.Dr, 2), Math.Round(x.Cr, 2),
            x.CompanyId is null ? null : pending.FirstOrDefault(p => p.SourceAccount == x.Account)?.Id);
        return (
            flat.Where(x => x.CompanyId == cid).Select(Make).Where(b => b.Balance != 0m || b.PendingRequestId is not null)
                .OrderBy(b => b.Account, StringComparer.Ordinal).ToList(),
            flat.Where(x => x.CompanyId is null).Select(Make).Where(b => b.Balance != 0m)
                .OrderBy(b => b.Account, StringComparer.Ordinal).ToList());
    }

    private async Task<decimal> ProvisionBalanceAsync(Guid tid, Guid cid, string provisionAccount, CancellationToken ct)
    {
        var rows = await _db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tid && x.CompanyId == cid
                     && (x.DebitAccount == provisionAccount || x.CreditAccount == provisionAccount))
            .Select(x => new { x.DebitAccount, x.Amount })
            .ToListAsync(ct);
        return Math.Round(rows.Sum(r => r.DebitAccount == provisionAccount ? -r.Amount : r.Amount), 2);
    }

    private async Task<List<object>> ListRequestsAsync(Guid tid, Guid cid, string kind, CancellationToken ct)
    {
        var rows = await _db.GlAdjustmentRequests.AsNoTracking()
            .Where(x => x.TenantId == tid && x.CompanyId == cid && x.Kind == kind)
            .OrderByDescending(x => x.RequestedAtUtc)
            .Take(100)
            .ToListAsync(ct);
        var me = this.GetUserId();
        var mayDecide = HasPermission("finance.gl.manage");
        return rows.Select(r => ToDto(r, me, mayDecide)).ToList();
    }

    private object ToDto(GlAdjustmentRequest r) => ToDto(r, this.GetUserId(), HasPermission("finance.gl.manage"));

    private static object ToDto(GlAdjustmentRequest r, Guid? me, bool mayDecide) => new
    {
        r.Id, r.CompanyId, r.Kind, r.Status, r.Period, r.AccountingDate, r.Amount, r.Currency,
        r.SourceAccount, r.TargetAccount, r.Reason, r.ReportReference,
        r.RequestedByName, r.RequestedAtUtc, r.DecidedByName, r.DecidedAtUtc, r.DecisionNote,
        // The panel hides Approve for the requester rather than letting them hit the SoD refusal.
        requestedByMe = me is not null && r.RequestedByUserId == me,
        // The server's answer to "may THIS viewer approve it?" — pending, not theirs, and allowed to decide.
        canApprove = mayDecide && me is not null && r.Status == GlAdjustmentStatuses.Pending && r.RequestedByUserId != me,
    };

    private static string AuditPrefix(GlAdjustmentRequest r) => r.Kind == GlAdjustmentKinds.EosbTrueUp
        ? "finance.gl.eosb_true_up" : "finance.gl.legacy_absence_reclass";

    private async Task<IActionResult?> CompanyError(Guid tid, Guid? companyId, CancellationToken ct)
    {
        if (companyId is not Guid cid)
            return BadRequest(new { code = "COMPANY_REQUIRED", message = "Choose the company (legal entity) first." });
        if (!this.GetEntityScope().CanAccessCompany(cid)) return Forbid();
        var exists = await ScopedBypass.TenantWide(_db.Companies, tid,
                "company scope was authorised above; only tenant membership of the requested company is checked")
            .AsNoTracking().AnyAsync(c => c.Id == cid, ct);
        return exists ? null : NotFound(new { message = "Company not found." });
    }

    private async Task<string> CompanyCurrencyAsync(Guid tid, Guid cid, CancellationToken ct)
    {
        var currency = await ScopedBypass.TenantWide(_db.Companies, tid, "company scope was authorised by the caller")
            .AsNoTracking().Where(c => c.Id == cid).Select(c => c.DefaultCurrency).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(currency) ? "SAR" : currency.Trim().ToUpperInvariant();
    }

    private static DateOnly PeriodEnd(string period)
    {
        var year = int.Parse(period[..4], CultureInfo.InvariantCulture);
        var month = int.Parse(period[5..], CultureInfo.InvariantCulture);
        return new DateOnly(year, month, DateTime.DaysInMonth(year, month));
    }

    /// <summary>Runs <paramref name="body"/> in one transaction (row-locked on the request), committing only
    /// a success. EF InMemory and an ambient caller transaction run it directly, as Lock does.</summary>
    private async Task<IActionResult> InTransactionAsync(Func<Task<IActionResult>> body, CancellationToken ct)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null) return await body();
        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await body();
            if (result is ObjectResult { StatusCode: >= 200 and < 300 } or OkResult) await tx.CommitAsync(ct);
            return result;
        });
    }

    private bool CanRead() => HasPermission("finance.gl.read") || HasPermission("finance.gl.manage");

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private string? UserName() => User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name");

    private void WriteAudit(string action, GlAdjustmentRequest row, object metadata) =>
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = row.TenantId,
            CompanyId = row.CompanyId,
            Action = action,
            EntityName = nameof(GlAdjustmentRequest),
            EntityId = row.Id.ToString(),
            UserId = this.GetUserId(),
            IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Metadata = JsonSerializer.Serialize(metadata),
            CreatedAtUtc = DateTime.UtcNow,
        });
}

/// <summary>Signed: + raises the provision (DR 5110 / CR 2310), − lowers it.</summary>
public record EosbTrueUpRequest(Guid? CompanyId, string? Period, decimal Amount, string? Reason, string? ReportReference);
/// <param name="SourceAccount">The legacy account label exactly as GET returns it ("2104 - …").</param>
/// <param name="TargetAccountCode">Optional: a retained-earnings (Equity) or other expense account code for a
/// prior, closed financial year. Omitted = the contra-expense the account now routes to (5008 / 5009).</param>
public record LegacyAbsenceReclassRequest(Guid? CompanyId, string? SourceAccount, DateOnly AccountingDate, string? TargetAccountCode, string? Reason);
public record GlAdjustmentDecisionRequest(string? Note);
