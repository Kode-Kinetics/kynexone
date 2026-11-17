using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
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
    Guid? JobId = null);

/// <summary>How an attempt ended (or moved on). The response body is hashed, never stored.</summary>
public sealed record GovernmentExchangeOutcome(
    string State,
    RequestContext Context,
    int? HttpStatus = null,
    byte[]? ResponseBody = null,
    string? PreviewText = null,
    string? ErrorCode = null,
    string? ExternalReference = null,
    bool Completed = false);

/// <summary>The ledger row, and whether this call created it (false: the idempotency key had already been used).</summary>
public sealed record GovernmentExchangeRecordResult(GovernmentExchange Exchange, bool Created);

/// <summary>The only writer of <c>government_exchanges</c> (ratchet: GovernmentLedgerWritesRatchetTests).</summary>
public interface IGovernmentExchangeRecorder
{
    /// <summary>
    /// Records an attempt BEFORE anything is sent. Idempotent on (tenant, body, operation, idempotency key): a second call with
    /// the same key returns the first row with <c>Created = false</c> and writes nothing, also when two calls race.
    /// </summary>
    Task<GovernmentExchangeRecordResult> BeginAsync(GovernmentExchangeAttempt attempt, CancellationToken ct);

    /// <summary>Moves an attempt to its next state (with the response hash, HTTP status, masked preview).</summary>
    Task<GovernmentExchange> UpdateAsync(Guid tenantId, Guid exchangeId, GovernmentExchangeOutcome outcome, CancellationToken ct);
}

/// <summary>
/// Writes the attempt ledger and stages one audit row per write in the same SaveChanges, so the row is sealed into the tenant's
/// hash chain with the change it describes (<c>IAuditService.Stage</c>). Enforces the honesty rule: a state that claims the
/// body has the exchange (<see cref="GovernmentExchangeStates.LiveOnly"/>) is refused unless the attempt went through an
/// adapter that declared itself live, on the api channel. <c>IsLive</c> is taken from the adapter, never from the caller.
/// </summary>
public sealed class GovernmentExchangeRecorder(ZayraDbContext db, IAuditService audit) : IGovernmentExchangeRecorder
{
    public const string LiveStateRefusedCode = "government_live_state_refused";
    public const int MaxIdempotencyKeyLength = 128;

    public async Task<GovernmentExchangeRecordResult> BeginAsync(GovernmentExchangeAttempt attempt, CancellationToken ct)
    {
        Validate(attempt);
        var existing = await FindAsync(attempt.TenantId, attempt.Body, attempt.Operation, attempt.IdempotencyKey, ct);
        if (existing is not null) return new GovernmentExchangeRecordResult(existing, false);

        var isLive = attempt.Channel == GovernmentExchangeChannels.Api && attempt.Adapter?.IsLiveIntegration == true;
        RequireHonestState(attempt.State, isLive, attempt.Channel);

        var row = new GovernmentExchange
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
            RequestSha256 = Sha256(attempt.RequestBody),
            ArtifactSha256 = attempt.ArtifactSha256?.ToLowerInvariant(),
            MaskedPreview = GovernmentLogScrubber.Preview(attempt.PreviewText),
            Adapter = attempt.Adapter?.AdapterName ?? attempt.Channel,
            IsLive = isLive,
            ActorUserId = attempt.Context.UserId,
            ApprovalRequestId = attempt.ApprovalRequestId,
            JobId = attempt.JobId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.GovernmentExchanges.Add(row);
        Stage("government.exchange.recorded", row, attempt.Context);
        try
        {
            await db.SaveChangesAsync(ct);
            return new GovernmentExchangeRecordResult(row, true);
        }
        catch (DbUpdateException)
        {
            // Lost a race on the unique key: forget our row and its audit, and hand back the winner's.
            Forget(row);
            var winner = await FindAsync(attempt.TenantId, attempt.Body, attempt.Operation, attempt.IdempotencyKey, ct);
            if (winner is null) throw;
            return new GovernmentExchangeRecordResult(winner, false);
        }
        catch
        {
            // Refused (a company or tenant write guard, a check): nothing of this attempt may linger in the shared context.
            Forget(row);
            throw;
        }
    }

    private void Forget(GovernmentExchange row)
    {
        audit.DiscardStaged();
        db.Entry(row).State = EntityState.Detached;
    }

    public async Task<GovernmentExchange> UpdateAsync(Guid tenantId, Guid exchangeId, GovernmentExchangeOutcome outcome, CancellationToken ct)
    {
        if (!GovernmentExchangeStates.IsKnown(outcome.State))
            throw new ArgumentException($"Unknown exchange state '{outcome.State}'.", nameof(outcome));
        var row = await db.GovernmentExchanges.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == exchangeId, ct)
            ?? throw new KeyNotFoundException("Government exchange not found.");
        RequireHonestState(outcome.State, row.IsLive, row.Channel);

        row.State = outcome.State;
        row.HttpStatus = outcome.HttpStatus ?? row.HttpStatus;
        row.ResponseSha256 = Sha256(outcome.ResponseBody) ?? row.ResponseSha256;
        row.MaskedPreview = GovernmentLogScrubber.Preview(outcome.PreviewText) ?? row.MaskedPreview;
        row.ErrorCode = outcome.ErrorCode ?? row.ErrorCode;
        row.ExternalReference = outcome.ExternalReference ?? row.ExternalReference;
        row.UpdatedAtUtc = DateTime.UtcNow;
        if (outcome.Completed) row.CompletedAtUtc ??= row.UpdatedAtUtc;
        Stage("government.exchange.updated", row, outcome.Context);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            audit.DiscardStaged();
            var entry = db.Entry(row);
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
            throw;
        }
        return row;
    }

    private Task<GovernmentExchange?> FindAsync(Guid tenantId, string body, string operation, string key, CancellationToken ct) =>
        db.GovernmentExchanges.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Body == body && x.Operation == operation && x.IdempotencyKey == key, ct);

    private void Stage(string action, GovernmentExchange row, RequestContext context) =>
        audit.Stage(action, nameof(GovernmentExchange), row.Id.ToString(),
            context with { TenantId = row.TenantId },
            JsonSerializer.Serialize(new
            {
                body = row.Body, operation = row.Operation, channel = row.Channel, state = row.State,
                isLive = row.IsLive, adapter = row.Adapter, httpStatus = row.HttpStatus, errorCode = row.ErrorCode,
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
        if (!GovernmentBodies.IsKnown(a.Body)) throw new ArgumentException($"Unknown body '{a.Body}'.", nameof(a));
        if (!GovernmentExchangeChannels.All.Contains(a.Channel, StringComparer.Ordinal)) throw new ArgumentException($"Unknown channel '{a.Channel}'.", nameof(a));
        if (!GovernmentExchangeStates.IsKnown(a.State)) throw new ArgumentException($"Unknown state '{a.State}'.", nameof(a));
        if (string.IsNullOrWhiteSpace(a.Operation) || a.Operation.Length > 64) throw new ArgumentException("An operation of at most 64 characters is required.", nameof(a));
        if (string.IsNullOrWhiteSpace(a.IdempotencyKey) || a.IdempotencyKey.Length > MaxIdempotencyKeyLength)
            throw new ArgumentException($"An idempotency key of at most {MaxIdempotencyKeyLength} characters is required.", nameof(a));
        if (a.Environment is not null && !GovernmentEnvironments.IsKnown(a.Environment)) throw new ArgumentException($"Unknown environment '{a.Environment}'.", nameof(a));
        if (a.ArtifactSha256 is not null && (a.ArtifactSha256.Length != 64 || !a.ArtifactSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("The artifact hash must be a SHA-256 hex string.", nameof(a));
    }

    private static string? Sha256(byte[]? bytes) =>
        bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
