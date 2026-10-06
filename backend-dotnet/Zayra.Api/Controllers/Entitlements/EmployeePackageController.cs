using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Entitlements;

/// <summary>
/// The employee's package for HR (Release A slice R2): pay in the Qiwa contract, contract benefits fixed for the term,
/// facilities by current policy — each line with what it is based on. Closed unless the tenant has release_a on
/// (FeatureFlagGuardFilter covers /api/entitlements). Company scope: the employee must be visible to the caller.
/// </summary>
[Authorize]
[ApiController]
[Route("api/entitlements")]
public sealed class EmployeePackageController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IEntitlementResolver _resolver;
    private readonly IEntitlementWriter _writer;
    private readonly ITenantClock _clock;

    public EmployeePackageController(ZayraDbContext db, IEntitlementResolver resolver, IEntitlementWriter writer, ITenantClock clock)
    {
        _db = db;
        _resolver = resolver;
        _writer = writer;
        _clock = clock;
    }

    /// <summary>GET /api/entitlements/employees/{employeeId}/package?asOf= — the package on a date (default: today, tenant time).</summary>
    [HttpGet("employees/{employeeId:int}/package")]
    [HasPermission("entitlements.read")]
    public async Task<ActionResult<EmployeePackageView>> Get(int employeeId, [FromQuery] DateOnly? asOf, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return NotFound(new { error = "employee_not_found", message = "That employee doesn't exist or isn't in your companies." });
        var date = asOf ?? await _clock.TodayAsync(tid, ct);
        var package = await _resolver.ResolveAsync(tid, employeeId, date, ct);
        var context = await PackageViewContext.LoadAsync(_db, tid, package, ct);
        return Ok(EmployeePackageView.From(package, context));
    }

    /// <summary>
    /// POST /api/entitlements/employees/{employeeId}/package/freeze — fix the contract-year package for an active term now.
    /// Naturally idempotent (an Idempotency-Key header is accepted and not needed): a term that already has its package
    /// answers <c>alreadyFrozen</c> and writes nothing.
    /// </summary>
    [HttpPost("employees/{employeeId:int}/package/freeze")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> Freeze(int employeeId, [FromBody] PackageContractRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return NotFound(new { error = "employee_not_found", message = "That employee doesn't exist or isn't in your companies." });
        if (!await OwnContractAsync(tid, employee.PublicId, req.ContractId, ct))
            return NotFound(new { error = "contract_not_found", message = "That contract isn't one of this employee's." });
        try
        {
            var result = await FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tid, employee.PublicId, async () =>
            {
                var outcome = await _writer.FreezeTermAsync(tid, req.ContractId, ct);
                if (outcome.Frozen) Audit(tid, employee.PublicId, req.ContractId, "PackageFrozen", new { rows = outcome.RowsWritten });
                await _db.SaveChangesAsync(ct);
                return outcome;
            }, ct);
            return Ok(new { frozen = result.Frozen, alreadyFrozen = result.AlreadyFrozen, rowsWritten = result.RowsWritten });
        }
        catch (EntitlementWriteRefusedException ex) { return Refused(ex.Code, ex.Message); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("ENTITLEMENT_") == true)
        {
            return Refused(EntitlementWriteRefusedException.ContractNotInForce, "The package doesn't fit this contract term. Check the term's dates and company.");
        }
    }

    /// <summary>POST /api/entitlements/employees/{employeeId}/package/confirm — HR confirms a migrated package matches the signed contract.</summary>
    [HttpPost("employees/{employeeId:int}/package/confirm")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> Confirm(int employeeId, [FromBody] PackageContractRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return NotFound(new { error = "employee_not_found", message = "That employee doesn't exist or isn't in your companies." });
        if (!await OwnContractAsync(tid, employee.PublicId, req.ContractId, ct))
            return NotFound(new { error = "contract_not_found", message = "That contract isn't one of this employee's." });
        var writer = new EntitlementWriter(_db, _clock, _resolver);
        var confirmed = await FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tid, employee.PublicId, async () =>
        {
            var count = await writer.ConfirmMigratedAsync(tid, employee.PublicId, req.ContractId, ct);
            if (count > 0) Audit(tid, employee.PublicId, req.ContractId, "PackageConfirmed", new { rows = count });
            await _db.SaveChangesAsync(ct);
            return count;
        }, ct);
        return Ok(new { confirmed });
    }

    /// <summary>
    /// POST /api/entitlements/package/freeze-bulk — freeze the package of every employee on a running term in one company
    /// (Migrated, to confirm). Background job; one live job per company.
    /// </summary>
    [HttpPost("package/freeze-bulk")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> FreezeBulk([FromBody] PackageBulkFreezeRequest req, [FromServices] BackgroundJobStore jobs, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (!this.GetRequestScope().CanAccessCompany(req.CompanyId)) return Forbid();
        if (!await _db.Companies.AnyAsync(x => x.TenantId == tid && x.Id == req.CompanyId && !x.IsDeleted, ct))
            return NotFound(new { error = "company_not_found", message = "That company doesn't exist." });
        var result = await jobs.EnqueueAsync(tid, PackageFreezeJobHandler.JobType, PackageFreezeJobHandler.IdempotencyKey(req.CompanyId),
            new PackageFreezeJobPayload(req.CompanyId), this.GetUserId(), ct);
        if (!result.Created) Response.Headers["X-Job-Deduplicated"] = "true";
        return Accepted($"/api/jobs/{result.Job.Id}",
            new BackgroundJobEnqueueResponse(result.Job.Id, result.Job.Status, !result.Created, $"/api/jobs/{result.Job.Id}"));
    }

    private ObjectResult Refused(string code, string message) =>
        Conflict(new { error = code, message, reason = ReleaseABlockReasons.All.TryGetValue(code, out var r) ? r : null });

    /// <summary>The employee through the caller's company filter: out of scope reads as not found.</summary>
    private async Task<Employee?> VisibleEmployeeAsync(Guid tid, int employeeId, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == employeeId && !x.IsDeleted, ct);
        return employee is not null && this.GetRequestScope().CanAccessCompany(employee.CompanyId) ? employee : null;
    }

    private Task<bool> OwnContractAsync(Guid tid, Guid employeePublicId, Guid contractId, CancellationToken ct) =>
        _db.EmployeeContracts.AsNoTracking().AnyAsync(x => x.TenantId == tid && x.Id == contractId && x.EmployeeId == employeePublicId && !x.IsDeleted, ct);

    private void Audit(Guid tid, Guid employeePublicId, Guid contractId, string action, object metadata) =>
        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "ContractPackage", EntityId = contractId.ToString(), EmployeeId = employeePublicId,
            Action = action, PerformedByUserId = this.GetUserId(),
            PerformedByName = User.Identity?.Name ?? string.Empty, MetadataJson = JsonSerializer.Serialize(metadata),
        });
}

public sealed record PackageContractRequest(Guid ContractId);
public sealed record PackageBulkFreezeRequest(Guid CompanyId);

/// <summary>HR view: the package plus what each line is based on (the grade cell, the frozen row, the term, the salary row).</summary>
public sealed record EmployeePackageView(
    EmployeePackage Package,
    string Currency,
    PackageGradeDto? Grade,
    PackageCompanyDto? Company,
    PackageContractDto? Contract,
    PackageSalaryDto? Salary,
    IReadOnlyList<PackageCellDto> Cells,
    IReadOnlyList<PackageFrozenRowDto> FrozenRows,
    IReadOnlyList<BlockReason> BlockReasons,
    bool CanFreeze,
    int UnverifiedRows)
{
    public static EmployeePackageView From(EmployeePackage package, PackageViewContext c)
    {
        var frozenCount = package.Lines.Count(l => l.Source == PackageLineSources.ContractFrozen);
        return new EmployeePackageView(
            package, c.Currency,
            c.Grade is null ? null : new PackageGradeDto(c.Grade.Id, c.Grade.Code, c.Grade.Name, c.Grade.NameAr, c.Grade.Level),
            c.Company is null ? null : new PackageCompanyDto(c.Company.Id, c.Company.LegalNameEn, c.Company.LegalNameAr),
            c.Contract is null ? null : new PackageContractDto(c.Contract.Id, c.Contract.ContractNumber, c.Contract.Status, c.Contract.StartDate, c.Contract.EndDate),
            c.Salary is null ? null : new PackageSalaryDto(c.Salary.EffectiveDate, c.Salary.BasicSalary, c.Salary.Currency),
            c.Cells.Values.Select(x => new PackageCellDto(x.Id, x.PayComponentCode, x.CompanyId != null, x.Eligible, x.ValueType, x.Amount, x.Rate,
                x.MaxOutstandingAmount, x.CoverageTier, x.Quantity, x.DependantScope, x.MaxDependants, x.LimitPeriod, x.MinServiceMonths,
                x.AfterProbation, x.NationalityScope, x.NationalityBasis, x.Note, x.EffectiveFrom, x.EffectiveTo)).ToList(),
            c.FrozenRows.Values.Select(x => new PackageFrozenRowDto(x.Id, x.PayComponentCode, x.Source, x.VerificationState,
                x.ResolvedAmount, x.EffectiveFrom, x.EffectiveTo)).ToList(),
            package.BlockCodes.Where(ReleaseABlockReasons.All.ContainsKey).Select(code => ReleaseABlockReasons.All[code]).ToList(),
            c.Contract is { Status: "Active" } && frozenCount == 0 && package.GradeId is not null,
            c.FrozenRows.Values.Count(x => x.VerificationState == EntitlementVerificationStates.Unverified));
    }
}

public sealed record PackageGradeDto(Guid Id, string Code, string Name, string? NameAr, int Level);
public sealed record PackageCompanyDto(Guid Id, string NameEn, string NameAr);
public sealed record PackageContractDto(Guid Id, string Number, string Status, DateOnly StartDate, DateOnly? EndDate);
public sealed record PackageSalaryDto(DateOnly EffectiveDate, decimal BasicSalary, string Currency);
public sealed record PackageCellDto(Guid Id, string ComponentCode, bool IsCompanyOverride, bool Eligible, string ValueType, decimal? Amount,
    decimal? Rate, decimal? MaxOutstandingAmount, string? CoverageTier, short? Quantity, string DependantScope, short? MaxDependants,
    string? LimitPeriod, short? MinServiceMonths, bool AfterProbation, string NationalityScope, string? NationalityBasis, string? Note,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo);
public sealed record PackageFrozenRowDto(Guid Id, string ComponentCode, string Source, string VerificationState, decimal? ResolvedAmount,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo);
