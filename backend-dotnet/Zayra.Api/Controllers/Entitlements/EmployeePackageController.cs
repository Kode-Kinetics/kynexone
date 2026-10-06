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
        FreezePreview? preview = null;
        if (context.Contract is { Status: "Active" } active)
        {
            try { preview = await new EntitlementWriter(_db, _clock, _resolver).PreviewAsync(tid, active.Id, ct); }
            catch (EntitlementWriteRefusedException) { preview = null; }
        }
        return Ok(EmployeePackageView.From(resolved, context, proposal, preview, this.GetUserId()));
    }

    /// <summary>
    /// GET /api/entitlements/contracts/package-status?ids=… — the one next action for each active contract's benefits, for
    /// the contract register: <c>proposeBenefits</c> (a term that started before it was activated: its benefits wait for a
    /// proposal a second person confirms), <c>reviewProposal</c> (a proposal is waiting), or nothing. Contracts the caller
    /// cannot see, or that need no action, are left out.
    /// </summary>
    [HttpGet("contracts/package-status")]
    [HasPermission("entitlements.read")]
    public async Task<IActionResult> ContractsPackageStatus([FromQuery] Guid[] ids, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (ids is not { Length: > 0 } || ids.Length > 200)
            return BadRequest(new { error = "ids_required", message = "Ask for between 1 and 200 contracts." });
        var scope = this.GetRequestScope();
        var contracts = await _db.EmployeeContracts.AsNoTracking()
            .Where(x => x.TenantId == tid && ids.Contains(x.Id) && x.Status == "Active" && !x.IsDeleted)
            .Select(x => new { x.Id, x.EmployeeId, x.CompanyId }).ToListAsync(ct);
        var publicIds = contracts.Select(c => c.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking().Where(x => x.TenantId == tid && publicIds.Contains(x.PublicId) && !x.IsDeleted)
            .Select(x => new { x.Id, x.PublicId, x.CompanyId }).ToListAsync(ct);
        var writer = new EntitlementWriter(_db, _clock, _resolver);
        var result = new List<ContractPackageStatusDto>();
        foreach (var contract in contracts)
        {
            var employee = employees.FirstOrDefault(e => e.PublicId == contract.EmployeeId);
            if (employee is null || !scope.CanAccessCompany(employee.CompanyId)) continue;
            string? next = null;
            if (await OpenProposalAsync(tid, contract.Id, ct) is not null) next = ContractPackageNextActions.ReviewProposal;
            else
            {
                FreezePreview? preview;
                try { preview = await writer.PreviewAsync(tid, contract.Id, ct); }
                catch (EntitlementWriteRefusedException) { preview = null; }
                if (preview is { Running: true, ProposableRows: > 0 }) next = ContractPackageNextActions.ProposeBenefits;
            }
            if (next is not null) result.Add(new ContractPackageStatusDto(contract.Id, employee.Id, next));
        }
        return Ok(result);
    }

    /// <summary>
    /// POST /api/entitlements/employees/{employeeId}/package/freeze — fix the package for a term that has NOT started yet, from
    /// the grade table. A running term (started before today) goes through propose → a second HR user confirms (four eyes),
    /// and a term with an open proposal waits for that decision; both are refused here. Idempotent per benefit (an
    /// Idempotency-Key header is accepted and not needed). What could not be fixed is returned with its reason.
    /// </summary>
    [HttpPost("employees/{employeeId:int}/package/freeze")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> Freeze(int employeeId, [FromBody] PackageContractRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return EmployeeNotFound();
        var contract = await OwnContractAsync(tid, employee.PublicId, req.ContractId, ct);
        if (contract is null)
            return Refused(PackageReasons.ContractNotFound, "That contract isn't one of this employee's.", StatusCodes.Status404NotFound);
        if (contract.StartDate < await _clock.TodayAsync(tid, ct))
            return Refused(PackageReasons.TermRunningNeedsProposal,
                "This term has already started. Propose its package, and another HR user confirms it against the signed contract.");
        if (await OpenProposalAsync(tid, contract.Id, ct) is not null)
            return Refused(PackageReasons.ProposalOpen, "A proposed package for this term is waiting for a decision.");
        var writer = new EntitlementWriter(_db, _clock, _resolver);
        return await WriteAsync(async () =>
        {
            var result = await FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tid, employee.PublicId, async () =>
            {
                var outcome = await writer.FreezeTermAsync(tid, req.ContractId, ct);
                if (outcome.Frozen) Audit(tid, employee.PublicId, "ContractPackage", req.ContractId.ToString(), "PackageFrozen",
                    new { rows = outcome.RowsWritten, skipped = writer.LastSkips });
                await _db.SaveChangesAsync(ct);
                return outcome;
            }, ct);
            return Ok(new { frozen = result.Frozen, alreadyFrozen = result.AlreadyFrozen, rowsWritten = result.RowsWritten,
                skipped = Skips(writer.LastSkips) });
        });
    }

    /// <summary>
    /// POST /api/entitlements/employees/{employeeId}/package/propose — propose the package of one running term (the four-eyes
    /// path). A background job that writes nothing; another HR user confirms or rejects it on the package panel.
    /// </summary>
    [HttpPost("employees/{employeeId:int}/package/propose")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> Propose(int employeeId, [FromBody] PackageContractRequest req, [FromServices] BackgroundJobStore jobs, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return EmployeeNotFound();
        var contract = await OwnContractAsync(tid, employee.PublicId, req.ContractId, ct);
        if (contract?.CompanyId is not Guid companyId)
            return Refused(PackageReasons.ContractNotFound, "That contract isn't one of this employee's.", StatusCodes.Status404NotFound);
        if (await OpenProposalAsync(tid, contract.Id, ct) is not null)
            return Refused(PackageReasons.ProposalOpen, "A proposed package for this term is already waiting for a decision.");
        var result = await jobs.EnqueueAsync(tid, PackageFreezeJobHandler.JobType, PackageFreezeJobHandler.IdempotencyKey(companyId, contract.Id),
            new PackageFreezeJobPayload(companyId, contract.Id), this.GetUserId(), ct);
        if (!result.Created) Response.Headers["X-Job-Deduplicated"] = "true";
        return Accepted($"/api/jobs/{result.Job.Id}",
            new BackgroundJobEnqueueResponse(result.Job.Id, result.Job.Status, !result.Created, $"/api/jobs/{result.Job.Id}"));
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
            batchId, requestedBy = job.CreatedByUserId, canDecide = job.CreatedByUserId is Guid r && r != userId,
            proposals = proposals.Select(p => new { proposal = p, decision = decisions.GetValueOrDefault(p.ContractId) }),
        });
    }

    /// <summary>
    /// POST /api/entitlements/package/proposals/{batchId}/confirm — write one proposed package after checking it against the
    /// signed contract: the contract's own file or a contract-type document of this employee. The confirmer must be a known,
    /// different person from whoever asked for the run (four eyes). Decided once: re-checked under the per-employee lock.
    /// Rows are written Migrated and Verified; the batch, document and row ids go to the audit log.
    /// </summary>
    [HttpPost("package/proposals/{batchId:guid}/confirm")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> ConfirmProposal(Guid batchId, [FromBody] ConfirmProposalRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var (proposal, employee, failure) = await OpenProposalForDecisionAsync(tid, batchId, req.ContractId, ct);
        if (failure is not null) return failure;
        if (req.DocumentId is not Guid documentId || !await IsSignedContractAsync(tid, employee!.Id, req.ContractId, documentId, ct))
            return Refused(PackageReasons.ProposalDocumentRequired, "Choose the employee's signed contract from their documents.", StatusCodes.Status400BadRequest);
        var writer = new EntitlementWriter(_db, _clock, _resolver);
        return await WriteAsync(async () =>
        {
            var rows = await FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tid, employee!.PublicId, async () =>
            {
                await EnsureUndecidedAsync(tid, batchId, req.ContractId, ct);
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
        return await WriteAsync(async () =>
        {
            await FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tid, employee!.PublicId, async () =>
            {
                await EnsureUndecidedAsync(tid, batchId, req.ContractId, ct);
                Audit(tid, employee.PublicId, ProposalAuditEntity, ProposalKey(batchId, req.ContractId), "Rejected",
                    new { batchId, contractId = req.ContractId, reason = req.Reason.Trim() });
                await _db.SaveChangesAsync(ct);
                return 0;
            }, ct);
            return Ok(new { rejected = true });
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task<(FreezeProposal? Proposal, Employee? Employee, IActionResult? Failure)> OpenProposalForDecisionAsync(
        Guid tid, Guid batchId, Guid contractId, CancellationToken ct)
    {
        var job = await BatchAsync(tid, batchId, ct);
        if (job is null) return (null, null, NotFound(new { error = "batch_not_found", message = "That proposal run doesn't exist." }));
        // Four eyes needs two known people: a run that does not record who asked for it can be rejected and proposed again.
        if (job.CreatedByUserId is not Guid requester)
            return (null, null, Refused(PackageReasons.ProposalRequesterUnknown, "This proposal does not record who asked for it, so it cannot be confirmed."));
        if (requester == this.GetUserId())
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

    /// <summary>Inside the per-employee lock: a proposal is decided once, however many people press the button at once.</summary>
    private async Task EnsureUndecidedAsync(Guid tid, Guid batchId, Guid contractId, CancellationToken ct)
    {
        if ((await DecisionsAsync(tid, batchId, ct)).ContainsKey(contractId))
            throw new EntitlementWriteRefusedException(PackageReasons.ProposalClosed, "This proposal was already decided.");
    }

    /// <summary>The signed contract: the term's own file, or a contract-type document on this employee's file.</summary>
    private async Task<bool> IsSignedContractAsync(Guid tid, int employeeId, Guid contractId, Guid documentId, CancellationToken ct)
    {
        var document = await _db.EmployeeDocuments.AsNoTracking()
            .Where(x => x.TenantId == tid && x.Id == documentId && x.EmployeeId == employeeId && !x.IsDeleted)
            .Select(x => new EmployeeDocumentView(x.DocumentType, x.StorageUrl)).FirstOrDefaultAsync(ct);
        if (document is null) return false;
        var fileUrl = await _db.EmployeeContracts.AsNoTracking().Where(x => x.TenantId == tid && x.Id == contractId)
            .Select(x => x.FileUrl).FirstOrDefaultAsync(ct);
        return SignedContractDocuments.Matches(document, fileUrl);
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

    /// <summary>The decision per term of one run. The FIRST decision recorded stands (the lock makes a second one impossible;
    /// the order is still explicit rather than whatever the database returns first).</summary>
    private async Task<Dictionary<Guid, string>> DecisionsAsync(Guid tid, Guid batchId, CancellationToken ct)
    {
        var prefix = $"{batchId:N}:";
        var logs = await _db.ComplianceAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == tid && x.EntityType == ProposalAuditEntity && x.EntityId.StartsWith(prefix))
            .Select(x => new { x.EntityId, x.Action, x.CreatedAtUtc, x.Id }).ToListAsync(ct);
        return logs.GroupBy(x => Guid.ParseExact(x.EntityId[prefix.Length..], "N"))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).First().Action);
    }

    private static string ProposalKey(Guid batchId, Guid contractId) => $"{batchId:N}:{contractId:N}";

    private static IEnumerable<object> Skips(IEnumerable<FreezeSkip> skips) =>
        skips.Select(s => new { s.ComponentCode, s.Code, reason = PackageReasons.Describe(s.Code) });

    /// <summary>Runs a write; a refusal or a database rule becomes 409 with a specific reason code, never a 500.</summary>
    private async Task<IActionResult> WriteAsync(Func<Task<IActionResult>> write)
    {
        try { return await write(); }
        catch (EntitlementLifecycleBlockedException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { error = ex.Code, message = ex.Message, possibleFrom = ex.PossibleFrom,
                components = ex.Components, reason = PackageReasons.Describe(ex.Code) });
        }
        catch (EntitlementWriteRefusedException ex) { return Refused(ex.Code, ex.Message); }
        catch (DbUpdateException ex) when (PackageReasons.FromDatabase(ex) is { } code)
        {
            return Refused(code, "The package doesn't fit this contract term.");
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

    private Task<EmployeeContract?> OwnContractAsync(Guid tid, Guid employeePublicId, Guid contractId, CancellationToken ct) =>
        _db.EmployeeContracts.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == contractId && x.EmployeeId == employeePublicId && !x.IsDeleted, ct);

    private void Audit(Guid tid, Guid employeePublicId, string entityType, string entityId, string action, object metadata) =>
        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = entityType, EntityId = entityId, EmployeeId = employeePublicId,
            Action = action, PerformedByUserId = this.GetUserId(),
            PerformedByName = User.Identity?.Name ?? string.Empty, MetadataJson = JsonSerializer.Serialize(metadata),
        });
}

public sealed record PackageContractRequest(Guid ContractId);

/// <summary>Value set of <see cref="ContractPackageStatusDto.NextAction"/>.</summary>
public static class ContractPackageNextActions
{
    public const string ProposeBenefits = "proposeBenefits";
    public const string ReviewProposal = "reviewProposal";
}

/// <param name="EmployeeId">The int employees.id, to open the employee's package panel.</param>
public sealed record ContractPackageStatusDto(Guid ContractId, int EmployeeId, string NextAction);
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
    bool CanPropose,
    PackageFreezeStatusDto FreezeStatus,
    int DependantsOnFile,
    PackageProposalDto? Proposal,
    IReadOnlyDictionary<string, ComponentLabel> Labels)
{
    public static EmployeePackageView From(ResolvedPackage resolved, PackageViewContext c, OpenProposal? proposal, FreezePreview? preview, Guid? viewer)
    {
        var package = resolved.Package;
        // "N of M benefits fixed": M = the contract benefits this employee is given (eligible and offered), N = those frozen.
        // Counted: given now, waiting on a date (service months, probation), or waiting for the term's nationality class.
        // Not counted: not in the grade, not offered, or excluded by nationality.
        var contractLines = package.Lines.Where(l => l.Class == PayEntitlementClasses.Contractual && l.Offered
            && (l.Eligible || l.ReasonCode == PackageReasons.NationalityUnconfirmed
                || (l.ReasonCode == PackageReasons.NotEligibleCriteria
                    && resolved.Reasons.GetValueOrDefault(l.ComponentCode)?.Criterion != PackageCriteria.Nationality))).ToList();
        var frozen = contractLines.Where(l => l.Source == PackageLineSources.ContractFrozen).Select(l => l.ComponentCode).ToHashSet();
        var skips = preview?.Skips.ToDictionary(x => x.ComponentCode, x => x.Code) ?? [];
        var notFixed = contractLines.Where(l => !frozen.Contains(l.ComponentCode))
            .Select(l => new PackageNotFixedDto(l.ComponentCode,
                proposal?.Proposal.Rows.Any(r => r.ComponentCode == l.ComponentCode) == true ? PackageReasons.ProposalOpen
                : skips.GetValueOrDefault(l.ComponentCode)
                  ?? preview?.BlockedCode
                  ?? (preview?.Running == true ? PackageReasons.TermRunningNeedsProposal : null)))
            .ToList();
        var freezeStatus = new PackageFreezeStatusDto(frozen.Count, contractLines.Count, notFixed, preview?.BlockedCode, preview?.PossibleFrom);
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
            codes.Concat(notFixed.Select(n => n.ReasonCode).OfType<string>()).Distinct()
                .Select(code => (code, reason: PackageReasons.Describe(code))).Where(x => x.reason is not null)
                .ToDictionary(x => x.code, x => x.reason!),
            resolved.Reasons.Where(r => r.Value.Criterion is not null).ToDictionary(r => r.Key, r => r.Value.Criterion!),
            // Offered only when it can succeed now: a "Fix" that would just repeat a skip is never shown.
            proposal is null && preview is { Running: false, BlockedCode: null, FreezableRows: > 0 },
            proposal is null && preview is { Running: true, ProposableRows: > 0 },
            freezeStatus,
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
/// <summary>"N of M benefits fixed" and why each of the rest is not.</summary>
/// <param name="ReasonCode">Why it is not fixed; NULL when it can be fixed now (the "Fix" action).</param>
public sealed record PackageNotFixedDto(string ComponentCode, string? ReasonCode);
public sealed record PackageFreezeStatusDto(int Fixed, int Total, IReadOnlyList<PackageNotFixedDto> NotFixed, string? BlockedCode, DateOnly? PossibleFrom);

/// <param name="RequestedByYou">The viewer asked for this run, so they cannot decide it (four eyes).</param>
public sealed record PackageProposalDto(Guid BatchId, bool RequestedByYou, DateOnly From, DateOnly? To, IReadOnlyList<ProposedRow> Rows,
    IReadOnlyList<FreezeSkip> Skips);
