using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>What a caller is about to attempt. The request body is hashed and previewed (masked), never stored.</summary>
public sealed record GovernmentExchangeAttempt(
    Guid TenantId,
    Guid? CompanyId,
    string Body,
    string Operation,
    string Channel,
    string IdempotencyKey,
    string State,
    RequestContext Context,
    IGovernmentAdapter? Adapter = null,
    string? Environment = null,
    string? SubjectType = null,
    string? SubjectId = null,
    byte[]? RequestBody = null,
    string? ArtifactSha256 = null,
    string? PreviewText = null,
    Guid? ApprovalRequestId = null,
    Guid? JobId = null,
    string? ArtifactSecondarySha256 = null,
    string? AccountFingerprint = null,
    string? BatchReference = null,
    decimal? TotalAmount = null,
    int? ItemCount = null);

/// <summary>How an attempt moved on. The response body is hashed, never stored.</summary>
public sealed record GovernmentExchangeOutcome(
    string State,
    RequestContext Context,
    int? HttpStatus = null,
    byte[]? ResponseBody = null,
    string? PreviewText = null,
    string? ErrorCode = null,
    string? ExternalReference = null,
    bool Completed = false,
    bool IsPoll = false);

/// <summary>
/// The ledger row, and whether this call created it (false: the idempotency key had already been used). <see cref="Receipt"/> is
/// set only when this call CREATED an intent on the api channel and it is committed: the one thing that lets the transport send.
/// A replayed key gets no receipt, so a retried job can never send twice; the outcome of the first is a person's to resolve.
/// </summary>
public sealed record GovernmentExchangeRecordResult(GovernmentExchange Exchange, bool Created, GovernmentExchangeIntentReceipt? Receipt = null);

/// <summary>
/// Proof that an intent row is durably committed BEFORE an outbound call. Only <see cref="GovernmentExchangeRecorder"/> creates one
/// (internal constructor; ratchet), and only after its own commit outside any caller transaction, so a rollback of the caller's work
/// cannot erase the record of a call that was made. <see cref="GovernmentHttp"/> refuses every non-GET call that does not carry one
/// for the same body and environment.
/// </summary>
public sealed class GovernmentExchangeIntentReceipt
{
    private int _spent;

    internal GovernmentExchangeIntentReceipt(Guid exchangeId, Guid tenantId, string body, string environment, string idempotencyKey, string? artifactSha256,
        string? artifactSecondarySha256 = null, Guid? companyId = null)
    {
        ExchangeId = exchangeId;
        TenantId = tenantId;
        CompanyId = companyId;
        Body = body;
        Environment = environment;
        IdempotencyKey = idempotencyKey;
        ArtifactSha256 = artifactSha256;
        ArtifactSecondarySha256 = artifactSecondarySha256;
    }

    public Guid ExchangeId { get; }
    public Guid TenantId { get; }
    /// <summary>The company of the intent row (null for a tenant-wide one). A bank channel refuses a receipt for another company than its scope.</summary>
    public Guid? CompanyId { get; }

    /// <summary>
    /// Spends the receipt: true exactly once, atomically, for the whole process. <see cref="GovernmentHttp"/> calls it immediately
    /// before the socket of every write, and a gated channel burns an unused receipt when its one attempt ends without sending.
    /// INTERIM (wave 1): in memory, on the one receipt object the recorder handed out (a replayed key never gets a receipt). Wave 2
    /// makes it a ledger transition with a concurrency token.
    /// </summary>
    internal bool TryConsume() => Interlocked.Exchange(ref _spent, 1) == 0;

    /// <summary>True once the receipt was spent (sent, or burnt by a channel).</summary>
    public bool IsSpent => Volatile.Read(ref _spent) != 0;
    public string Body { get; }
    public string Environment { get; }
    public string IdempotencyKey { get; }
    public string? ArtifactSha256 { get; }
    /// <summary>The second frozen file of a two-file submission (a bank header and body), so a receipt binds both files.</summary>
    public string? ArtifactSecondarySha256 { get; }
}

/// <summary>A ledger write that collides with another one (a reused key, a held batch, a bank id, a lost update): 409.</summary>
public sealed class GovernmentExchangeConflictException(string code, string message) : InvalidOperationException(message)
{
    public const int StatusCode = 409;
    public string Code { get; } = code;
}

/// <summary>The only writer of <c>government_exchanges</c> (ratchet: GovernmentRatchetTests D).</summary>
public interface IGovernmentExchangeRecorder
{
    /// <summary>
    /// Records an attempt BEFORE anything is sent, in its own unit of work (the caller's pending changes are neither saved nor
    /// discarded). Idempotent on (tenant, body, operation, idempotency key): the same key with the same content returns the first
    /// row with <c>Created = false</c>; the same key with different content, a batch already held by another open submission, or
    /// a row the caller cannot see is a <see cref="GovernmentExchangeConflictException"/> (409).
    /// </summary>
    Task<GovernmentExchangeRecordResult> BeginAsync(GovernmentExchangeAttempt attempt, CancellationToken ct);

    /// <summary>Moves an attempt on along the transition table (<see cref="GovernmentExchangeStates.Next"/>); anything else is refused.</summary>
    Task<GovernmentExchange> UpdateAsync(Guid tenantId, Guid exchangeId, GovernmentExchangeOutcome outcome, CancellationToken ct);

    /// <summary>
    /// The only way out of <see cref="GovernmentExchangeStates.Unknown"/> (architecture 3.5): a person other than the one who started
    /// the attempt records, with a reason, either "the bank has it" (with the bank's id: Accepted) or "the bank does not" (the intent
    /// is released and the batch may be submitted again).
    /// </summary>
    Task<GovernmentExchange> ResolveUnknownAsync(Guid tenantId, Guid exchangeId, string resolution, string? bankReference, string reason,
        RequestContext context, CancellationToken ct);
}

/// <summary>
/// Writes the attempt ledger in a SEPARATE unit of work (its own context and audit service, the request's user still applies), and
/// stages one audit row per write in the same save, so the row is sealed into the tenant's hash chain with the change it describes.
/// The audit row carries the money fields (artifact hashes, bank id, batch, amount) before and after. Enforces the honesty rule: a
/// state claiming the body has the exchange (<see cref="GovernmentExchangeStates.LiveOnly"/>) needs an adapter that declared itself
/// live, on the api channel; <c>IsLive</c> comes from the adapter, never from the caller.
/// </summary>
public sealed class GovernmentExchangeRecorder : IGovernmentExchangeRecorder
{
    public const string LiveStateRefusedCode = "government_live_state_refused";
    public const string KeyInUseCode = "exchange_key_in_use";
    public const string KeyReusedCode = "exchange_key_reused_with_different_content";
    public const string BatchHeldCode = "exchange_batch_already_submitted";
    public const string BankReferenceCode = "exchange_bank_reference_conflict";
    public const string IllegalTransitionCode = "exchange_illegal_transition";
    public const string ConcurrentUpdateCode = "exchange_concurrent_update";
    public const string ResolutionRefusedCode = "exchange_resolution_refused";
    public const int MaxIdempotencyKeyLength = 128;

    public const string CallerHasUnsavedChangesCode = "exchange_caller_has_unsaved_changes";
    /// <summary>An intent (or an outcome that follows a call) inside a caller's open transaction: refused, it would not be durable.</summary>
    public const string IntentMustBeCommittedCode = "exchange_intent_must_be_committed";
    private const string Savepoint = "gov_exchange_ledger";

    private readonly Func<(ZayraDbContext Db, IAuditService Audit, IAsyncDisposable Owner)> _open;
    private readonly ZayraDbContext? _ambient;
    private readonly IAuditService? _ambientAudit;

    /// <summary>
    /// Production. Outside a transaction each ledger write runs in a fresh DI scope (its own context and audit service), so the
    /// caller's pending changes are neither saved nor discarded. When the request's context has an open transaction, the write
    /// joins it under a SAVEPOINT (a separate connection could deadlock on the tenant's audit lock the caller already holds); it
    /// then refuses if the caller has unsaved changes, and a lost race rolls back to the savepoint, leaving the caller's work alone.
    /// </summary>
    public GovernmentExchangeRecorder(IServiceScopeFactory scopes, ZayraDbContext? ambient = null, IAuditService? ambientAudit = null)
    {
        _open = () =>
        {
            var scope = scopes.CreateAsyncScope();
            return (scope.ServiceProvider.GetRequiredService<ZayraDbContext>(), scope.ServiceProvider.GetRequiredService<IAuditService>(), scope);
        };
        _ambient = ambient;
        _ambientAudit = ambient is null ? null : ambientAudit ?? new AuditService(ambient);
    }

    /// <summary>Each ledger write runs in a fresh context from <paramref name="newContext"/>, or under a savepoint of <paramref name="ambient"/>'s open transaction.</summary>
    public GovernmentExchangeRecorder(Func<ZayraDbContext> newContext, ZayraDbContext? ambient = null)
    {
        _open = () =>
        {
            var db = newContext();
            return (db, new AuditService(db), db);
        };
        _ambient = ambient;
        _ambientAudit = ambient is null ? null : new AuditService(ambient);
    }

    /// <summary>
    /// Runs one ledger write: in the caller's open transaction under a savepoint, or else in a fresh unit of work. On a database
    /// conflict the write is rolled back (savepoint) or thrown away (fresh unit) and <paramref name="onConflict"/> runs in a clean state.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<ZayraDbContext, IAuditService, Task<T>> work,
        Func<ZayraDbContext, DbUpdateException, Task<T>>? onConflict, CancellationToken ct, bool mustBeDurable = false)
    {
        if (_ambient?.Database.CurrentTransaction is { } tx)
        {
            // An intent, or an outcome that follows a call, must be committed on its own BEFORE (or after) the call: inside the
            // caller's transaction a later rollback would erase the record of a call that was made. Refused, never written there.
            // This also means the recorder never opens a second connection while the caller holds the tenant's audit lock.
            if (mustBeDurable)
                throw new InvalidOperationException($"{IntentMustBeCommittedCode}: commit (or leave) your transaction, then record the intent; it must be durable before the call.");
            if (_ambient.ChangeTracker.HasChanges())
                throw new InvalidOperationException($"{CallerHasUnsavedChangesCode}: save your own changes before recording a government exchange in the same transaction.");
            await tx.CreateSavepointAsync(Savepoint, ct);
            try
            {
                var result = await work(_ambient, _ambientAudit!);
                await tx.ReleaseSavepointAsync(Savepoint, ct);
                return result;
            }
            catch (Exception ex)
            {
                await tx.RollbackToSavepointAsync(Savepoint, ct);
                // Only this write's entries can be pending (the caller had none), so this forgets nothing of the caller's.
                foreach (var entry in _ambient.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
                    entry.State = EntityState.Detached;
                if (ex is DbUpdateException conflict and not DbUpdateConcurrencyException && onConflict is not null) return await onConflict(_ambient, conflict);
                throw;
            }
        }

        DbUpdateException? lost = null;
        var (db, audit, owner) = _open();
        await using (owner)
        {
            try { return await work(db, audit); }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && onConflict is not null) { lost = ex; }
        }
        var (fresh, _, freshOwner) = _open();
        await using (freshOwner) return await onConflict!(fresh, lost!);
    }

    public async Task<GovernmentExchangeRecordResult> BeginAsync(GovernmentExchangeAttempt attempt, CancellationToken ct)
    {
        Validate(attempt);
        var isLive = attempt.Channel == GovernmentExchangeChannels.Api && attempt.Adapter?.IsLiveIntegration == true;
        RequireHonestState(attempt.State, isLive, attempt.Channel);
        if (!GovernmentExchangeStates.Initial.Contains(attempt.State, StringComparer.Ordinal))
            throw new GovernmentExchangeConflictException(IllegalTransitionCode, $"An attempt cannot start in state '{attempt.State}'.");
        if (attempt.Operation == GovernmentExchangeOperations.PayrollSubmit) ValidatePayrollSubmission(attempt);
        attempt = Normalise(attempt);

        var requestSha = Sha256(attempt.RequestBody);
        var isIntent = attempt.Channel == GovernmentExchangeChannels.Api && GovernmentExchangeStates.Intents.Contains(attempt.State, StringComparer.Ordinal);
        return await RunAsync(async (db, audit) =>
        {
            var existing = await FindByKeyAsync(db, attempt.TenantId, attempt.Body, attempt.Operation, attempt.IdempotencyKey, ct);
            if (existing is not null) return new GovernmentExchangeRecordResult(SameContentOrConflict(existing, attempt, requestSha), false);

            // The same key and the same filter as ux_government_exchanges__open_batch: the payment batch's id, in this environment, on
            // ANY channel. The database holds it for real (a race, a raw insert); this check only gives the caller the clear answer.
            if (HoldsAPaymentBatch(attempt))
            {
                var holding = GovernmentExchangeStates.HoldsTheBatch.ToArray();
                if (await db.GovernmentExchanges.AsNoTracking().AnyAsync(x => x.TenantId == attempt.TenantId && x.Body == attempt.Body
                        && x.Environment == attempt.Environment && x.Operation == GovernmentExchangeOperations.PayrollSubmit
                        && x.SubjectType == GovernmentExchangeSubjects.PaymentBatch && x.SubjectId == attempt.SubjectId
                        && holding.Contains(x.State), ct))
                    throw new GovernmentExchangeConflictException(BatchHeldCode, "This payment batch already has an open or completed submission.");
            }

            var row = NewRow(attempt, requestSha, isLive);
            db.GovernmentExchanges.Add(row);
            Stage(audit, "government.exchange.recorded", null, row, attempt.Context);
            await db.SaveChangesAsync(ct);
            // Committed in this unit of work (an intent is never written inside a caller's transaction), so it is durable now.
            var receipt = isIntent
                ? new GovernmentExchangeIntentReceipt(row.Id, row.TenantId, row.Body, row.Environment!, row.IdempotencyKey, row.ArtifactSha256,
                    row.ArtifactSecondarySha256, row.CompanyId)
                : null;
            return new GovernmentExchangeRecordResult(row, true, receipt);
        },
        // Lost a race on a unique key. If the caller can see the winner and it carries the same content, hand it back; otherwise it
        // is a conflict (another company's row, another submission of the batch, another payload), never a 500.
        async (fresh, lost) =>
        {
            var winner = await FindByKeyAsync(fresh, attempt.TenantId, attempt.Body, attempt.Operation, attempt.IdempotencyKey, ct);
            if (winner is null)
                // Another submission of this payment batch won the race (the batch lock fired), or the key is held by a row this caller
                // cannot see (another company's): either way a conflict, never a 500.
                throw HoldsAPaymentBatch(attempt) && IsBatchLock(lost)
                    ? new GovernmentExchangeConflictException(BatchHeldCode, "This payment batch already has an open or completed submission.")
                    : new GovernmentExchangeConflictException(KeyInUseCode, "This attempt collides with another exchange record.");
            return new GovernmentExchangeRecordResult(SameContentOrConflict(winner, attempt, requestSha), false);
        }, ct, mustBeDurable: isIntent);
    }

    private static GovernmentExchange NewRow(GovernmentExchangeAttempt attempt, string? requestSha, bool isLive) => new()
    {
        TenantId = attempt.TenantId,
        CompanyId = attempt.CompanyId,
        Body = attempt.Body,
        Operation = attempt.Operation,
        Channel = attempt.Channel,
        Environment = attempt.Environment,
        SubjectType = attempt.SubjectType,
        SubjectId = attempt.SubjectId,
        IdempotencyKey = attempt.IdempotencyKey,
        State = attempt.State,
        RequestSha256 = requestSha,
        ArtifactSha256 = attempt.ArtifactSha256?.ToLowerInvariant(),
        ArtifactSecondarySha256 = attempt.ArtifactSecondarySha256?.ToLowerInvariant(),
        AccountFingerprint = attempt.AccountFingerprint,
        BatchReference = attempt.BatchReference,
        TotalAmount = attempt.TotalAmount,
        ItemCount = attempt.ItemCount,
        MaskedPreview = GovernmentLogScrubber.Preview(attempt.PreviewText),
        Adapter = attempt.Adapter?.AdapterName ?? attempt.Channel,
        IsLive = isLive,
        ActorUserId = attempt.Context.UserId,
        ApprovalRequestId = attempt.ApprovalRequestId,
        JobId = attempt.JobId,
        CreatedAtUtc = DateTime.UtcNow,
    };

    public async Task<GovernmentExchange> UpdateAsync(Guid tenantId, Guid exchangeId, GovernmentExchangeOutcome outcome, CancellationToken ct)
    {
        if (!GovernmentExchangeStates.IsKnown(outcome.State))
            throw new ArgumentException($"Unknown exchange state '{outcome.State}'.", nameof(outcome));
        return await RunAsync(async (db, audit) =>
        {
        var row = await db.GovernmentExchanges.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == exchangeId, ct)
            ?? throw new KeyNotFoundException("Government exchange not found.");
        RequireHonestState(outcome.State, row.IsLive, row.Channel);
        if (row.CompletedAtUtc is not null || !GovernmentExchangeStates.CanMove(row.State, outcome.State))
            throw new GovernmentExchangeConflictException(IllegalTransitionCode, $"An attempt in state '{row.State}' cannot move to '{outcome.State}'.");
        if (outcome.ExternalReference is { } reference)
        {
            if (row.ExternalReference is { } held && held != reference)
                throw new GovernmentExchangeConflictException(BankReferenceCode, "The body's reference for this attempt is already recorded and cannot change.");
            if (await db.GovernmentExchanges.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Body == row.Body
                    && x.Environment == row.Environment && x.ExternalReference == reference && x.Id != row.Id, ct))
                throw new GovernmentExchangeConflictException(BankReferenceCode, "The body's reference already belongs to another attempt.");
        }

        var before = Snapshot(row);
        row.State = outcome.State;
        row.HttpStatus = outcome.HttpStatus ?? row.HttpStatus;
        row.ResponseSha256 = Sha256(outcome.ResponseBody) ?? row.ResponseSha256;
        row.MaskedPreview = GovernmentLogScrubber.Preview(outcome.PreviewText) ?? row.MaskedPreview;
        row.ErrorCode = outcome.ErrorCode is null ? row.ErrorCode : Truncate(GovernmentLogScrubber.Mask(outcome.ErrorCode), 64);
        row.ExternalReference = outcome.ExternalReference ?? row.ExternalReference;
        row.UpdatedAtUtc = DateTime.UtcNow;
        if (outcome.IsPoll) { row.PollCount++; row.LastPolledAtUtc = row.UpdatedAtUtc; }
        if (outcome.Completed) row.CompletedAtUtc ??= row.UpdatedAtUtc;
        row.StateVersion++;
        Stage(audit, "government.exchange.updated", before, row, outcome.Context);
        await SaveOrConflictAsync(db, ct);
        return row;
        }, null, ct, mustBeDurable: true);
    }

    public async Task<GovernmentExchange> ResolveUnknownAsync(Guid tenantId, Guid exchangeId, string resolution, string? bankReference, string reason,
        RequestContext context, CancellationToken ct)
    {
        if (resolution is not (GovernmentExchangeResolutions.BankHasIt or GovernmentExchangeResolutions.BankDoesNot))
            throw new ArgumentException("The resolution is bank_has_it or bank_does_not.", nameof(resolution));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ArgumentException("A reason of at most 500 characters is required.", nameof(reason));
        return await RunAsync(async (db, audit) =>
        {
        var row = await db.GovernmentExchanges.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == exchangeId, ct)
            ?? throw new KeyNotFoundException("Government exchange not found.");
        if (row.State != GovernmentExchangeStates.Unknown)
            throw new GovernmentExchangeConflictException(IllegalTransitionCode, $"Only an Unknown outcome can be resolved; this one is '{row.State}'.");
        if (context.UserId is not Guid resolver || resolver == row.ActorUserId)
            throw new GovernmentExchangeConflictException(ResolutionRefusedCode, "An Unknown outcome is resolved by a second person, not the one who started it.");
        var target = resolution == GovernmentExchangeResolutions.BankHasIt ? GovernmentExchangeStates.Accepted : GovernmentExchangeStates.ReleasedAfterUnknown;
        RequireHonestState(target, row.IsLive, row.Channel);
        if (target == GovernmentExchangeStates.Accepted)
        {
            if (string.IsNullOrWhiteSpace(bankReference))
                throw new GovernmentExchangeConflictException(ResolutionRefusedCode, "\"The bank has it\" needs the bank's id for the batch.");
            if (await db.GovernmentExchanges.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Body == row.Body
                    && x.Environment == row.Environment && x.ExternalReference == bankReference && x.Id != row.Id, ct))
                throw new GovernmentExchangeConflictException(BankReferenceCode, "The body's reference already belongs to another attempt.");
        }

        var before = Snapshot(row);
        row.State = target;
        row.ExternalReference = target == GovernmentExchangeStates.Accepted ? bankReference : row.ExternalReference;
        row.Resolution = resolution;
        row.ResolutionReason = reason.Trim();
        row.ResolvedByUserId = resolver;
        row.ResolvedAtUtc = DateTime.UtcNow;
        row.UpdatedAtUtc = row.ResolvedAtUtc;
        if (target == GovernmentExchangeStates.ReleasedAfterUnknown) row.CompletedAtUtc = row.ResolvedAtUtc;
        row.StateVersion++;
        Stage(audit, "government.exchange.unknown_resolved", before, row, context);
        await SaveOrConflictAsync(db, ct);
        return row;
        }, null, ct, mustBeDurable: true);
    }

    private static async Task SaveOrConflictAsync(ZayraDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new GovernmentExchangeConflictException(ConcurrentUpdateCode, "Another writer moved this attempt first; read it again."); }
        catch (DbUpdateException) { throw new GovernmentExchangeConflictException(BankReferenceCode, "The change collides with another exchange record."); }
    }

    private static GovernmentExchange SameContentOrConflict(GovernmentExchange existing, GovernmentExchangeAttempt attempt, string? requestSha)
    {
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (!Same(existing.RequestSha256, requestSha) || !Same(existing.ArtifactSha256, attempt.ArtifactSha256)
            || !Same(existing.ArtifactSecondarySha256, attempt.ArtifactSecondarySha256)
            || existing.SubjectType != attempt.SubjectType || existing.SubjectId != attempt.SubjectId || existing.CompanyId != attempt.CompanyId
            || existing.AccountFingerprint != attempt.AccountFingerprint || existing.BatchReference != attempt.BatchReference
            || existing.TotalAmount != attempt.TotalAmount)
            throw new GovernmentExchangeConflictException(KeyReusedCode, "This idempotency key was already used for a different exchange.");
        return existing;
    }

    private static Task<GovernmentExchange?> FindByKeyAsync(ZayraDbContext db, Guid tenantId, string body, string operation, string key, CancellationToken ct) =>
        db.GovernmentExchanges.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Body == body && x.Operation == operation && x.IdempotencyKey == key, ct);

    private static object Snapshot(GovernmentExchange row) => new
    {
        state = row.State, stateVersion = row.StateVersion, externalReference = row.ExternalReference, httpStatus = row.HttpStatus,
        errorCode = row.ErrorCode, responseSha256 = row.ResponseSha256, resolution = row.Resolution, resolvedBy = row.ResolvedByUserId,
    };

    /// <summary>The sealed audit row: the identity and money fields of the attempt, and the state before and after.</summary>
    private static void Stage(IAuditService audit, string action, object? before, GovernmentExchange row, RequestContext context) =>
        audit.Stage(action, nameof(GovernmentExchange), row.Id.ToString(),
            context with { TenantId = row.TenantId },
            JsonSerializer.Serialize(new
            {
                body = row.Body, operation = row.Operation, channel = row.Channel, environment = row.Environment, isLive = row.IsLive,
                adapter = row.Adapter, idempotencyKey = row.IdempotencyKey, subjectType = row.SubjectType, subjectId = row.SubjectId,
                approvalRequestId = row.ApprovalRequestId, requestSha256 = row.RequestSha256, artifactSha256 = row.ArtifactSha256,
                artifactSecondarySha256 = row.ArtifactSecondarySha256, accountFingerprint = row.AccountFingerprint,
                batchReference = row.BatchReference, totalAmount = row.TotalAmount, itemCount = row.ItemCount,
                before, after = Snapshot(row), resolutionReason = row.ResolutionReason,
            }),
            row.CompanyId);

    private static void RequireHonestState(string state, bool isLive, string channel)
    {
        if (GovernmentExchangeStates.LiveOnly.Contains(state, StringComparer.Ordinal)
            && !(isLive && channel == GovernmentExchangeChannels.Api))
            throw new InvalidOperationException(
                $"{LiveStateRefusedCode}: '{state}' says the body has the exchange, and only a live adapter on the api channel may record that.");
    }

    private static void Validate(GovernmentExchangeAttempt a)
    {
        if (a.TenantId == Guid.Empty) throw new ArgumentException("A tenant is required.", nameof(a));
        if (!GovernmentBodies.IsLedgerBody(a.Body)) throw new ArgumentException($"Unknown body '{a.Body}'.", nameof(a));
        if (!GovernmentBodies.IsKnown(a.Body) && a.Channel == GovernmentExchangeChannels.Api)
            throw new ArgumentException($"The ledger-only body '{a.Body}' is recorded on a file channel, never api.", nameof(a));
        if (!GovernmentExchangeChannels.All.Contains(a.Channel, StringComparer.Ordinal)) throw new ArgumentException($"Unknown channel '{a.Channel}'.", nameof(a));
        if (!GovernmentExchangeStates.IsKnown(a.State)) throw new ArgumentException($"Unknown state '{a.State}'.", nameof(a));
        if (string.IsNullOrWhiteSpace(a.Operation) || a.Operation.Length > 64) throw new ArgumentException("An operation of at most 64 characters is required.", nameof(a));
        if (string.IsNullOrWhiteSpace(a.IdempotencyKey) || a.IdempotencyKey.Length > MaxIdempotencyKeyLength)
            throw new ArgumentException($"An idempotency key of at most {MaxIdempotencyKeyLength} characters is required.", nameof(a));
        if (a.Environment is not null && !GovernmentEnvironments.IsKnown(a.Environment)) throw new ArgumentException($"Unknown environment '{a.Environment}'.", nameof(a));
        if (a.Channel == GovernmentExchangeChannels.Api && a.Environment is null) throw new ArgumentException("An api attempt names its environment.", nameof(a));
        foreach (var hash in new[] { a.ArtifactSha256, a.ArtifactSecondarySha256 })
            if (hash is not null && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
                throw new ArgumentException("An artifact hash must be a SHA-256 hex string.", nameof(a));
        if (a.AccountFingerprint is { Length: > 64 } || a.BatchReference is { Length: > 64 }) throw new ArgumentException("The account fingerprint and batch reference are at most 64 characters.", nameof(a));
        if (a.State == GovernmentExchangeStates.SubmitIntended
            && (a.ArtifactSha256 is null || a.SubjectId is null || a.CompanyId is null || a.AccountFingerprint is null || a.BatchReference is null))
            throw new ArgumentException("A submission intent names the frozen artifact, the subject, the company, the account and the batch.", nameof(a));
        // A submission intent is a payroll submission, whose lock is the payment batch: under any other operation it would hold nothing.
        if (a.State == GovernmentExchangeStates.SubmitIntended && a.Operation != GovernmentExchangeOperations.PayrollSubmit)
            throw new ArgumentException($"A submission intent is recorded under the '{GovernmentExchangeOperations.PayrollSubmit}' operation only.", nameof(a));
    }

    /// <summary>
    /// A payroll submission names the payment batch by id (the lock is that id), is a bank body's other than the file-only bank, is
    /// not a plain attempt (it would earn a receipt outside the lock), and is only ever recorded on the api channel in sandbox or
    /// production, and on any other channel (a person's manual record or a file) in production only.
    /// </summary>
    private static void ValidatePayrollSubmission(GovernmentExchangeAttempt a)
    {
        if (a.SubjectType != GovernmentExchangeSubjects.PaymentBatch || !Guid.TryParse(a.SubjectId, out var batch) || batch == Guid.Empty)
            throw new ArgumentException("A payroll submission names its payment batch by id (subject type PaymentBatch, a Guid).", nameof(a));
        if (!GovernmentBodies.IsBank(a.Body) || a.Body == GovernmentBodies.BankFile)
            throw new ArgumentException($"A payroll submission goes to a bank body, not '{a.Body}'.", nameof(a));
        if (a.State == GovernmentExchangeStates.Attempted)
            throw new ArgumentException("A payroll submission cannot start as a plain attempt.", nameof(a));
        if (a.Channel != GovernmentExchangeChannels.Api && a.Environment is { } environment && environment != GovernmentEnvironments.Production)
            throw new ArgumentException("A manual or file submission is always production; sandbox is the api channel's.", nameof(a));
    }

    /// <summary>The attempt as it is stored: a payroll submission's batch id in its canonical form, and a non-api one in production.</summary>
    private static GovernmentExchangeAttempt Normalise(GovernmentExchangeAttempt a)
    {
        if (a.Operation != GovernmentExchangeOperations.PayrollSubmit) return a;
        return a with
        {
            SubjectId = Guid.Parse(a.SubjectId!).ToString("D"),
            Environment = a.Channel == GovernmentExchangeChannels.Api ? a.Environment : GovernmentEnvironments.Production,
        };
    }

    /// <summary>The unique violation is the payment-batch lock's (Postgres names the constraint; a provider that does not is not assumed to).</summary>
    private static bool IsBatchLock(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is Npgsql.PostgresException { ConstraintName: BatchLockIndex }) return true;
        return false;
    }

    /// <summary>The unique index that holds one open submission per payment batch.</summary>
    private const string BatchLockIndex = "ux_government_exchanges__open_batch";

    /// <summary>True when this attempt would sit in the batch lock (<c>ux_government_exchanges__open_batch</c>): a payroll submission in a holding state.</summary>
    private static bool HoldsAPaymentBatch(GovernmentExchangeAttempt a) =>
        a.Operation == GovernmentExchangeOperations.PayrollSubmit && a.SubjectType == GovernmentExchangeSubjects.PaymentBatch && a.SubjectId is not null
        && a.Body != GovernmentBodies.BankFile && GovernmentExchangeStates.HoldsTheBatch.Contains(a.State, StringComparer.Ordinal);

    private static string? Sha256(byte[]? bytes) =>
        bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
