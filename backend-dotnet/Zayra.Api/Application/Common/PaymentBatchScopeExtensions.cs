using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Application.Common;

/// <summary>
/// D3 — the single authority for "may this caller act on this payment batch?".
///
/// <para>THE DEFECT. <c>PayrollPaymentBatch</c>, <c>PayrollPaymentRecord</c>,
/// <c>BankPaymentConfirmation</c>, <c>FinanceGlEntry</c> and <c>SIFFileRecord</c> are
/// <c>ITenantOwned</c> ONLY — no ambient company filter reaches them. Every batch endpoint therefore
/// checked the tenant and a permission and stopped there, so a Company-A payroll officer could read
/// and mutate a Company-B batch: per-employee amounts, IBANs, bank references, WPS status, and the
/// net-pay settlement GL.</para>
///
/// <para>Worse, the run-status guards on those endpoints failed <b>OPEN</b> in exactly that case.
/// They read <c>PayrollRun</c>, which IS company-filtered, so for a cross-company caller the run came
/// back <c>null</c>, <c>run?.Status == "Voided"</c> was false, and the refusal was skipped.</para>
///
/// <para>WHY THE RUN AND NOT THE REQUEST. The company is never taken from the caller: it is read from
/// the run the batch belongs to. <c>PayrollRun</c> is <c>ICompanyScopedOperational</c> — the one
/// trustworthy carrier of the legal entity on this path.</para>
///
/// <para>This lives here, rather than being copied into each controller, so the batch authorization
/// rule has exactly ONE implementation. <c>BankConfirmationsController</c> and
/// <c>PayrollController</c> both call it.</para>
///
/// <para>FAIL-CLOSED CASES, all deliberate:</para>
/// <list type="bullet">
///   <item>Batch not in this tenant → <c>NotFound</c>, never disclosing cross-tenant existence.</item>
///   <item>Run missing, or not owned by the batch's tenant → refused. Inconsistent ownership is a
///     data-integrity fault; guessing an entity there would authorise by accident.</item>
///   <item>Run with a NULL <c>CompanyId</c> (legacy, pre-company-dimension) → GROUP callers only.</item>
/// </list>
/// </summary>
public static class PaymentBatchScopeExtensions
{
    /// <summary>
    /// Returns a refusal to return from the action, or <c>null</c> when the caller may proceed.
    /// Call it BEFORE reading an uploaded body, parsing a file, matching records, returning history or
    /// applying any mutation, and before validating a bound request body — an unauthorised caller should
    /// learn that the batch is not theirs, not that a field was spelled wrong.
    ///
    /// <para>ONE HONEST LIMIT: for an action with a <c>[FromBody]</c> parameter, ASP.NET Core has already
    /// model-bound and deserialized the body before the action runs, and <c>[ApiController]</c> may
    /// already have returned an automatic 400 on model-state failure. Nothing an action can do changes
    /// that. The claim that holds everywhere is that no batch DATA is read or written first; the stronger
    /// "the payload is never consumed" claim holds only where the action reads the stream itself, as
    /// <c>BankConfirmationsController.Import</c> does.</para>
    ///
    /// <para>This is ADDITIONAL to the endpoint's existing permission check, never a replacement.</para>
    /// </summary>
    public static async Task<IActionResult?> PaymentBatchScopeErrorAsync(
        this ControllerBase controller, ZayraDbContext db, Guid tenantId, Guid batchId, CancellationToken ct)
    {
        var batch = await db.PayrollPaymentBatches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.Id == batchId)
            .Select(b => new { b.Id, b.PayrollRunId })
            .FirstOrDefaultAsync(ct);
        if (batch is null) return controller.NotFound();

        // The COMPANY filter must not apply while we are deciding whether the caller may access this
        // company: the run's own CompanyId is the INPUT to that decision, so filtering by it first is
        // circular. Stated precisely, because an earlier revision of this comment overclaimed: reading
        // through the ambient filter would fail CLOSED, not open — the run would come back null and this
        // would return batch_run_not_resolvable. The bypass is here so the decision is made on the real
        // value and the caller gets 403 rather than a 404 that conflates "not yours" with "not there",
        // not because it would fail open. ScopedBypass.TenantWide drops exactly that one dimension and
        // re-applies the tenant itself, so this cannot become a cross-tenant read by omission.
        var run = await ScopedBypass.TenantWide(db.PayrollRuns, tenantId,
                "Company scope must be dropped to READ the run's own CompanyId — that value is the input "
                + "to the authorization decision being made here, so filtering by it first would be circular.")
            .AsNoTracking()
            .Where(r => r.Id == batch.PayrollRunId)
            .Select(r => new { r.Id, r.CompanyId })
            .FirstOrDefaultAsync(ct);
        if (run is null)
            return controller.NotFound(new
            {
                error = "batch_run_not_resolvable",
                message = "The payroll run behind this payment batch could not be resolved in this tenant, "
                        + "so the batch's legal entity cannot be established. Refused rather than guessed.",
            });

        var scope = controller.GetEntityScope();
        if (run.CompanyId is null) return scope.IsGroupLevel ? null : controller.Forbid();
        return scope.CanAccessCompany(run.CompanyId) ? null : controller.Forbid();
    }
}
