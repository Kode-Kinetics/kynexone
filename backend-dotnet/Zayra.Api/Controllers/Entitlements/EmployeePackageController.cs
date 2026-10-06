using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Entitlements;

/// <summary>
/// The employee's package for HR (Release A slice R2): pay in the Qiwa contract, contract benefits fixed for the term,
/// facilities by current policy — each line with what it is based on — plus the proposed packages of the bulk run and
/// their confirm / reject. Closed unless the tenant has release_a on (FeatureFlagGuardFilter covers /api/entitlements).
/// Company scope: the employee (or the proposal's company) must be visible to the caller.
/// </summary>
[Authorize]
[ApiController]
[Route("api/entitlements")]
public sealed class EmployeePackageController : ControllerBase
{
    /// <summary>Audit entity of a proposal decision. EntityId = "{batchId:N}:{contractId:N}"; the batch is the job id.
    /// employee_entitlements has no batch column (and R2 adds no migration), so the batch and the document are recorded
    /// here, with the ids of the rows written.</summary>
    public const string ProposalAuditEntity = "ContractPackageProposal";

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
        if (await VisibleEmployeeAsync(tid, employeeId, ct) is null) return EmployeeNotFound();
        var date = asOf ?? await _clock.TodayAsync(tid, ct);
        var resolved = await _resolver.ResolveDetailedAsync(tid, employeeId, date, ct);
        var context = await PackageViewContext.LoadAsync(_db, tid, resolved.Package, ct);
        var proposal = context.Contract is null ? null : await OpenProposalAsync(tid, context.Contract.Id, ct);
        return Ok(EmployeePackageView.From(resolved, context, proposal, this.GetUserId()));
    }

    /// <summary>
    /// POST /api/entitlements/employees/{employeeId}/package/freeze — fix the contract-year package for an active term now.
    /// Naturally idempotent (an Idempotency-Key header is accepted and not needed). Components that could not be fixed are
    /// returned with their reason; nothing that would be refused is ever written.
    /// </summary>
    [HttpPost("employees/{employeeId:int}/package/freeze")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> Freeze(int employeeId, [FromBody] PackageContractRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return EmployeeNotFound();
        if (!await OwnContractAsync(tid, employee.PublicId, req.ContractId, ct))
            return Refused(PackageReasons.ContractNotFound, "That contract isn't one of this employee's.", StatusCodes.Status404NotFound);
        var writer = _writer as EntitlementWriter;
        return await WriteAsync(async () =>
        {
            var result = await FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tid, employee.PublicId, async () =>
            {
                var outcome = await _writer.FreezeTermAsync(tid, req.ContractId, ct);
                if (outcome.Frozen) Audit(tid, employee.PublicId, "ContractPackage", req.ContractId.ToString(), "PackageFrozen",
                    new { rows = outcome.RowsWritten, skipped = writer?.LastSkips });
                await _db.SaveChangesAsync(ct);
                return outcome;
            }, ct);
            return Ok(new { frozen = result.Frozen, alreadyFrozen = result.AlreadyFrozen, rowsWritten = result.RowsWritten,
                skipped = Skips(writer?.LastSkips ?? []) });
        });
    }

    /// <summary>
    /// POST /api/entitlements/package/freeze-bulk — propose the package of every employee on a running term in one company.
    /// A background job that writes nothing: HR confirms or rejects each proposal (<see cref="ConfirmProposal"/>).
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

    /// <summary>GET /api/entitlements/package/proposals/{batchId} — the proposals of one run, with their decision.</summary>
    [HttpGet("package/proposals/{batchId:guid}")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> Proposals(Guid batchId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var job = await BatchAsync(tid, batchId, ct);
        if (job is null) return NotFound(new { error = "batch_not_found", message = "That proposal run doesn't exist." });
        var scope = this.GetRequestScope();
        var items = await _db.BackgroundJobItems.AsNoTracking().Where(x => x.TenantId == tid && x.JobId == batchId).ToListAsync(ct);
        var decisions = await DecisionsAsync(tid, batchId, ct);
        var proposals = items.Select(i => PackageFreezeJobHandler.ReadProposal(i.ResultJson)).OfType<FreezeProposal>()
            .Where(p => scope.CanAccessCompany(p.CompanyId)).ToList();
        var userId = this.GetUserId();
        return Ok(new
        {
            batchId, requestedBy = job.CreatedByUserId, canDecide = job.CreatedByUserId != userId,
            proposals = proposals.Select(p => new { proposal = p, decision = decisions.GetValueOrDefault(p.ContractId) }),
        });
    }

    /// <summary>
    /// POST /api/entitlements/package/proposals/{batchId}/confirm — write one proposed package after checking it against the
    /// signed contract. Needs the contract document on the employee's file, and a different person from whoever asked for
    /// the run (four eyes). Rows are written Migrated and Verified; the batch, document and row ids go to the audit log.
    /// </summary>
    [HttpPost("package/proposals/{batchId:guid}/confirm")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> ConfirmProposal(Guid batchId, [FromBody] ConfirmProposalRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var (proposal, employee, failure) = await OpenProposalForDecisionAsync(tid, batchId, req.ContractId, ct);
        if (failure is not null) return failure;
        if (req.DocumentId is not Guid documentId
            || !await _db.EmployeeDocuments.AsNoTracking().AnyAsync(x => x.TenantId == tid && x.Id == documentId && x.EmployeeId == employee!.Id && !x.IsDeleted, ct))
            return Refused(PackageReasons.ProposalDocumentRequired, "Choose the employee's signed contract from their documents.", StatusCodes.Status400BadRequest);
        var writer = new EntitlementWriter(_db, _clock, _resolver);
        return await WriteAsync(async () =>
        {
            var rows = await FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tid, employee!.PublicId, async () =>
            {
                var written = await writer.WriteConfirmedProposalAsync(tid, proposal!, ct);
                Audit(tid, employee.PublicId, ProposalAuditEntity, ProposalKey(batchId, req.ContractId), "Confirmed", new
                {
                    batchId, contractId = req.ContractId, documentId, rowIds = written.Select(r => r.Id), skipped = writer.LastSkips,
                });
                await _db.SaveChangesAsync(ct);
                return written;
            }, ct);
            return Ok(new { confirmed = rows.Count, skipped = Skips(writer.LastSkips) });
        });
    }

    /// <summary>POST /api/entitlements/package/proposals/{batchId}/reject — the proposal does not match the signed contract.</summary>
    [HttpPost("package/proposals/{batchId:guid}/reject")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> RejectProposal(Guid batchId, [FromBody] RejectProposalRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var (_, employee, failure) = await OpenProposalForDecisionAsync(tid, batchId, req.ContractId, ct);
        if (failure is not null) return failure;
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
            return BadRequest(new { error = "reason_required", message = "Say why the proposal does not match the contract (up to 500 characters)." });
        Audit(tid, employee!.PublicId, ProposalAuditEntity, ProposalKey(batchId, req.ContractId), "Rejected",
            new { batchId, contractId = req.ContractId, reason = req.Reason.Trim() });
        await _db.SaveChangesAsync(ct);
        return Ok(new { rejected = true });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task<(FreezeProposal? Proposal, Employee? Employee, IActionResult? Failure)> OpenProposalForDecisionAsync(
        Guid tid, Guid batchId, Guid contractId, CancellationToken ct)
    {
        var job = await BatchAsync(tid, batchId, ct);
        if (job is null) return (null, null, NotFound(new { error = "batch_not_found", message = "That proposal run doesn't exist." }));
        if (job.CreatedByUserId is Guid requester && requester == this.GetUserId())
            return (null, null, Refused(PackageReasons.ProposalSameUser, "You asked for these proposals, so another HR user must decide them."));
        var item = await _db.BackgroundJobItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.JobId == batchId && x.ItemKey == PackageFreezeJobHandler.ItemKey(contractId), ct);
        if (PackageFreezeJobHandler.ReadProposal(item?.ResultJson) is not { } proposal)
            return (null, null, Refused(PackageReasons.ContractNotFound, "There is no proposal for that contract in this run.", StatusCodes.Status404NotFound));
        if (!this.GetRequestScope().CanAccessCompany(proposal.CompanyId)) return (null, null, Forbid());
        if ((await DecisionsAsync(tid, batchId, ct)).ContainsKey(contractId))
            return (null, null, Refused(PackageReasons.ProposalClosed, "This proposal was already decided."));
        var employee = await VisibleEmployeeAsync(tid, proposal.EmployeeId, ct);
        if (employee is null) return (null, null, EmployeeNotFound());
        return (proposal, employee, null);
    }

    /// <summary>The open (undecided) proposal for one term, from the newest run that proposed something for it.</summary>
    private async Task<OpenProposal?> OpenProposalAsync(Guid tid, Guid contractId, CancellationToken ct)
    {
        var key = PackageFreezeJobHandler.ItemKey(contractId);
        var items = await (from item in _db.BackgroundJobItems.AsNoTracking()
                           join job in _db.BackgroundJobs.AsNoTracking() on item.JobId equals job.Id
                           where item.TenantId == tid && job.TenantId == tid && job.JobType == PackageFreezeJobHandler.JobType && item.ItemKey == key
                           orderby item.CreatedAtUtc descending
                           select new { item.JobId, item.ResultJson, job.CreatedByUserId }).Take(5).ToListAsync(ct);
        foreach (var item in items)
        {
            if (PackageFreezeJobHandler.ReadProposal(item.ResultJson) is not { } proposal) continue;
            if ((await DecisionsAsync(tid, item.JobId, ct)).ContainsKey(contractId)) continue;
            return new OpenProposal(item.JobId, item.CreatedByUserId, proposal);
        }
        return null;
    }

    private Task<BackgroundJob?> BatchAsync(Guid tid, Guid batchId, CancellationToken ct) =>
        _db.BackgroundJobs.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == batchId && x.JobType == PackageFreezeJobHandler.JobType, ct);

    private async Task<Dictionary<Guid, string>> DecisionsAsync(Guid tid, Guid batchId, CancellationToken ct)
    {
        var prefix = $"{batchId:N}:";
        var logs = await _db.ComplianceAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == tid && x.EntityType == ProposalAuditEntity && x.EntityId.StartsWith(prefix))
            .Select(x => new { x.EntityId, x.Action }).ToListAsync(ct);
        return logs.GroupBy(x => Guid.ParseExact(x.EntityId[prefix.Length..], "N")).ToDictionary(g => g.Key, g => g.First().Action);
    }

    private static string ProposalKey(Guid batchId, Guid contractId) => $"{batchId:N}:{contractId:N}";

    private static IEnumerable<object> Skips(IEnumerable<FreezeSkip> skips) =>
        skips.Select(s => new { s.ComponentCode, s.Code, reason = PackageReasons.Describe(s.Code) });

    /// <summary>Runs a write; a refusal or a database rule becomes 409 with a reason code, never a 500.</summary>
    private async Task<IActionResult> WriteAsync(Func<Task<IActionResult>> write)
    {
        try { return await write(); }
        catch (EntitlementWriteRefusedException ex) { return Refused(ex.Code, ex.Message); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation })
        {
            return Refused(PackageReasons.TermOverlap, "Another contract term already has this benefit for these dates.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            return Refused(PackageReasons.ContractNotInForce, "The package doesn't fit this contract term. Check the term's dates and company.");
        }
    }

    private ObjectResult Refused(string code, string message, int status = StatusCodes.Status409Conflict) =>
        StatusCode(status, new { error = code, message, reason = PackageReasons.Describe(code) });

    private NotFoundObjectResult EmployeeNotFound() =>
        NotFound(new { error = "employee_not_found", message = "That employee doesn't exist or isn't in your companies." });

    /// <summary>The employee through the caller's company filter: out of scope reads as not found.</summary>
    private async Task<Employee?> VisibleEmployeeAsync(Guid tid, int employeeId, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == employeeId && !x.IsDeleted, ct);
        return employee is not null && this.GetRequestScope().CanAccessCompany(employee.CompanyId) ? employee : null;
    }

    private Task<bool> OwnContractAsync(Guid tid, Guid employeePublicId, Guid contractId, CancellationToken ct) =>
        _db.EmployeeContracts.AsNoTracking().AnyAsync(x => x.TenantId == tid && x.Id == contractId && x.EmployeeId == employeePublicId && !x.IsDeleted, ct);

    private void Audit(Guid tid, Guid employeePublicId, string entityType, string entityId, string action, object metadata) =>
        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = entityType, EntityId = entityId, EmployeeId = employeePublicId,
            Action = action, PerformedByUserId = this.GetUserId(),
            PerformedByName = User.Identity?.Name ?? string.Empty, MetadataJson = JsonSerializer.Serialize(metadata),
        });
}

public sealed record PackageContractRequest(Guid ContractId);
public sealed record PackageBulkFreezeRequest(Guid CompanyId);
public sealed record ConfirmProposalRequest(Guid ContractId, Guid? DocumentId);
public sealed record RejectProposalRequest(Guid ContractId, string? Reason);
public sealed record OpenProposal(Guid BatchId, Guid? RequestedBy, FreezeProposal Proposal);

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
    IReadOnlyDictionary<string, BlockReason> Reasons,
    IReadOnlyDictionary<string, string> Criteria,
    bool CanFreeze,
    int DependantsOnFile,
    PackageProposalDto? Proposal,
    IReadOnlyDictionary<string, ComponentLabel> Labels)
{
    public static EmployeePackageView From(ResolvedPackage resolved, PackageViewContext c, OpenProposal? proposal, Guid? viewer)
    {
        var package = resolved.Package;
        var frozenCount = package.Lines.Count(l => l.Source == PackageLineSources.ContractFrozen);
        var codes = package.Lines.Select(l => l.ReasonCode).Concat(package.BlockCodes).OfType<string>().Distinct();
        return new EmployeePackageView(
            package, c.Currency,
            c.Grade is null ? null : new PackageGradeDto(c.Grade.Id, c.Grade.Code, c.Grade.Name, c.Grade.NameAr, c.Grade.Level),
            c.Company is null ? null : new PackageCompanyDto(c.Company.Id, c.Company.LegalNameEn, c.Company.LegalNameAr),
            c.Contract is null ? null : new PackageContractDto(c.Contract.Id, c.Contract.ContractNumber, c.Contract.Status, c.Contract.StartDate,
                c.Contract.EndDate, c.Contract.WorkerNationalityClass),
            c.Salary is null ? null : new PackageSalaryDto(c.Salary.EffectiveDate, c.Salary.BasicSalary, c.Salary.Currency),
            c.Cells.Values.Select(x => new PackageCellDto(x.Id, x.PayComponentCode, x.CompanyId != null, x.Eligible, x.ValueType, x.Amount, x.Rate,
                x.MaxOutstandingAmount, x.CoverageTier, x.Quantity, x.DependantScope, x.MaxDependants, x.LimitPeriod, x.MinServiceMonths,
                x.AfterProbation, x.NationalityScope, x.NationalityBasis, x.Note, x.EffectiveFrom, x.EffectiveTo)).ToList(),
            c.FrozenRows.Values.Select(x => new PackageFrozenRowDto(x.Id, x.PayComponentCode, x.Source, x.VerificationState,
                x.ResolvedAmount, x.EffectiveFrom, x.EffectiveTo)).ToList(),
            package.BlockCodes.Select(PackageReasons.Describe).OfType<BlockReason>().ToList(),
            codes.Select(code => (code, reason: PackageReasons.Describe(code))).Where(x => x.reason is not null)
                .ToDictionary(x => x.code, x => x.reason!),
            resolved.Reasons.Where(r => r.Value.Criterion is not null).ToDictionary(r => r.Key, r => r.Value.Criterion!),
            c.Contract is { Status: "Active" } && frozenCount == 0 && package.GradeId is not null && proposal is null,
            c.Dependants.Count,
            proposal is null ? null : new PackageProposalDto(proposal.BatchId, proposal.RequestedBy is Guid r && r == viewer,
                proposal.Proposal.From, proposal.Proposal.To, proposal.Proposal.Rows, proposal.Proposal.Skips),
            c.Labels);
    }
}

public sealed record PackageGradeDto(Guid Id, string Code, string Name, string? NameAr, int Level);
public sealed record PackageCompanyDto(Guid Id, string NameEn, string NameAr);
public sealed record PackageContractDto(Guid Id, string Number, string Status, DateOnly StartDate, DateOnly? EndDate, string? WorkerNationalityClass);
public sealed record PackageSalaryDto(DateOnly EffectiveDate, decimal BasicSalary, string Currency);
public sealed record PackageCellDto(Guid Id, string ComponentCode, bool IsCompanyOverride, bool Eligible, string ValueType, decimal? Amount,
    decimal? Rate, decimal? MaxOutstandingAmount, string? CoverageTier, short? Quantity, string DependantScope, short? MaxDependants,
    string? LimitPeriod, short? MinServiceMonths, bool AfterProbation, string NationalityScope, string? NationalityBasis, string? Note,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo);
public sealed record PackageFrozenRowDto(Guid Id, string ComponentCode, string Source, string VerificationState, decimal? ResolvedAmount,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo);
/// <param name="RequestedByYou">The viewer asked for this run, so they cannot decide it (four eyes).</param>
public sealed record PackageProposalDto(Guid BatchId, bool RequestedByYou, DateOnly From, DateOnly? To, IReadOnlyList<ProposedRow> Rows,
    IReadOnlyList<FreezeSkip> Skips);
