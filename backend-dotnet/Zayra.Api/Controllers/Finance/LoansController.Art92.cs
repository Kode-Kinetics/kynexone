using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Finance;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// Saudi Labour Law Art. 92 on loan requests (Release A slice R3): an employer-loan instalment deducted from pay above
/// 10% of the wage needs the employee's signed <see cref="RestrictedEmployeeDocumentTypes.LoanDeductionConsent"/> on their
/// file. Enforced only for tenants with release_a on, so no live tenant changes behaviour until the platform enables it.
/// The check runs at the request and again at every approval (the approver may change the terms).
/// </summary>
public partial class LoansController
{
    /// <summary>True when the tenant has Release A on. No module service (a bare unit-test controller) means off.</summary>
    private async Task<bool> ReleaseAEnabledAsync(Guid tenantId, CancellationToken ct)
    {
        var modules = HttpContext?.RequestServices?.GetService(typeof(ITenantModuleService)) as ITenantModuleService;
        return modules is not null && (await modules.GetStateAsync(tenantId, ct)).IsEnabled(FeatureKeys.ReleaseA);
    }

    /// <summary>The refusal when the Art. 92 consent is missing or is not a consent on this employee's file; null when the
    /// request may proceed. A consent document that is offered is always checked, required or not.</summary>
    private async Task<IActionResult?> Art92RefusalAsync(Guid tenantId, int employeeId, LoanArt92Check? art92,
        Guid? consentDocumentId, CancellationToken ct)
    {
        if (consentDocumentId is Guid documentId && !await ConsentOnFileAsync(tenantId, employeeId, documentId, ct))
            return BadRequest(Art92Refusal(art92,
                "The consent document was not found on this employee's file as a signed loan-deduction consent."));
        if (art92 is { RequiresConsent: true } && consentDocumentId is null)
            return BadRequest(Art92Refusal(art92, null));
        return null;
    }

    private static object Art92Refusal(LoanArt92Check? art92, string? detail)
    {
        var reason = ReleaseABlockReasons.Get(ReleaseABlockReasons.LoanInstalmentOver10PctNoConsent);
        return new { error = reason.Code, reason, message = detail ?? reason.WhyEn, detail, art92 = Art92Dto(art92, true) };
    }

    private Task<bool> ConsentOnFileAsync(Guid tenantId, int employeeId, Guid documentId, CancellationToken ct)
    {
        var type = RestrictedEmployeeDocumentTypes.LoanDeductionConsent.ToLowerInvariant();
        return _db.EmployeeDocuments.AsNoTracking().AnyAsync(d => d.TenantId == tenantId && d.Id == documentId
            && d.EmployeeId == employeeId && !d.IsDeleted && d.DocumentType.Trim().ToLower() == type, ct);
    }

    /// <summary>
    /// Attaches the employee's signed Art. 92 consent to a PENDING loan, so a request whose instalment is above 10% of the
    /// wage can be approved (approval re-checks Art. 92 and refuses without it). The borrower (loans.self, their own loan
    /// only) uploads the signed form; HR (loans.write, within its data scope) uploads it or names a consent already on the
    /// employee's file. The document is stored as a restricted LoanDeductionConsent (never listed in self-service), the
    /// wage the test used is stamped as the cap_base_wage witness, and the act is written to the loan's audit trail.
    /// </summary>
    [HttpPost("{id:guid}/consent")]
    [HasPermission("loans.self", "loans.write")]
    [RequireOptInFeature(FeatureKeys.ReleaseA)]
    [RequestSizeLimit(10_485_760)]
    [Consumes("multipart/form-data")]
    public Task<IActionResult> AttachLoanConsent(Guid id, [FromForm] LoanConsentForm form,
        [FromServices] IEmployeeManagementService employees, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeLoan, GetTenantId(), id, async () =>
        {
            var tid = GetTenantId();
            var uid = GetUserId();
            if (uid is null) return Unauthorized();
            var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
            if (loan is null || loan.EmployeeIntId is not int employeeId) return NotFound();
            var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == employeeId && !x.IsDeleted, ct);
            if (employee is null) return NotFound();

            var isBorrower = User.HasPermission("loans.self") && employee.UserAccountId == uid;
            var isHr = User.HasPermission("loans.write") && IsHrLoanActor() && await CanAccessLoanAsync(loan, ct);
            if (!isBorrower && !isHr) return NotFound(); // another person's loan is indistinguishable from a missing one
            if (loan.Status != "Pending")
                return Conflict(new { error = "loan_not_pending", message = "A consent can be attached only while the loan request is pending." });
            if ((form.File is null) == (form.DocumentId is null))
                return BadRequest(new { error = "consent_input", message = "Upload the signed consent, or (HR) choose one already on the employee's file." });
            if (form.DocumentId is not null && !isHr) return NotFound();

            Guid documentId;
            if (form.DocumentId is Guid existing)
            {
                if (await Art92RefusalAsync(tid, employeeId, null, existing, ct) is { } refusal) return refusal;
                documentId = existing;
            }
            else
            {
                try
                {
                    var document = await employees.UploadDocumentAsync(tid, employeeId,
                        new EmployeeDocumentUploadMetadata(RestrictedEmployeeDocumentTypes.LoanDeductionConsent, "Loan",
                            null, null, null, false, null, $"Art. 92 consent for loan {loan.LoanNumber}"),
                        form.File!, new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                            Request.Headers.UserAgent.ToString(), uid, tid), ct);
                    documentId = document.Id;
                }
                catch (InvalidOperationException ex) { return BadRequest(new { error = "consent_upload", message = ex.Message }); }
            }

            var installments = loan.ApprovedInstallments > 0 ? loan.ApprovedInstallments : loan.RequestedInstallments;
            var amount = loan.ApprovedAmount > 0 ? loan.ApprovedAmount : loan.RequestedAmount;
            var art92 = await new LoanEligibilityService(_db).Art92ForInstalmentAsync(tid, employee,
                amount / Math.Max(1, installments), loan.RepaymentMethod, ct);
            var previous = loan.ConsentDocumentId;
            loan.ConsentDocumentId = documentId;
            loan.CapBaseWage = art92.WageDue;
            loan.UpdatedAtUtc = DateTime.UtcNow;
            loan.UpdatedBy = uid;
            AddLoanAudit(loan.Id, "Art92ConsentAttached", new
            {
                ConsentDocumentId = documentId, PreviousConsentDocumentId = previous, loan.CapBaseWage, art92.Instalment, art92.Pct,
                AttachedBy = isBorrower ? "Borrower" : "HR",
            });
            await _db.SaveChangesAsync(ct);
            return Ok(EmployeeLoanDto.Project(loan));
        }, ct);

    /// <summary>
    /// Art. 92 when a loan moves onto payroll collection after the request (the CollectionMethod change, checked at the
    /// request and again at the decision). Same rule, wage and block code as a new request; null when it may proceed or
    /// Release A is off. A reschedule needs no check: ValidateReschedule refuses payroll-deducted loans outright.
    /// </summary>
    private async Task<IActionResult?> Art92ForChangedInstalmentAsync(EmployeeLoan loan, decimal monthlyInstalment,
        string repaymentMethod, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (repaymentMethod != "PayrollDeduction" || !await ReleaseAEnabledAsync(tid, ct)) return null;
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == loan.EmployeeIntId && !x.IsDeleted, ct);
        if (employee is null) return Conflict("Employee requires HR review before the instalment can change.");
        var art92 = await new LoanEligibilityService(_db).Art92ForInstalmentAsync(tid, employee, monthlyInstalment, repaymentMethod, ct);
        return await Art92RefusalAsync(tid, employee.Id, art92, loan.ConsentDocumentId, ct);
    }

    /// <summary>The eligibility response's <c>art92</c> block. The wage (and the share, which reveals it) only to callers who
    /// may see the salary.</summary>
    private static object? Art92Dto(LoanArt92Check? a, bool includeSalary) => a is null ? null : new
    {
        a.Instalment,
        wageDue = includeSalary ? a.WageDue : null,
        pct = includeSalary ? a.Pct : null,
        a.RequiresConsent,
        a.DeductedFromPay,
        thresholdPercent = LoanArt92Check.ThresholdPercent,
    };
}

/// <summary>Attach-consent input: the signed file, or (HR only) a LoanDeductionConsent already on the employee's file.</summary>
public sealed class LoanConsentForm
{
    public IFormFile? File { get; set; }
    public Guid? DocumentId { get; set; }
}
