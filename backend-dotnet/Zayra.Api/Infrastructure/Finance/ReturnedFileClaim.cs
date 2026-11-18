using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>What the import asks to claim: the returned file's company-scoped FILE-REF key and its canonical content hash, for one batch.</summary>
public sealed record ReturnedFileClaimRequest(
    Guid TenantId, Guid? CompanyId, Guid PaymentBatchId, string ImportKey, string ContentSha256, Guid? ActorId);

/// <summary>The claimed ledger row (<see cref="ClaimId"/>), or why the file was refused (<see cref="RefusalCode"/>, 409).</summary>
public sealed record ReturnedFileClaimOutcome(Guid? ClaimId, string? RefusalCode = null, string? Message = null)
{
    public bool Claimed => ClaimId is not null;
}

/// <summary>Carried on the import result from the claim to the save, never serialised.</summary>
public sealed record ReturnedFileClaimTicket(Guid TenantId, Guid ClaimId, Guid? ActorId);

/// <summary>
/// Claims a bank-returned file in the government exchange ledger BEFORE the import applies anything, so a FILE-REF belongs to one
/// payment batch and one file is applied once, even under concurrent imports. See <see cref="ReturnedFileClaim"/>.
/// </summary>
public interface IReturnedFileClaim
{
    Task<ReturnedFileClaimOutcome> ClaimAsync(ReturnedFileClaimRequest request, CancellationToken ct);

    /// <summary>After the import's save: <paramref name="saved"/> lands the claim (Recorded); false releases it (Failed) for a retry.</summary>
    Task SettleAsync(ReturnedFileClaimTicket ticket, bool saved, CancellationToken ct);
}

/// <summary>
/// Two ledger rows per import, written through <see cref="IGovernmentExchangeRecorder"/> (body <c>bank_file</c>, channel
/// <c>file_in</c>, environment <c>production</c>, subject = the payment batch). The recorder's unique key
/// (tenant, body, operation, idempotency key) is what makes them race-safe:
/// <list type="bullet">
/// <item><b>Bind</b> (<see cref="BindOperation"/>, key = company|FILE-REF, state Recorded, permanent): the reference belongs to the
/// first batch it was claimed for. The same reference on another batch is a key reused with different content: refused, 409. The
/// bank's later status file for the SAME batch matches the row and goes on.</item>
/// <item><b>Replay</b> (<see cref="ImportOperation"/>, key = company|FILE-REF|content-hash#attempt, state Attempted, artifact = the
/// hash): exactly one import of one file wins the row. A second finds it Attempted (in flight, 409) or Recorded (already imported,
/// 409). A row the save released (Failed), or one left Attempted for longer than <see cref="StaleAfter"/> by a crashed request, is
/// passed over: the next attempt number is claimed, again by exactly one racer.</item>
/// </list>
/// A failed import keeps its reference bound to the batch it was aimed at; importing the same reference for another batch then needs a
/// new reference from the bank (the same rule the reader's own FILE-REF memory applies).
/// </summary>
public sealed class ReturnedFileClaim(IGovernmentExchangeRecorder recorder) : IReturnedFileClaim
{
    public const string BindOperation = "returned_file.bind";
    public const string ImportOperation = "returned_file.import";
    public const string BoundToOtherBatchCode = "file_ref_bound_to_other_batch";
    public const string InProgressCode = "returned_file_import_in_progress";
    public const string AlreadyImportedCode = "returned_file_already_imported";
    /// <summary>409 at save: the claim this import held was taken over (or is too old to be safe), so it applied nothing.</summary>
    public const string TakenOverCode = "returned_file_claim_taken_over";
    public const string SubjectType = "PaymentBatch";
    public const int MaxAttempts = 20;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    /// <summary>A claim older than <see cref="StaleAfter"/> less this is no longer safe to save under: a takeover could start during the
    /// save. Far longer than any save (the command timeout is 30 seconds).</summary>
    public static readonly TimeSpan FenceMargin = TimeSpan.FromMinutes(2);

    /// <summary>The replay key without its "#attempt" suffix, plus the '#': every attempt at the same file starts with it.</summary>
    public static string AttemptPrefix(string replayKey)
    {
        var hash = replayKey.LastIndexOf('#');
        return (hash < 0 ? replayKey : replayKey[..hash]) + "#";
    }

    public async Task<ReturnedFileClaimOutcome> ClaimAsync(ReturnedFileClaimRequest request, CancellationToken ct)
    {
        var context = Context(request.TenantId, request.ActorId);
        try
        {
            await recorder.BeginAsync(Attempt(request, BindOperation, request.ImportKey, GovernmentExchangeStates.Recorded, null, context), ct);
        }
        catch (GovernmentExchangeConflictException)
        {
            return BoundElsewhere();
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            GovernmentExchangeRecordResult claim;
            try
            {
                claim = await recorder.BeginAsync(Attempt(request, ImportOperation, $"{request.ImportKey}|{request.ContentSha256}#{attempt}",
                    GovernmentExchangeStates.Attempted, request.ContentSha256, context), ct);
            }
            catch (GovernmentExchangeConflictException)
            {
                return BoundElsewhere();
            }
            if (claim.Created) return new ReturnedFileClaimOutcome(claim.Exchange.Id);

            var prior = claim.Exchange;
            var abandoned = prior.State == GovernmentExchangeStates.Attempted && prior.CreatedAtUtc < DateTime.UtcNow - StaleAfter;
            if (prior.State == GovernmentExchangeStates.Failed || abandoned) continue;
            return prior.State == GovernmentExchangeStates.Attempted
                ? new ReturnedFileClaimOutcome(null, InProgressCode,
                    "This file is being imported for this payment batch right now. Wait for that import to finish, then check the batch.")
                : new ReturnedFileClaimOutcome(null, AlreadyImportedCode,
                    "This exact file was already imported for this payment batch. Nothing was applied again.");
        }
        return new ReturnedFileClaimOutcome(null, InProgressCode,
            "This file has been tried too many times. Ask an administrator to check the earlier imports before trying again.");
    }

    public Task SettleAsync(ReturnedFileClaimTicket ticket, bool saved, CancellationToken ct) =>
        recorder.UpdateAsync(ticket.TenantId, ticket.ClaimId, new GovernmentExchangeOutcome(
            saved ? GovernmentExchangeStates.Recorded : GovernmentExchangeStates.Failed, Context(ticket.TenantId, ticket.ActorId),
            ErrorCode: saved ? null : "import_save_failed", Completed: true), ct);

    private static ReturnedFileClaimOutcome BoundElsewhere() => new(null, BoundToOtherBatchCode,
        "This bank file reference (FILE-REF) is already tied to another payment batch of this company, so this file cannot belong to this "
      + "batch. If the bank re-issued the file, ask it for a new reference. Nothing was applied.");

    private static GovernmentExchangeAttempt Attempt(ReturnedFileClaimRequest r, string operation, string key, string state, string? artifact,
        RequestContext context) =>
        new(r.TenantId, r.CompanyId, GovernmentBodies.BankFile, operation, GovernmentExchangeChannels.FileIn, key, state, context,
            Environment: GovernmentEnvironments.Production, SubjectType: SubjectType, SubjectId: r.PaymentBatchId.ToString(),
            ArtifactSha256: artifact);

    private static RequestContext Context(Guid tenantId, Guid? actorId) => new("server", "bank-confirmation-import", actorId, tenantId);
}
