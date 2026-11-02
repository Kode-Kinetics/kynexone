using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// Loan types in Setup follow the one removal rule: Edit, Remove (a loan type is turned off, never erased, so issued loans and
/// grade limits keep resolving their type) and Restore. The code is editable only until the type is used.
/// </summary>
public partial class LoansController
{
    private const string LoanTypeUsageWhy = "Removing a loan type must see every loan and policy that uses it in the tenant, whatever company the caller's switcher shows.";

    /// <summary>Field validation shared by Create and Edit (one rule, two doors).</summary>
    private static IActionResult? ValidateLoanType(LoanTypeRequest req)
    {
        if (req.RepaymentFrequency is not ("Monthly" or "Weekly" or "BiWeekly" or "Quarterly"))
            return new BadRequestObjectResult("Repayment frequency must be Monthly, Weekly, BiWeekly, or Quarterly.");
        if (!req.IsInterestFree || req.InterestRate != 0)
            return new BadRequestObjectResult(new { error = LoanEligibilityCodes.InterestNotPermitted, message = LoanEligibilityCodes.InterestNotPermittedText });
        if (req.MaxInstallments is < 1 or > 600 || req.MinServiceMonths is < 0 or > 600 || req.MaxAmount < 0 || decimal.Round(req.MaxAmount, 2) != req.MaxAmount || req.MaxAmount > 999999999999.99m)
            return new BadRequestObjectResult("Set 1–600 maximum installments and a nonnegative two-decimal maximum amount.");
        if (req.MinInstallments < 1 || req.MinInstallments > req.MaxInstallments)
            return new BadRequestObjectResult(new { error = "invalid_min_installments", message = $"Set the shortest repayment between 1 and the longest repayment ({req.MaxInstallments} instalments)." });
        if (string.IsNullOrWhiteSpace(req.Code) || req.Code.Trim().Length > 50)
            return new BadRequestObjectResult(new { error = "invalid_code", message = "Enter a loan type code of up to 50 characters." });
        return null;
    }

    private Task<SetupRemoval.Measured> LoanTypeUsageAsync(Guid tid, Guid id, CancellationToken ct) => SetupRemoval.MeasureAsync(this, new[]
    {
        SetupRemoval.Of<EmployeeLoan>(_db, tid, "loan", "loans", x => x.LoanTypeId == id),
        SetupRemoval.Of<LoanPolicy>(_db, tid, "loanPolicy", "loan policies", x => x.LoanTypeId == id),
    }, ct);

    [HttpPut("types/{id:guid}")]
    [HasPermission("loans.write")]
    public async Task<IActionResult> UpdateLoanType(Guid id, [FromBody] LoanTypeRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (ValidateLoanType(req) is { } invalid) return invalid;
        var t = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (t is null) return NotFound();

        var newCode = req.Code.Trim();
        if (!string.Equals(newCode, t.Code, StringComparison.Ordinal))
        {
            var used = await LoanTypeUsageAsync(tid, id, ct);
            if (used.InUse)
                return SetupRemoval.Blocked(this, "LOAN_TYPE_CODE_IN_USE",
                    "The code is fixed because loans or loan policies already use this loan type. Change the name or limits instead.");
            if (await _db.LoanTypes.AnyAsync(x => x.TenantId == tid && x.Id != id && x.Code == newCode && !x.IsDeleted, ct))
                return Conflict("Loan type code already exists.");
            var facility = GradeLoanLimitResolver.FacilityCodeFor(newCode);
            var similar = (await _db.LoanTypes.AsNoTracking().Where(x => x.TenantId == tid && x.Id != id && !x.IsDeleted)
                    .Select(x => new { x.Code, x.NameEn, x.EntitlementComponentCode }).ToListAsync(ct))
                .FirstOrDefault(x => GradeLoanLimitResolver.FacilityCodeFor(x.Code) == facility || x.EntitlementComponentCode == facility);
            if (similar != null)
                return Conflict(new { error = "loan_type_code_too_similar", message = $"The code {newCode} is too similar to the existing loan type {similar.NameEn} ({similar.Code}). Choose a code that differs by more than case or punctuation." });
            t.Code = newCode;
        }

        t.NameEn = req.NameEn; t.NameAr = req.NameAr ?? string.Empty;
        t.MaxAmount = req.MaxAmount; t.MaxInstallments = req.MaxInstallments; t.MinInstallments = req.MinInstallments;
        t.RepaymentFrequency = req.RepaymentFrequency; t.MinServiceMonths = req.MinServiceMonths; t.RequiresApproval = req.RequiresApproval;
        this.RecordChange(_db, tid, null, "LoanType", t.Id.ToString(), "Updated", null, new { t.Code, t.NameEn, t.MaxAmount, t.MaxInstallments, t.MinInstallments });
        await _db.SaveChangesAsync(ct);
        return Ok(t);
    }

    [HttpDelete("types/{id:guid}")]
    [HasPermission("loans.write")]
    public async Task<IActionResult> RemoveLoanType(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var t = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (t is null) return NotFound();
        var used = await LoanTypeUsageAsync(tid, id, ct);
        // A loan type is always turned off, never erased: the grade loan limits derive their facility from its code, and an
        // unused type must still be restorable from Setup.
        t.IsActive = false;
        this.RecordChange(_db, tid, null, "LoanType", id.ToString(), "TurnedOff", new { t.Code }, new { references = used.WideTotal });
        await _db.SaveChangesAsync(ct);
        return SetupRemoval.TurnedOff(this, used);
    }

    [HttpPost("types/{id:guid}/restore")]
    [HasPermission("loans.write")]
    public async Task<IActionResult> RestoreLoanType(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var t = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (t is null) return NotFound();
        if (!t.IsActive)
        {
            t.IsActive = true;
            this.RecordChange(_db, tid, null, "LoanType", id.ToString(), "Restored", null, new { t.Code });
            await _db.SaveChangesAsync(ct);
        }
        return SetupRemoval.Restored(this);
    }
}
