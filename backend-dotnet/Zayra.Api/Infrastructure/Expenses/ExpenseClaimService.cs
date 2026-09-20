using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Expenses;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Expenses;

/// <summary>
/// W2-B — expense claims and reimbursement.
///
/// <list type="bullet">
/// <item><b>Approval</b> goes through the F1 router: <see cref="IApprovalWorkflowService.CreateRequestAsync"/>
/// with <c>WorkflowId = null</c> and <c>EntityName = "ExpenseClaim"</c>. No configured workflow ⇒
/// <see cref="ApprovalRouteNotConfiguredException"/> (422) and nothing is written.</item>
/// <item><b>Payout</b> is exactly ONE <see cref="PayrollAdjustment"/> per claim
/// (<c>SourceType = "ExpenseClaim"</c>, <c>SourceId = claim.Id</c>, unique per tenant), created when a
/// payroll officer binds approved claims to an open run. Payroll pays it as an earning and marks it
/// Processed; the claim then reads as Paid.</item>
/// </list>
/// </summary>
public sealed class ExpenseClaimService : IExpenseClaimService
{
    private const int MaxLinesPerClaim = 50;
    private const decimal MaxLineAmount = 1_000_000m;
    private const long MaxReceiptBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string[]> ReceiptTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = new[] { ".pdf" },
        ["image/jpeg"] = new[] { ".jpg", ".jpeg" },
        ["image/png"] = new[] { ".png" },
        ["image/webp"] = new[] { ".webp" },
        ["image/heic"] = new[] { ".heic" },
        ["image/heif"] = new[] { ".heif" },
    };

    private readonly ZayraDbContext _db;
    private readonly IApprovalWorkflowService _approvals;
    private readonly IDocumentStorage _storage;
    private readonly IAuditService _audit;

    public ExpenseClaimService(ZayraDbContext db, IApprovalWorkflowService approvals, IDocumentStorage storage, IAuditService audit)
    {
        _db = db;
        _approvals = approvals;
        _storage = storage;
        _audit = audit;
    }

    // ══ Categories & policy ═════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<ExpenseCategoryDto>> GetCategoriesAsync(Guid tenantId, bool includeInactive, CancellationToken ct)
    {
        var values = await CategoryValuesQuery(tenantId, includeInactive).AsNoTracking()
            .OrderBy(v => v.SortOrder).ThenBy(v => v.ValueEn).ToListAsync(ct);
        return values.Select(ToCategoryDto).ToList();
    }

    public async Task<ExpenseCategoryDto> UpdateCategoryPolicyAsync(Guid tenantId, string code, ExpenseCategoryPolicyRequest request, CancellationToken ct)
    {
        if (request.MaxAmountPerClaim is < 0 || request.ReceiptRequiredAbove is < 0)
            throw new ExpenseValidationException("invalid_policy", "Limits cannot be negative.");
        var clean = (code ?? string.Empty).Trim();
        var value = await CategoryValuesQuery(tenantId, includeInactive: true)
            .FirstOrDefaultAsync(v => v.Code.ToUpper() == clean.ToUpper(), ct)
            ?? throw new ExpenseNotFoundException($"Expense category '{clean}' was not found.");

        JsonObject extra;
        try { extra = (string.IsNullOrWhiteSpace(value.ExtraJson) ? null : JsonNode.Parse(value.ExtraJson) as JsonObject) ?? new JsonObject(); }
        catch (JsonException) { extra = new JsonObject(); }
        extra["maxAmountPerClaim"] = request.MaxAmountPerClaim is { } max ? JsonValue.Create(Math.Round(max, 2)) : null;
        extra["receiptRequiredAbove"] = request.ReceiptRequiredAbove is { } rr ? JsonValue.Create(Math.Round(rr, 2)) : null;
        value.ExtraJson = extra.ToJsonString();
        value.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToCategoryDto(value);
    }

    private IQueryable<MasterDataValue> CategoryValuesQuery(Guid tenantId, bool includeInactive)
    {
        var typeIds = _db.MasterDataTypes
            .Where(t => t.TenantId == tenantId && !t.IsDeleted && t.Code == ExpenseClaimConstants.CategoryMasterType)
            .Select(t => t.Id);
        var q = _db.MasterDataValues.Where(v => v.TenantId == tenantId && !v.IsDeleted && typeIds.Contains(v.TypeId));
        return includeInactive ? q : q.Where(v => v.IsActive);
    }

    internal static (decimal? MaxAmountPerClaim, decimal? ReceiptRequiredAbove) ParsePolicy(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(extraJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            return (ReadDecimal(doc.RootElement, "maxAmountPerClaim"), ReadDecimal(doc.RootElement, "receiptRequiredAbove"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static decimal? ReadDecimal(JsonElement root, string name)
            => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d) ? d : null;
    }

    private static ExpenseCategoryDto ToCategoryDto(MasterDataValue v)
    {
        var (max, receipt) = ParsePolicy(v.ExtraJson);
        return new ExpenseCategoryDto(v.Id, v.Code, v.ValueEn, v.ValueAr, v.IsActive, max, receipt);
    }

    // ══ Employee self-service ═══════════════════════════════════════════════════════════════════

    public async Task<PagedResult<ExpenseClaimDto>> ListOwnAsync(Guid tenantId, int employeeId, string? status, int page, int pageSize, CancellationToken ct)
    {
        await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: false, ct);
        return await ListAsync(tenantId, new ExpenseClaimQuery { EmployeeId = employeeId, Status = status, Page = page, PageSize = pageSize },
            new[] { employeeId }, ct, reconcile: false);
    }

    public async Task<ExpenseClaimDto?> GetOwnAsync(Guid tenantId, int employeeId, Guid claimId, CancellationToken ct)
    {
        await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: false, ct, new[] { claimId });
        var claim = await LoadClaimAsync(tenantId, claimId, tracked: false, ct);
        return claim is null || claim.EmployeeId != employeeId ? null : await ToDtoAsync(claim, null, ct);
    }

    public async Task<ExpenseClaimDto> CreateDraftAsync(Guid tenantId, int employeeId, SaveExpenseClaimRequest request, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, ct)
            ?? throw new ExpenseNotFoundException("Your user account is not linked to an employee record.");
        if (employee.CompanyId is null)
            throw new ExpenseValidationException("no_legal_entity", "Your employee record is not assigned to a legal entity, so expenses cannot be paid through payroll. Ask HR to assign one.");
        var currency = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == employee.CompanyId && c.IsActive && !c.IsDeleted)
            .Select(c => c.DefaultCurrency).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(currency))
            throw new ExpenseValidationException("no_payroll_currency", "Your legal entity has no active payroll currency. Ask HR to configure it.");

        var claim = new ExpenseClaim
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            ClaimNumber = NewClaimNumber(),
            Currency = currency.Trim().ToUpperInvariant(),
            Status = ExpenseClaimStatuses.Draft,
        };
        await ApplyLinesAsync(claim, request, requireReceipts: false, ct);
        _db.ExpenseClaims.Add(claim);
        await _db.SaveChangesAsync(ct);
        return (await GetOwnAsync(tenantId, employeeId, claim.Id, ct))!;
    }

    public async Task<ExpenseClaimDto> UpdateDraftAsync(Guid tenantId, int employeeId, Guid claimId, SaveExpenseClaimRequest request, CancellationToken ct)
    {
        var claim = await LoadOwnDraftAsync(tenantId, employeeId, claimId, ct);
        await ApplyLinesAsync(claim, request, requireReceipts: false, ct);
        claim.Version++;
        await SaveOrConflictAsync("This claim was changed at the same time. Reload it and try again.", ct);
        return (await GetOwnAsync(tenantId, employeeId, claimId, ct))!;
    }

    public async Task<ExpenseClaimDto> AttachReceiptAsync(Guid tenantId, int employeeId, Guid claimId, Guid lineId, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length <= 0) throw new ExpenseValidationException("receipt_empty", "Choose a receipt file to upload.");
        if (file.Length > MaxReceiptBytes) throw new ExpenseValidationException("receipt_too_large", "Receipts can be at most 10 MB.");
        var contentType = (file.ContentType ?? string.Empty).Split(';')[0].Trim();
        var extension = Path.GetExtension(file.FileName ?? string.Empty);
        if (!ReceiptTypes.TryGetValue(contentType, out var extensions) || !extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ExpenseValidationException("receipt_type", "Receipts must be a PDF, JPEG, PNG, WEBP or HEIC file.");

        var claim = await LoadOwnDraftAsync(tenantId, employeeId, claimId, ct);
        var line = claim.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new ExpenseNotFoundException("That expense line was not found on this claim.");

        // The storage key is generated by IDocumentStorage (tenant-prefixed, random) — never by the client.
        StoredDocument stored;
        try { stored = await _storage.SaveAsync(tenantId, file, ct); }
        catch (InvalidOperationException ex) { throw new ExpenseValidationException("receipt_rejected", ex.Message); }
        line.ReceiptStorageKey = stored.StorageUrl;
        line.ReceiptFileName = Path.GetFileName(file.FileName);
        line.ReceiptContentType = contentType;
        line.ReceiptSizeBytes = file.Length;
        line.ReceiptUploadedAtUtc = DateTime.UtcNow;
        line.UpdatedAtUtc = DateTime.UtcNow;
        claim.Version++;
        await SaveOrConflictAsync("This claim was changed at the same time. Reload it and try again.", ct);
        return (await GetOwnAsync(tenantId, employeeId, claimId, ct))!;
    }

    public async Task<ExpenseClaimDto> SubmitAsync(Guid tenantId, int employeeId, Guid claimId, RequestContext context, CancellationToken ct)
    {
        // Claim → Submitted and the routed ApprovalRequest commit together or not at all: a tenant with
        // no ExpenseClaim workflow gets the router's typed 422 and the claim stays a Draft.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var claim = await LoadOwnDraftAsync(tenantId, employeeId, claimId, ct);
            var violations = await ValidateAsync(tenantId, claim.Lines.Select(l => new LineCandidate(l.LineNumber, l.ExpenseDate, l.CategoryCode, l.Amount, l.Description, l.ReceiptStorageKey is not null)).ToList(),
                requireReceipts: true, ct);
            if (violations.Count > 0) throw new ExpenseValidationException(violations);

            claim.Status = ExpenseClaimStatuses.Submitted;
            claim.SubmittedAtUtc = DateTime.UtcNow;
            claim.SubmittedByUserId = context.UserId;
            claim.Version++;
            await SaveOrConflictAsync("This claim was submitted or changed at the same time. Reload it.", ct);

            var title = $"Expense claim {claim.ClaimNumber} — {claim.TotalAmount:N2} {claim.Currency}";
            var approval = await _approvals.CreateRequestAsync(tenantId,
                new CreateApprovalRequest(null, ExpenseClaimConstants.ApprovalEntityName, claim.Id.ToString(), title,
                    claim.EmployeeId, claim.CompanyId, "Normal"),
                context, ct);
            claim.ApprovalRequestId = approval.Id;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
        await _audit.WriteAsync("expense.claim_submitted", nameof(ExpenseClaim), claimId.ToString(), context, null, ct);
        return (await GetOwnAsync(tenantId, employeeId, claimId, ct))!;
    }

    public async Task<ExpenseClaimDto> CancelDraftAsync(Guid tenantId, int employeeId, Guid claimId, CancellationToken ct)
    {
        var claim = await LoadOwnDraftAsync(tenantId, employeeId, claimId, ct);
        claim.Status = ExpenseClaimStatuses.Cancelled;
        claim.Version++;
        await SaveOrConflictAsync("This claim was changed at the same time. Reload it.", ct);
        return (await GetOwnAsync(tenantId, employeeId, claimId, ct))!;
    }

    // ══ Approvers / HR / finance ════════════════════════════════════════════════════════════════

    public Task<PagedResult<ExpenseClaimDto>> ListAsync(Guid tenantId, ExpenseClaimQuery query, IReadOnlyCollection<int>? allowedEmployeeIds, CancellationToken ct)
        => ListAsync(tenantId, query, allowedEmployeeIds, ct, reconcile: true);

    private async Task<PagedResult<ExpenseClaimDto>> ListAsync(Guid tenantId, ExpenseClaimQuery query, IReadOnlyCollection<int>? allowedEmployeeIds, CancellationToken ct, bool reconcile)
    {
        if (reconcile) await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: true, ct);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var q = _db.ExpenseClaims.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (allowedEmployeeIds is not null) q = q.Where(c => allowedEmployeeIds.Contains(c.EmployeeId));
        if (!string.IsNullOrWhiteSpace(query.Status)) q = q.Where(c => c.Status == query.Status.Trim());
        if (query.EmployeeId.HasValue) q = q.Where(c => c.EmployeeId == query.EmployeeId.Value);
        if (query.CompanyId.HasValue) q = q.Where(c => c.CompanyId == query.CompanyId.Value);
        if (query.PayrollRunId.HasValue) q = q.Where(c => c.PayrollRunId == query.PayrollRunId.Value);
        if (!string.IsNullOrWhiteSpace(query.CategoryCode))
        {
            var cat = query.CategoryCode.Trim().ToUpperInvariant();
            q = q.Where(c => c.Lines.Any(l => l.CategoryCode == cat));
        }
        if (query.From.HasValue)
        {
            var from = DateTime.SpecifyKind(query.From.Value.Date, DateTimeKind.Utc);
            q = q.Where(c => (c.SubmittedAtUtc ?? c.CreatedAtUtc) >= from);
        }
        if (query.To.HasValue)
        {
            var toExclusive = DateTime.SpecifyKind(query.To.Value.Date.AddDays(1), DateTimeKind.Utc);
            q = q.Where(c => (c.SubmittedAtUtc ?? c.CreatedAtUtc) < toExclusive);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToLower();
            q = q.Where(c => c.ClaimNumber.ToLower().Contains(s) || c.EmployeeName.ToLower().Contains(s) || c.Title.ToLower().Contains(s));
        }

        var total = await q.CountAsync(ct);
        var claims = await q.Include(c => c.Lines)
            .OrderByDescending(c => c.SubmittedAtUtc ?? c.CreatedAtUtc)
            .ThenByDescending(c => c.ClaimNumber)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        var items = new List<ExpenseClaimDto>(claims.Count);
        var periods = await RunPeriodsAsync(tenantId, claims, ct);
        foreach (var c in claims) items.Add(ToDto(c, periods, null));
        return new PagedResult<ExpenseClaimDto>(items, total, page, pageSize);
    }

    public async Task<ExpenseClaimDto?> GetAsync(Guid tenantId, Guid claimId, RequestContext? context, CancellationToken ct)
    {
        await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: true, ct, new[] { claimId });
        var claim = await LoadClaimAsync(tenantId, claimId, tracked: false, ct);
        return claim is null ? null : await ToDtoAsync(claim, context, ct);
    }

    public async Task<IReadOnlyList<ExpenseClaimDto>> GetApprovalInboxAsync(Guid tenantId, RequestContext context, CancellationToken ct)
    {
        await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: false, ct);
        // Oversight roles see every pending expense approval (and CanDecide says which they can act on);
        // everyone else sees their own queue — the same split the Approval Center applies.
        var queue = SeesAllApprovals(context) ? null : "mine";
        var pending = await _approvals.GetRequestsAsync(tenantId, "Pending", ExpenseClaimConstants.ApprovalEntityName, queue, 1, 100, context, ct);
        var byClaim = pending.Items
            .Where(a => Guid.TryParse(a.EntityId, out _))
            .ToDictionary(a => Guid.Parse(a.EntityId), a => a);
        if (byClaim.Count == 0) return Array.Empty<ExpenseClaimDto>();
        var ids = byClaim.Keys.ToList();
        var claims = await _db.ExpenseClaims.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.TenantId == tenantId && ids.Contains(c.Id) && c.Status == ExpenseClaimStatuses.Submitted)
            .OrderBy(c => c.SubmittedAtUtc)
            .ToListAsync(ct);
        var periods = await RunPeriodsAsync(tenantId, claims, ct);
        return claims.Select(c =>
        {
            var a = byClaim[c.Id];
            return ToDto(c, periods, new ExpenseApprovalProgressDto(a.Status, a.CurrentStepOrder, a.CurrentApproverName, a.CurrentApproverRole, a.DueAtUtc, a.CanDecide));
        }).ToList();
    }

    public async Task<ExpenseClaimDto> DecideAsync(Guid tenantId, Guid claimId, ExpenseDecisionRequest request, RequestContext context, CancellationToken ct)
    {
        await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: false, ct, new[] { claimId });
        var claim = await LoadClaimAsync(tenantId, claimId, tracked: false, ct)
            ?? throw new ExpenseNotFoundException("Expense claim not found.");
        if (claim.Status != ExpenseClaimStatuses.Submitted || claim.ApprovalRequestId is null)
            throw new ExpenseConflictException($"This claim is {claim.Status}; only a submitted claim can be approved or rejected.");
        var isReject = string.Equals(request.Decision, "Reject", StringComparison.OrdinalIgnoreCase);
        if (!isReject && !string.Equals(request.Decision, "Approve", StringComparison.OrdinalIgnoreCase))
            throw new ExpenseValidationException("invalid_decision", "Decision must be Approve or Reject.");
        if (isReject && string.IsNullOrWhiteSpace(request.Comments))
            throw new ExpenseValidationException("reason_required", "Give the employee a reason for the rejection.");

        // The approval engine owns maker-checker, the step's approver check, the DecisionVersion CAS and
        // the multi-step chain; only the step marked IsFinalStep completes the request.
        var decided = await _approvals.DecideAsync(tenantId, claim.ApprovalRequestId.Value,
            new ApprovalDecisionRequest(isReject ? "Reject" : "Approve", request.Comments?.Trim()), context, ct)
            ?? throw new ExpenseNotFoundException("The approval for this claim was not found or is outside your scope.");
        _db.ChangeTracker.Clear();
        await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: false, ct, new[] { claimId });
        await _audit.WriteAsync(isReject ? "expense.claim_rejected_step" : "expense.claim_approved_step", nameof(ExpenseClaim), claimId.ToString(), context,
            JsonSerializer.Serialize(new { approvalStatus = decided.Status, step = decided.CurrentStepOrder }), ct);
        return (await GetAsync(tenantId, claimId, context, ct))!;
    }

    public async Task<ExpensePayoutResult> ScheduleForPayrollAsync(Guid tenantId, ScheduleExpensesRequest request, Guid? actorUserId, CancellationToken ct)
    {
        await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: true, ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await ScheduleCoreAsync(tenantId, request, actorUserId, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private async Task<ExpensePayoutResult> ScheduleCoreAsync(Guid tenantId, ScheduleExpensesRequest request, Guid? actorUserId, CancellationToken ct)
    {
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == request.PayrollRunId, ct)
            ?? throw new ExpenseNotFoundException("Payroll run not found.");
        if (run.Status != "Draft" || run.LockedAtUtc.HasValue)
            throw new ExpenseConflictException($"Payroll run {run.Year}-{run.Month:00} is {run.Status}; expenses can only be added to an open (Draft) run.");
        if (run.CompanyId is null)
            throw new ExpenseValidationException("run_without_company", "This payroll run has no legal entity, so it cannot pay expense claims.");
        var companyCurrency = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == run.CompanyId && c.IsActive && !c.IsDeleted)
            .Select(c => c.DefaultCurrency).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(companyCurrency))
            throw new ExpenseValidationException("no_payroll_currency", "The run's legal entity has no active payroll currency.");

        // THE MONEY GATE: claim projected Approved AND its approval row actually Approved.
        var candidatesQuery =
            from c in _db.ExpenseClaims
            where c.TenantId == tenantId && c.CompanyId == run.CompanyId && c.Status == ExpenseClaimStatuses.Approved && c.ApprovalRequestId != null
            join a in _db.ApprovalRequests on c.ApprovalRequestId equals a.Id
            where a.TenantId == tenantId && a.Status == "Approved" && a.EntityName == ExpenseClaimConstants.ApprovalEntityName
            select c;
        if (request.ClaimIds is { Count: > 0 } ids) candidatesQuery = candidatesQuery.Where(c => ids.Contains(c.Id));
        var claims = await candidatesQuery.OrderBy(c => c.SubmittedAtUtc).ToListAsync(ct);

        var items = new List<ExpensePayoutItem>();
        if (request.ClaimIds is { Count: > 0 })
        {
            foreach (var missing in request.ClaimIds.Except(claims.Select(c => c.Id)))
                items.Add(new ExpensePayoutItem(missing, string.Empty, 0, 0m, "Skipped",
                    "Not an approved, unscheduled claim of this run's legal entity."));
        }

        var periodEnd = new DateTime(run.Year, run.Month, DateTime.DaysInMonth(run.Year, run.Month), 0, 0, 0, DateTimeKind.Utc);
        var employeeIds = claims.Select(c => c.EmployeeId).Distinct().ToList();
        var eligible = (await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id) && !e.IsDeleted
                && e.CompanyId == run.CompanyId && e.Status == "Active" && e.JoiningDate <= periodEnd)
            .Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var selections = await _db.PayrollRunEmployeeSelections.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == run.Id).ToListAsync(ct);
        var explicitOnly = PayrollRunTypes.RequiresExplicitPopulation(run.RunType);

        var claimIdList = claims.Select(c => c.Id).Cast<Guid?>().ToList();
        var adjustments = await _db.PayrollAdjustments
            .Where(a => a.TenantId == tenantId && a.SourceType == ExpenseClaimConstants.AdjustmentSourceType && claimIdList.Contains(a.SourceId))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var scheduled = 0;
        foreach (var claim in claims)
        {
            string? skip = null;
            if (!string.Equals(claim.Currency, companyCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
                skip = $"Claim currency {claim.Currency} does not match the run currency {companyCurrency}.";
            else if (!eligible.Contains(claim.EmployeeId))
                skip = "The employee is not active in this run's legal entity for the period.";
            else if (selections.Any(s => s.EmployeeId == claim.EmployeeId && s.Mode == PayrollRunSelectionModes.Exclude))
                skip = "The employee is excluded from this payroll run.";
            else if (explicitOnly && !selections.Any(s => s.EmployeeId == claim.EmployeeId && s.Mode == PayrollRunSelectionModes.Include))
                skip = "The employee is not included in this supplemental run.";

            var adjustment = adjustments.FirstOrDefault(a => a.SourceId == claim.Id);
            if (skip is null && adjustment is not null)
            {
                if (adjustment.Status == "Processed")
                    skip = "Already paid by a processed payroll run.";
                else if (adjustment.Status == "Approved" && adjustment.PayrollRunId != run.Id
                         && await _db.PayrollRuns.AnyAsync(r => r.TenantId == tenantId && r.Id == adjustment.PayrollRunId && r.Status != "Voided", ct))
                    skip = "Already bound to another open payroll run. Remove it from that run first.";
            }
            if (skip is not null)
            {
                items.Add(new ExpensePayoutItem(claim.Id, claim.ClaimNumber, claim.EmployeeId, claim.TotalAmount, "Skipped", skip));
                continue;
            }

            if (adjustment is null)
            {
                adjustment = new PayrollAdjustment
                {
                    TenantId = tenantId,
                    SourceType = ExpenseClaimConstants.AdjustmentSourceType,
                    SourceId = claim.Id,
                };
                _db.PayrollAdjustments.Add(adjustment);
            }
            // One row per claim, ever: a released claim re-points its existing row instead of adding one.
            adjustment.PayrollRunId = run.Id;
            adjustment.EmployeeId = claim.EmployeeId;
            adjustment.AdjustmentType = ExpenseClaimConstants.AdjustmentType;
            adjustment.Amount = claim.TotalAmount;
            adjustment.Reason = $"Expense claim {claim.ClaimNumber}";
            adjustment.Status = "Approved";

            claim.Status = ExpenseClaimStatuses.Scheduled;
            claim.PayrollRunId = run.Id;
            claim.PayrollAdjustmentId = adjustment.Id;
            claim.ScheduledAtUtc = now;
            claim.ScheduledByUserId = actorUserId;
            claim.Version++;
            scheduled++;
            items.Add(new ExpensePayoutItem(claim.Id, claim.ClaimNumber, claim.EmployeeId, claim.TotalAmount, "Scheduled", null));
        }
        await SaveOrConflictAsync("Another user scheduled these claims at the same time. Reload and try again.", ct);
        return new ExpensePayoutResult(run.Id, scheduled, items.Count - scheduled, items);
    }

    public async Task<ExpenseClaimDto> UnscheduleAsync(Guid tenantId, Guid claimId, Guid? actorUserId, CancellationToken ct)
    {
        await ExpenseClaimReconciler.ReconcileAsync(_db, tenantId, releaseVoidedRuns: true, ct, new[] { claimId });
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var claim = await LoadClaimAsync(tenantId, claimId, tracked: true, ct)
                ?? throw new ExpenseNotFoundException("Expense claim not found.");
            if (claim.Status != ExpenseClaimStatuses.Scheduled)
                throw new ExpenseConflictException($"This claim is {claim.Status}; only a scheduled, unpaid claim can be removed from a payroll run.");
            var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == claim.PayrollRunId, ct);
            if (run is not null && (run.Status != "Draft" || run.LockedAtUtc.HasValue))
                throw new ExpenseConflictException($"Payroll run {run.Year}-{run.Month:00} is {run.Status}; the claim can no longer be removed from it.");
            var adjustment = await _db.PayrollAdjustments.FirstOrDefaultAsync(a => a.TenantId == tenantId
                && a.SourceType == ExpenseClaimConstants.AdjustmentSourceType && a.SourceId == claim.Id, ct);
            if (adjustment is not null)
            {
                if (adjustment.Status == "Processed")
                    throw new ExpenseConflictException("This claim has already been paid.");
                adjustment.Status = "Voided";
            }
            claim.Status = ExpenseClaimStatuses.Approved;
            claim.PayrollRunId = null;
            claim.ScheduledAtUtc = null;
            claim.ScheduledByUserId = actorUserId;
            claim.Version++;
            await SaveOrConflictAsync("This claim was changed at the same time. Reload it.", ct);
            await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
        return (await GetAsync(tenantId, claimId, null, ct))!;
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> GetReceiptAsync(Guid tenantId, Guid claimId, Guid lineId, CancellationToken ct)
    {
        var line = await _db.ExpenseClaimLines.AsNoTracking()
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.ClaimId == claimId && l.Id == lineId, ct)
            ?? throw new ExpenseNotFoundException("Expense line not found.");
        if (string.IsNullOrWhiteSpace(line.ReceiptStorageKey))
            throw new ExpenseNotFoundException("This expense line has no receipt.");
        var bytes = await _storage.GetBytesAsync(tenantId, line.ReceiptStorageKey, ct);
        return (bytes, line.ReceiptContentType ?? "application/octet-stream", line.ReceiptFileName ?? "receipt");
    }

    // ══ Validation / policy ═════════════════════════════════════════════════════════════════════

    private sealed record LineCandidate(int LineNumber, DateOnly ExpenseDate, string CategoryCode, decimal Amount, string Description, bool HasReceipt);

    private async Task ApplyLinesAsync(ExpenseClaim claim, SaveExpenseClaimRequest request, bool requireReceipts, CancellationToken ct)
    {
        if (request?.Lines is null || request.Lines.Count == 0)
            throw new ExpenseValidationException("no_lines", "Add at least one expense line.");
        var candidates = request.Lines.Select((l, i) =>
        {
            var existing = l.Id is { } id ? claim.Lines.FirstOrDefault(x => x.Id == id) : null;
            return new LineCandidate(i + 1, l.ExpenseDate, (l.CategoryCode ?? string.Empty).Trim().ToUpperInvariant(), l.Amount,
                (l.Description ?? string.Empty).Trim(), existing?.ReceiptStorageKey is not null);
        }).ToList();
        foreach (var l in request.Lines.Where(l => l.Id is not null && claim.Lines.All(x => x.Id != l.Id)))
            throw new ExpenseValidationException("unknown_line", "One of the lines no longer exists on this claim. Reload it and try again.");

        var violations = await ValidateAsync(claim.TenantId, candidates, requireReceipts, ct);
        if (violations.Count > 0) throw new ExpenseValidationException(violations);
        var names = await CategoryValuesQuery(claim.TenantId, includeInactive: false).AsNoTracking()
            .ToDictionaryAsync(v => v.Code.ToUpper(), v => v.ValueEn, ct);

        var keep = request.Lines.Where(l => l.Id is not null).Select(l => l.Id!.Value).ToHashSet();
        foreach (var removed in claim.Lines.Where(l => !keep.Contains(l.Id)).ToList())
        {
            claim.Lines.Remove(removed);
            if (_db.Entry(removed).State != EntityState.Detached) _db.ExpenseClaimLines.Remove(removed);
        }
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var r = request.Lines[i];
            var c = candidates[i];
            var line = r.Id is { } id ? claim.Lines.First(x => x.Id == id) : null;
            if (line is null)
            {
                line = new ExpenseClaimLine { TenantId = claim.TenantId, CompanyId = claim.CompanyId, ClaimId = claim.Id };
                claim.Lines.Add(line);
                if (_db.Entry(claim).State != EntityState.Detached && _db.Entry(claim).State != EntityState.Added)
                    _db.ExpenseClaimLines.Add(line);
            }
            line.LineNumber = c.LineNumber;
            line.ExpenseDate = c.ExpenseDate;
            line.CategoryCode = c.CategoryCode;
            line.CategoryName = names.TryGetValue(c.CategoryCode, out var n) ? n : c.CategoryCode;
            line.Amount = c.Amount;
            line.Description = c.Description;
            line.UpdatedAtUtc = DateTime.UtcNow;
        }
        claim.Title = string.IsNullOrWhiteSpace(request.Title)
            ? $"{candidates.Count} expense{(candidates.Count == 1 ? string.Empty : "s")} — {candidates.Min(x => x.ExpenseDate):dd MMM yyyy}"
            : request.Title.Trim();
        claim.TotalAmount = candidates.Sum(x => x.Amount);
    }

    private async Task<List<ExpenseViolation>> ValidateAsync(Guid tenantId, IReadOnlyList<LineCandidate> lines, bool requireReceipts, CancellationToken ct)
    {
        var violations = new List<ExpenseViolation>();
        if (lines.Count == 0) violations.Add(new(null, "no_lines", "Add at least one expense line."));
        if (lines.Count > MaxLinesPerClaim) violations.Add(new(null, "too_many_lines", $"A claim can have at most {MaxLinesPerClaim} lines."));

        var categories = (await GetCategoriesAsync(tenantId, includeInactive: false, ct))
            .ToDictionary(c => c.Code.ToUpperInvariant(), c => c, StringComparer.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var l in lines)
        {
            if (l.Amount <= 0) violations.Add(new(l.LineNumber, "amount_not_positive", $"Line {l.LineNumber}: the amount must be greater than zero."));
            else if (l.Amount > MaxLineAmount) violations.Add(new(l.LineNumber, "amount_too_large", $"Line {l.LineNumber}: the amount is larger than any single expense allowed."));
            else if (decimal.Round(l.Amount, 2) != l.Amount) violations.Add(new(l.LineNumber, "amount_precision", $"Line {l.LineNumber}: amounts can have at most two decimal places."));
            if (l.ExpenseDate > today.AddDays(1)) violations.Add(new(l.LineNumber, "future_date", $"Line {l.LineNumber}: the expense date cannot be in the future."));
            if (string.IsNullOrWhiteSpace(l.Description)) violations.Add(new(l.LineNumber, "description_required", $"Line {l.LineNumber}: describe what the expense was for."));
            else if (l.Description.Length > 500) violations.Add(new(l.LineNumber, "description_too_long", $"Line {l.LineNumber}: the description is longer than 500 characters."));
            if (!categories.TryGetValue(l.CategoryCode, out var category))
            {
                violations.Add(new(l.LineNumber, "unknown_category", $"Line {l.LineNumber}: '{l.CategoryCode}' is not an active expense category."));
                continue;
            }
            if (requireReceipts && category.ReceiptRequiredAbove is { } threshold && l.Amount > threshold && !l.HasReceipt)
                violations.Add(new(l.LineNumber, "receipt_required",
                    $"Line {l.LineNumber}: {category.NameEn} expenses above {threshold:N2} need a receipt. Attach one before submitting."));
        }

        // Cap per category per claim (sum), so splitting one expense across lines cannot evade it.
        foreach (var group in lines.Where(l => categories.ContainsKey(l.CategoryCode)).GroupBy(l => l.CategoryCode))
        {
            var category = categories[group.Key];
            var sum = group.Sum(l => l.Amount);
            if (category.MaxAmountPerClaim is { } cap && sum > cap)
                violations.Add(new(group.Count() == 1 ? group.First().LineNumber : null, "category_cap_exceeded",
                    $"{category.NameEn}: {sum:N2} is over the limit of {cap:N2} per claim."));
        }
        return violations;
    }

    // ══ Loading / mapping ═══════════════════════════════════════════════════════════════════════

    private async Task<ExpenseClaim?> LoadClaimAsync(Guid tenantId, Guid claimId, bool tracked, CancellationToken ct)
    {
        var q = _db.ExpenseClaims.Include(c => c.Lines).Where(c => c.TenantId == tenantId && c.Id == claimId);
        if (!tracked) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(ct);
    }

    private async Task<ExpenseClaim> LoadOwnDraftAsync(Guid tenantId, int employeeId, Guid claimId, CancellationToken ct)
    {
        var claim = await LoadClaimAsync(tenantId, claimId, tracked: true, ct);
        if (claim is null || claim.EmployeeId != employeeId)
            throw new ExpenseNotFoundException("Expense claim not found.");
        if (claim.Status != ExpenseClaimStatuses.Draft)
            throw new ExpenseConflictException($"This claim is {claim.Status}; only a draft can be changed.");
        return claim;
    }

    private async Task<Dictionary<Guid, string>> RunPeriodsAsync(Guid tenantId, IEnumerable<ExpenseClaim> claims, CancellationToken ct)
    {
        var runIds = claims.Where(c => c.PayrollRunId.HasValue).Select(c => c.PayrollRunId!.Value).Distinct().ToList();
        if (runIds.Count == 0) return new();
        return await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => $"{r.Year}-{r.Month:00}", ct);
    }

    private async Task<ExpenseClaimDto> ToDtoAsync(ExpenseClaim claim, RequestContext? context, CancellationToken ct)
    {
        var periods = await RunPeriodsAsync(claim.TenantId, new[] { claim }, ct);
        ExpenseApprovalProgressDto? progress = null;
        if (claim.ApprovalRequestId is { } approvalId)
        {
            var a = context is null
                ? await _approvals.GetRequestAsync(claim.TenantId, approvalId, ct)
                : await _approvals.GetRequestAsync(claim.TenantId, approvalId, context, ct);
            if (a is not null)
                progress = new ExpenseApprovalProgressDto(a.Status, a.CurrentStepOrder, a.CurrentApproverName, a.CurrentApproverRole, a.DueAtUtc, a.CanDecide);
        }
        return ToDto(claim, periods, progress);
    }

    private static ExpenseClaimDto ToDto(ExpenseClaim c, IReadOnlyDictionary<Guid, string> periods, ExpenseApprovalProgressDto? progress)
        => new(c.Id, c.ClaimNumber, c.EmployeeId, c.EmployeeName, c.CompanyId, c.Title, c.Currency, c.TotalAmount, c.Status,
            c.CreatedAtUtc, c.SubmittedAtUtc, c.DecidedAtUtc, c.RejectionReason, c.ApprovalRequestId, c.PayrollRunId,
            c.PayrollRunId is { } r && periods.TryGetValue(r, out var p) ? p : null,
            c.ScheduledAtUtc, c.PaidAtUtc,
            c.Lines.OrderBy(l => l.LineNumber).Select(l => new ExpenseClaimLineDto(l.Id, l.LineNumber, l.ExpenseDate, l.CategoryCode, l.CategoryName,
                l.Amount, l.Description, l.ReceiptStorageKey is not null, l.ReceiptFileName, l.ReceiptContentType, l.ReceiptSizeBytes)).ToList(),
            progress);

    private async Task SaveOrConflictAsync(string conflictMessage, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex) { throw new ExpenseConflictException(conflictMessage, ex); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new ExpenseConflictException(conflictMessage, ex);
        }
    }

    private static bool SeesAllApprovals(RequestContext context)
    {
        var roles = context.Roles ?? Array.Empty<string>();
        var permissions = context.Permissions ?? Array.Empty<string>();
        return roles.Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                              || r.Equals("HR Manager", StringComparison.OrdinalIgnoreCase)
                              || r.Equals("Auditor", StringComparison.OrdinalIgnoreCase))
               || permissions.Any(p => p.Equals("approvals.override", StringComparison.OrdinalIgnoreCase));
    }

    private static string NewClaimNumber()
        => $"EXP-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}
