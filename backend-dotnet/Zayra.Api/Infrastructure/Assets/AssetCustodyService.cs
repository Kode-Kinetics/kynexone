using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Assets;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Assets;

/// <summary>
/// W2-C — the one place custody changes happen. Every operation:
/// <list type="bullet">
///   <item>runs inside ONE database transaction, opened inside the retrying execution strategy
///     (so it is safe under <c>EnableRetryOnFailure</c> and re-runs from a clean change tracker);</item>
///   <item>writes a central audit row (<c>asset.*</c>) in that same transaction;</item>
///   <item>relies on PostgreSQL for the custody invariant — the partial unique index
///     <c>ux_asset_assignments_one_active_holder</c> — and on <see cref="Asset.Version"/> for every other
///     state race. A lost race is reported as a typed 409, never as a 500 or a silent double-issue.</item>
/// </list>
/// </summary>
public interface IAssetCustodyService
{
    Task<Asset> CreateAsync(Guid tenantId, AssetUpsertRequest request, RequestContext context, CancellationToken ct);
    Task<Asset> UpdateAsync(Guid tenantId, Guid assetId, AssetUpsertRequest request, RequestContext context, CancellationToken ct);
    Task<AssetAssignment> IssueAsync(Guid tenantId, Guid assetId, IssueAssetRequest request, RequestContext context, CancellationToken ct);
    Task<AssetAssignment> ReturnAsync(Guid tenantId, Guid assetId, ReturnAssetRequest request, RequestContext context, CancellationToken ct);
    Task<AssetAssignment> TransferAsync(Guid tenantId, Guid assetId, TransferAssetRequest request, RequestContext context, CancellationToken ct);
    Task<AssetWriteOffRequest> RequestWriteOffAsync(Guid tenantId, Guid assetId, WriteOffAssetRequest request, RequestContext context, CancellationToken ct);
    Task<Asset> RetireAsync(Guid tenantId, Guid assetId, RetireAssetRequest request, RequestContext context, CancellationToken ct);
    Task<Asset> RepairAsync(Guid tenantId, Guid assetId, RepairAssetRequest request, RequestContext context, CancellationToken ct);
    Task<AssetAssignment> SetExpectedReturnDateAsync(Guid tenantId, Guid assignmentId, DateOnly? expectedReturnDate, RequestContext context, CancellationToken ct);

    /// <summary>
    /// Applies every write-off whose approval request has reached a terminal state. Approved → the custody
    /// period closes as WrittenOff and the asset leaves the register (Lost / Retired). Rejected → nothing moves.
    /// Returns the number of write-offs settled. Idempotent.
    /// </summary>
    Task<int> ReconcileWriteOffsAsync(Guid tenantId, RequestContext context, CancellationToken ct, Guid? assetId = null);
}

public sealed class AssetCustodyService : IAssetCustodyService
{
    public const string ApprovalEntityName = "AssetWriteOff";
    private const string OneActiveHolderIndex = "ux_asset_assignments_one_active_holder";
    private const string OnePendingWriteOffIndex = "ux_asset_write_off_requests_one_pending";

    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IApprovalWorkflowService _approvals;
    private readonly AssetClearanceService _clearance;

    public AssetCustodyService(ZayraDbContext db, IAuditService audit, IApprovalWorkflowService approvals)
    {
        _db = db;
        _audit = audit;
        _approvals = approvals;
        _clearance = new AssetClearanceService(db);
    }

    // ── Register ─────────────────────────────────────────────────────────────────

    public Task<Asset> CreateAsync(Guid tenantId, AssetUpsertRequest request, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var tag = Clean(request.AssetTag).ToUpperInvariant();
            if (tag.Length == 0) throw new AssetOperationException("asset_tag_required", "An asset tag is required.", 400);
            if (await _db.Assets.AnyAsync(a => a.TenantId == tenantId && a.AssetTag == tag, ct))
                throw new AssetOperationException("asset_tag_taken", $"Asset tag '{tag}' is already in the register.");
            var companyId = await ResolveCompanyAsync(tenantId, request.CompanyId, ct);
            if (request.PurchaseCost is < 0) throw new AssetOperationException("invalid_cost", "Purchase cost cannot be negative.", 400);

            var asset = new Asset
            {
                TenantId = tenantId,
                CompanyId = companyId,
                AssetTag = tag,
                Status = AssetStatuses.InStock,
                CreatedBy = context.UserId,
            };
            ApplyRegisterFields(asset, request);
            _db.Assets.Add(asset);
            await _audit.WriteAsync("asset.created", nameof(Asset), asset.Id.ToString(), context,
                Json(new { asset.AssetTag, asset.CategoryCode, asset.SerialNumber, asset.CompanyId }), ct);
            return asset;
        }, ct);

    public Task<Asset> UpdateAsync(Guid tenantId, Guid assetId, AssetUpsertRequest request, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var asset = await LoadAssetAsync(tenantId, assetId, ct);
            var tag = Clean(request.AssetTag).ToUpperInvariant();
            if (tag.Length == 0) throw new AssetOperationException("asset_tag_required", "An asset tag is required.", 400);
            if (!string.Equals(tag, asset.AssetTag, StringComparison.Ordinal)
                && await _db.Assets.AnyAsync(a => a.TenantId == tenantId && a.AssetTag == tag && a.Id != assetId, ct))
                throw new AssetOperationException("asset_tag_taken", $"Asset tag '{tag}' is already in the register.");
            if (request.PurchaseCost is < 0) throw new AssetOperationException("invalid_cost", "Purchase cost cannot be negative.", 400);
            // Ownership is not an edit: company is fixed at registration (the company-scope write guard
            // would refuse it anyway). Custody fields (status, holder) only move through the operations below.
            asset.AssetTag = tag;
            ApplyRegisterFields(asset, request);
            asset.UpdatedBy = context.UserId;
            asset.Version++;
            await _audit.WriteAsync("asset.updated", nameof(Asset), asset.Id.ToString(), context,
                Json(new { asset.AssetTag, asset.CategoryCode, asset.SerialNumber }), ct);
            return asset;
        }, ct);

    // ── Custody operations ───────────────────────────────────────────────────────

    public Task<AssetAssignment> IssueAsync(Guid tenantId, Guid assetId, IssueAssetRequest request, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var asset = await LoadAssetAsync(tenantId, assetId, ct);
            if (asset.Status != AssetStatuses.InStock)
                throw new AssetOperationException(asset.Status == AssetStatuses.Assigned ? "asset_already_assigned" : "asset_not_available",
                    asset.Status == AssetStatuses.Assigned
                        ? $"Asset {asset.AssetTag} is already assigned. Return or transfer it instead."
                        : $"Asset {asset.AssetTag} is {asset.Status} and cannot be issued.");
            var employee = await LoadIssuableEmployeeAsync(tenantId, request.EmployeeId, asset, ct);
            var issuedOn = request.IssuedOn ?? Today();
            ValidateExpectedReturn(issuedOn, request.ExpectedReturnDate);

            var assignment = NewAssignment(asset, employee, issuedOn, request.ExpectedReturnDate, request.Condition, request.Notes, context);
            assignment.IssuedByName = await ActorNameAsync(tenantId, context.UserId, ct);
            _db.AssetAssignments.Add(assignment);
            asset.Status = AssetStatuses.Assigned;
            if (!string.IsNullOrWhiteSpace(request.Condition)) asset.Condition = Clean(request.Condition).ToUpperInvariant();
            asset.UpdatedBy = context.UserId;
            asset.Version++;

            await _clearance.SyncOffboardingFlagAsync(tenantId, employee.Id, cleared: false, ct);
            await _audit.WriteAsync("asset.issued", nameof(Asset), asset.Id.ToString(), context,
                Json(new { assignmentId = assignment.Id, employeeId = employee.Id, assignment.IssuedOn, assignment.ExpectedReturnDate }), ct);
            return assignment;
        }, ct);

    public Task<AssetAssignment> ReturnAsync(Guid tenantId, Guid assetId, ReturnAssetRequest request, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var asset = await LoadAssetAsync(tenantId, assetId, ct);
            var active = await LoadActiveAssignmentAsync(tenantId, asset, ct);
            await EnsureNoPendingWriteOffAsync(tenantId, asset, ct);
            var condition = Clean(request.Condition).ToUpperInvariant();
            if (condition.Length == 0) throw new AssetOperationException("condition_required", "Record the condition the item came back in.", 400);
            var returnedOn = request.ReturnedOn ?? Today();
            if (returnedOn < active.IssuedOn)
                throw new AssetOperationException("invalid_return_date", "The return date cannot be before the issue date.", 400);

            active.Status = AssetAssignmentStatuses.Returned;
            active.ReturnedOn = returnedOn;
            active.ClosedAtUtc = DateTime.UtcNow;
            active.ClosedByUserId = context.UserId;
            active.ClosedByName = await ActorNameAsync(tenantId, context.UserId, ct);
            active.ConditionOnReturn = condition;
            active.ReturnNotes = Clean(request.Notes);

            asset.Status = request.SendToRepair ? AssetStatuses.InRepair : AssetStatuses.InStock;
            asset.Condition = condition;
            asset.UpdatedBy = context.UserId;
            asset.Version++;

            await _clearance.SyncOffboardingFlagAsync(tenantId, active.EmployeeId, cleared: true, ct);
            await _audit.WriteAsync("asset.returned", nameof(Asset), asset.Id.ToString(), context,
                Json(new { assignmentId = active.Id, employeeId = active.EmployeeId, returnedOn, condition, toRepair = request.SendToRepair }), ct);
            return active;
        }, ct);

    public Task<AssetAssignment> TransferAsync(Guid tenantId, Guid assetId, TransferAssetRequest request, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var asset = await LoadAssetAsync(tenantId, assetId, ct);
            var current = await LoadActiveAssignmentAsync(tenantId, asset, ct);
            await EnsureNoPendingWriteOffAsync(tenantId, asset, ct);
            if (current.EmployeeId == request.ToEmployeeId)
                throw new AssetOperationException("transfer_to_same_holder", "The item is already held by that employee.", 400);
            var employee = await LoadIssuableEmployeeAsync(tenantId, request.ToEmployeeId, asset, ct);
            var today = Today();
            ValidateExpectedReturn(today, request.ExpectedReturnDate);
            var actorName = await ActorNameAsync(tenantId, context.UserId, ct);
            var condition = string.IsNullOrWhiteSpace(request.Condition) ? asset.Condition : Clean(request.Condition).ToUpperInvariant();

            // Close the old custody period FIRST and flush it: the partial unique index is checked per
            // statement, so the new Active row can only be inserted once the old one has left 'Active'.
            // Both statements are in this one transaction — a failure after this point rolls both back.
            current.Status = AssetAssignmentStatuses.Transferred;
            current.ReturnedOn = today;
            current.ClosedAtUtc = DateTime.UtcNow;
            current.ClosedByUserId = context.UserId;
            current.ClosedByName = actorName;
            current.ConditionOnReturn = condition;
            current.ReturnNotes = $"Transferred to {employee.FullName} ({employee.EmployeeCode}).";
            await _db.SaveChangesAsync(ct);

            var next = NewAssignment(asset, employee, today, request.ExpectedReturnDate, condition, request.Notes, context);
            next.IssuedByName = actorName;
            _db.AssetAssignments.Add(next);
            current.TransferredToAssignmentId = next.Id;
            asset.Condition = condition;
            asset.UpdatedBy = context.UserId;
            asset.Version++;

            await _clearance.SyncOffboardingFlagAsync(tenantId, current.EmployeeId, cleared: true, ct);
            await _clearance.SyncOffboardingFlagAsync(tenantId, employee.Id, cleared: false, ct);
            await _audit.WriteAsync("asset.transferred", nameof(Asset), asset.Id.ToString(), context,
                Json(new { fromAssignmentId = current.Id, fromEmployeeId = current.EmployeeId, toAssignmentId = next.Id, toEmployeeId = employee.Id }), ct);
            return next;
        }, ct);

    public Task<AssetWriteOffRequest> RequestWriteOffAsync(Guid tenantId, Guid assetId, WriteOffAssetRequest request, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var asset = await LoadAssetAsync(tenantId, assetId, ct);
            if (AssetStatuses.IsTerminal(asset.Status))
                throw new AssetOperationException("asset_not_available", $"Asset {asset.AssetTag} is already {asset.Status}.");
            var kind = string.Equals(request.Kind, AssetWriteOffKinds.Damaged, StringComparison.OrdinalIgnoreCase)
                ? AssetWriteOffKinds.Damaged : string.Equals(request.Kind, AssetWriteOffKinds.Lost, StringComparison.OrdinalIgnoreCase)
                    ? AssetWriteOffKinds.Lost : throw new AssetOperationException("invalid_kind", "Kind must be Lost or Damaged.", 400);
            var reason = Clean(request.Reason);
            if (reason.Length < 5) throw new AssetOperationException("reason_required", "Give a reason for the write-off (at least 5 characters).", 400);
            await EnsureNoPendingWriteOffAsync(tenantId, asset, ct);

            var active = await _db.AssetAssignments
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.AssetId == asset.Id && a.Status == AssetAssignmentStatuses.Active, ct);
            var writeOff = new AssetWriteOffRequest
            {
                TenantId = tenantId,
                CompanyId = asset.CompanyId,
                AssetId = asset.Id,
                AssignmentId = active?.Id,
                EmployeeId = active?.EmployeeId,
                Kind = kind,
                Reason = reason,
                Status = AssetWriteOffStatuses.Pending,
                RequestedByUserId = context.UserId,
                RequestedByName = await ActorNameAsync(tenantId, context.UserId, ct),
            };
            _db.AssetWriteOffRequests.Add(writeOff);
            // Bumping the version makes the pending write-off conflict with any concurrent custody change.
            asset.Version++;
            await _db.SaveChangesAsync(ct);

            // F1: the ONE router picks the workflow (WorkflowId = null). No configured workflow is a typed
            // 422 (approval_route_not_configured) and — because we are inside the transaction — nothing
            // above survives it.
            var approval = await _approvals.CreateRequestAsync(tenantId, new CreateApprovalRequest(
                WorkflowId: null,
                EntityName: ApprovalEntityName,
                EntityId: writeOff.Id.ToString(),
                Title: $"Write off {asset.AssetTag} ({kind}){(active is null ? string.Empty : $" — held by {active.EmployeeName}")}",
                RequestedForEmployeeId: active?.EmployeeId,
                CompanyId: asset.CompanyId,
                Priority: "Normal"), context, ct);
            writeOff.ApprovalRequestId = approval.Id;
            await _audit.WriteAsync("asset.write_off_requested", nameof(Asset), asset.Id.ToString(), context,
                Json(new { writeOffId = writeOff.Id, approvalRequestId = approval.Id, kind, reason, assignmentId = active?.Id }), ct);
            return writeOff;
        }, ct);

    public Task<Asset> RetireAsync(Guid tenantId, Guid assetId, RetireAssetRequest request, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var asset = await LoadAssetAsync(tenantId, assetId, ct);
            if (asset.Status is not (AssetStatuses.InStock or AssetStatuses.InRepair))
                throw new AssetOperationException("asset_not_retirable",
                    asset.Status == AssetStatuses.Assigned
                        ? $"Asset {asset.AssetTag} is held by an employee. Return it first, or request a write-off if it is lost or damaged."
                        : $"Asset {asset.AssetTag} is already {asset.Status}.");
            await EnsureNoPendingWriteOffAsync(tenantId, asset, ct);
            var reason = Clean(request.Reason);
            if (reason.Length < 5) throw new AssetOperationException("reason_required", "Give a reason for retiring the item.", 400);
            asset.Status = AssetStatuses.Retired;
            asset.RetiredAtUtc = DateTime.UtcNow;
            asset.RetirementReason = reason;
            asset.UpdatedBy = context.UserId;
            asset.Version++;
            await _audit.WriteAsync("asset.retired", nameof(Asset), asset.Id.ToString(), context, Json(new { reason }), ct);
            return asset;
        }, ct);

    public Task<Asset> RepairAsync(Guid tenantId, Guid assetId, RepairAssetRequest request, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var asset = await LoadAssetAsync(tenantId, assetId, ct);
            var send = string.Equals(request.Action, "Send", StringComparison.OrdinalIgnoreCase);
            if (send && asset.Status != AssetStatuses.InStock)
                throw new AssetOperationException("asset_not_available",
                    $"Only an in-stock item can be sent to repair (this one is {asset.Status}). Return it first.");
            if (!send && asset.Status != AssetStatuses.InRepair)
                throw new AssetOperationException("asset_not_in_repair", $"Asset {asset.AssetTag} is not in repair.");
            asset.Status = send ? AssetStatuses.InRepair : AssetStatuses.InStock;
            if (!string.IsNullOrWhiteSpace(request.Condition)) asset.Condition = Clean(request.Condition).ToUpperInvariant();
            asset.UpdatedBy = context.UserId;
            asset.Version++;
            await _audit.WriteAsync(send ? "asset.sent_to_repair" : "asset.repaired", nameof(Asset), asset.Id.ToString(), context,
                Json(new { notes = Clean(request.Notes), asset.Condition }), ct);
            return asset;
        }, ct);

    public Task<AssetAssignment> SetExpectedReturnDateAsync(Guid tenantId, Guid assignmentId, DateOnly? expectedReturnDate, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var assignment = await _db.AssetAssignments.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == assignmentId, ct)
                ?? throw new AssetOperationException("assignment_not_found", "Assignment not found.", 404);
            if (assignment.Status != AssetAssignmentStatuses.Active)
                throw new AssetOperationException("assignment_closed", "Only a current assignment's return date can change.");
            ValidateExpectedReturn(assignment.IssuedOn, expectedReturnDate);
            var previous = assignment.ExpectedReturnDate;
            assignment.ExpectedReturnDate = expectedReturnDate;
            // A new due date re-arms both reminders (the outbox dedupe key includes the date, so it is a new message).
            assignment.DueSoonReminderSentAtUtc = null;
            assignment.OverdueReminderSentAtUtc = null;
            await _audit.WriteAsync("asset.return_date_changed", nameof(Asset), assignment.AssetId.ToString(), context,
                Json(new { assignmentId, previous, expectedReturnDate }), ct);
            return assignment;
        }, ct);

    // ── Write-off settlement (pull from the approval engine) ─────────────────────

    public async Task<int> ReconcileWriteOffsAsync(Guid tenantId, RequestContext context, CancellationToken ct, Guid? assetId = null)
    {
        // Candidate list read outside the unit; each write-off is then settled in its own transaction so
        // one conflicting row cannot hold back the rest.
        var candidates = await (
                from w in _db.AssetWriteOffRequests.AsNoTracking()
                join a in _db.ApprovalRequests.AsNoTracking() on w.ApprovalRequestId equals a.Id
                where w.TenantId == tenantId && w.Status == AssetWriteOffStatuses.Pending
                      && a.TenantId == tenantId && a.Status != "Pending"
                      && (assetId == null || w.AssetId == assetId)
                select w.Id)
            .Take(200)
            .ToListAsync(ct);

        var settled = 0;
        foreach (var id in candidates)
        {
            try
            {
                if (await SettleWriteOffAsync(tenantId, id, context, ct)) settled++;
            }
            catch (AssetOperationException) { /* lost a race with another settler — it is settled */ }
        }
        return settled;
    }

    private Task<bool> SettleWriteOffAsync(Guid tenantId, Guid writeOffId, RequestContext context, CancellationToken ct)
        => RunAsync(async () =>
        {
            var writeOff = await _db.AssetWriteOffRequests.FirstOrDefaultAsync(w => w.TenantId == tenantId && w.Id == writeOffId, ct);
            if (writeOff is null || writeOff.Status != AssetWriteOffStatuses.Pending || writeOff.ApprovalRequestId is null) return false;
            var approval = await _db.ApprovalRequests.AsNoTracking().Include(x => x.Decisions)
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == writeOff.ApprovalRequestId, ct);
            if (approval is null || approval.Status == "Pending") return false;

            var comments = approval.Decisions.OrderByDescending(d => d.StepOrder).Select(d => d.Comments).FirstOrDefault() ?? string.Empty;
            writeOff.DecidedAtUtc = approval.CompletedAtUtc ?? DateTime.UtcNow;
            writeOff.DecisionComments = comments;
            var auditContext = context with { TenantId = tenantId };

            if (!string.Equals(approval.Status, "Approved", StringComparison.OrdinalIgnoreCase))
            {
                writeOff.Status = AssetWriteOffStatuses.Rejected;
                await _audit.WriteAsync("asset.write_off_rejected", nameof(Asset), writeOff.AssetId.ToString(), auditContext,
                    Json(new { writeOffId, approvalRequestId = approval.Id, approvalStatus = approval.Status }), ct);
                return true;
            }

            // Approved — the FINAL step approved it (only IsFinalStep sets Approved; F1).
            var asset = await _db.Assets.FirstAsync(a => a.TenantId == tenantId && a.Id == writeOff.AssetId, ct);
            writeOff.Status = AssetWriteOffStatuses.Approved;
            var active = await _db.AssetAssignments
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.AssetId == asset.Id && a.Status == AssetAssignmentStatuses.Active, ct);
            if (active is not null)
            {
                active.Status = AssetAssignmentStatuses.WrittenOff;
                active.ClosedAtUtc = DateTime.UtcNow;
                active.ClosedByUserId = context.UserId;
                active.WriteOffRequestId = writeOff.Id;
                active.ConditionOnReturn = writeOff.Kind == AssetWriteOffKinds.Damaged ? "DAMAGED" : string.Empty;
                active.ReturnNotes = $"Written off ({writeOff.Kind}): {writeOff.Reason}";
            }
            asset.Status = writeOff.Kind == AssetWriteOffKinds.Lost ? AssetStatuses.Lost : AssetStatuses.Retired;
            if (writeOff.Kind == AssetWriteOffKinds.Damaged) asset.Condition = "DAMAGED";
            asset.RetiredAtUtc = DateTime.UtcNow;
            asset.RetirementReason = $"Written off ({writeOff.Kind}), approval {approval.Id}: {writeOff.Reason}";
            asset.Version++;
            if (active is not null)
                await _clearance.SyncOffboardingFlagAsync(tenantId, active.EmployeeId, cleared: true, ct);
            await _audit.WriteAsync("asset.written_off", nameof(Asset), asset.Id.ToString(), auditContext,
                Json(new { writeOffId, approvalRequestId = approval.Id, writeOff.Kind, assignmentId = active?.Id, employeeId = active?.EmployeeId }), ct);
            return true;
        }, ct);

    // ── Unit of work ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One transaction per operation, inside the retrying execution strategy. The change tracker is cleared
    /// at the start of every attempt so a retry re-reads state instead of replaying stale entities. Database
    /// refusals of the custody invariants are translated to typed <see cref="AssetOperationException"/>s.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        try
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var result = await body();
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return result;
            });
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, OneActiveHolderIndex))
        {
            _db.ChangeTracker.Clear();
            throw new AssetOperationException("asset_already_assigned",
                "This asset was just assigned to someone else. Refresh and try again.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, OnePendingWriteOffIndex))
        {
            _db.ChangeTracker.Clear();
            throw new AssetOperationException("write_off_pending", "A write-off for this asset is already awaiting approval.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "IX_assets_tenant_id_asset_tag"))
        {
            _db.ChangeTracker.Clear();
            throw new AssetOperationException("asset_tag_taken", "That asset tag is already in the register.");
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new AssetOperationException("asset_changed_concurrently",
                "This asset was changed by someone else at the same time. Refresh and try again.");
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex, string constraint)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
           && string.Equals(pg.ConstraintName, constraint, StringComparison.OrdinalIgnoreCase);

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<Asset> LoadAssetAsync(Guid tenantId, Guid assetId, CancellationToken ct)
        => await _db.Assets.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == assetId, ct)
           ?? throw new AssetOperationException("asset_not_found", "Asset not found.", 404);

    private async Task<AssetAssignment> LoadActiveAssignmentAsync(Guid tenantId, Asset asset, CancellationToken ct)
    {
        if (asset.Status != AssetStatuses.Assigned)
            throw new AssetOperationException("asset_not_assigned", $"Asset {asset.AssetTag} is not currently assigned to anyone.");
        return await _db.AssetAssignments
                   .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.AssetId == asset.Id && a.Status == AssetAssignmentStatuses.Active, ct)
               ?? throw new AssetOperationException("asset_not_assigned", $"Asset {asset.AssetTag} has no active holder on record.");
    }

    private async Task EnsureNoPendingWriteOffAsync(Guid tenantId, Asset asset, CancellationToken ct)
    {
        if (await _db.AssetWriteOffRequests.AnyAsync(w => w.TenantId == tenantId && w.AssetId == asset.Id && w.Status == AssetWriteOffStatuses.Pending, ct))
            throw new AssetOperationException("write_off_pending",
                $"A write-off for {asset.AssetTag} is awaiting approval. If the item has turned up, reject the write-off in Approvals first.");
    }

    private async Task<Employee> LoadIssuableEmployeeAsync(Guid tenantId, int employeeId, Asset asset, CancellationToken ct)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, ct)
                       ?? throw new AssetOperationException("employee_not_found", "Employee not found.", 404);
        if (!EstablishmentOccupancy.IsOccupyingStatus(employee.Status))
            throw new AssetOperationException("employee_not_active",
                $"{employee.FullName} is '{employee.Status}'. Assets can only be issued to a current employee.");
        // An item stays inside the legal entity that owns it: its holder must belong to the same company.
        if (asset.CompanyId is not null && employee.CompanyId is not null && asset.CompanyId != employee.CompanyId)
            throw new AssetOperationException("company_mismatch",
                "This asset belongs to a different company from the employee. Issue an asset owned by the employee's company.", 400);
        return employee;
    }

    private static AssetAssignment NewAssignment(Asset asset, Employee employee, DateOnly issuedOn, DateOnly? expectedReturn,
        string? condition, string? notes, RequestContext context) => new()
    {
        TenantId = asset.TenantId,
        CompanyId = asset.CompanyId,
        AssetId = asset.Id,
        EmployeeId = employee.Id,
        EmployeeName = employee.FullName,
        EmployeeCode = employee.EmployeeCode,
        Status = AssetAssignmentStatuses.Active,
        IssuedOn = issuedOn,
        IssuedAtUtc = DateTime.UtcNow,
        IssuedByUserId = context.UserId,
        ConditionOnIssue = string.IsNullOrWhiteSpace(condition) ? asset.Condition : Clean(condition).ToUpperInvariant(),
        IssueNotes = Clean(notes),
        ExpectedReturnDate = expectedReturn,
    };

    private static void ValidateExpectedReturn(DateOnly from, DateOnly? expected)
    {
        if (expected is { } d && d < from)
            throw new AssetOperationException("invalid_expected_return", "The expected return date cannot be before the issue date.", 400);
    }

    private async Task<Guid?> ResolveCompanyAsync(Guid tenantId, Guid? requested, CancellationToken ct)
    {
        if (requested is Guid id)
        {
            if (!await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == id, ct))
                throw new AssetOperationException("company_not_found", "Company not found.", 400);
            return id;
        }
        // No company given: a single-company tenant is unambiguous; otherwise the caller must choose.
        var companies = await _db.Companies.Where(c => c.TenantId == tenantId && c.IsActive).Select(c => c.Id).Take(2).ToListAsync(ct);
        if (companies.Count == 1) return companies[0];
        if (companies.Count == 0) return null;
        throw new AssetOperationException("company_required", "Choose which company owns this asset.", 400);
    }

    private static void ApplyRegisterFields(Asset asset, AssetUpsertRequest request)
    {
        asset.Name = Clean(request.Name);
        asset.SerialNumber = Clean(request.SerialNumber);
        asset.CategoryCode = Clean(request.CategoryCode).ToUpperInvariant();
        asset.Make = Clean(request.Make);
        asset.Model = Clean(request.Model);
        asset.PurchaseDate = request.PurchaseDate;
        asset.PurchaseCost = request.PurchaseCost;
        asset.Currency = Clean(request.Currency).ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(request.Condition)) asset.Condition = Clean(request.Condition).ToUpperInvariant();
        asset.BranchId = request.BranchId;
        asset.LocationId = request.LocationId;
        asset.LocationNote = Clean(request.LocationNote);
        asset.Notes = Clean(request.Notes);
    }

    private async Task<string> ActorNameAsync(Guid tenantId, Guid? userId, CancellationToken ct)
    {
        if (userId is null) return "System";
        return await _db.Users.AsNoTracking().Where(u => u.Id == userId && u.TenantId == tenantId)
                   .Select(u => u.FullName).FirstOrDefaultAsync(ct)
               ?? userId.Value.ToString();
    }

    internal static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);
    private static string Clean(string? value) => (value ?? string.Empty).Trim();
    private static string Json(object value) => JsonSerializer.Serialize(value);
}
