using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
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
