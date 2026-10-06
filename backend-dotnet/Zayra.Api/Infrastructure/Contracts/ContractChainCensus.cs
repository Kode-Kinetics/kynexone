using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>How a term joins the chain before it.</summary>
public static class ChainLinkKinds
{
    /// <summary>The first term of the chain: renewal_number 0, the chain starts on its start date.</summary>
    public const string Original = "Original";
    /// <summary>Starts the day after an earlier term ended: renewal_number + 1, same chain start (Art. 56).</summary>
    public const string Renewal = "Renewal";
    /// <summary>A new version of the same term (supersede inside the term): same renewal number and chain start.</summary>
    public const string Amendment = "Amendment";
    /// <summary>HR stated the history (chain/confirm); kept as recorded.</summary>
    public const string Recorded = "Recorded";
    /// <summary>Could not be linked; nothing is assumed (case opens NeedsConfirmation).</summary>
    public const string Unconfirmed = "Unconfirmed";
}

/// <summary>Why a term could not be linked. Plain-language mapping lives in the UI (renewals strings).</summary>
public static class ChainGapReasons
{
    /// <summary>An earlier term exists but this one does not start the day after it ended.</summary>
    public const string GapOrOverlap = "GapOrOverlap";
    /// <summary>The earlier term itself is unconfirmed, so this one cannot be counted.</summary>
    public const string PredecessorUnconfirmed = "PredecessorUnconfirmed";
    /// <summary>The first term on file starts after the employee joined: earlier terms are not on file.</summary>
    public const string EarlierTermsNotOnFile = "EarlierTermsNotOnFile";
    /// <summary>Two later terms both claim the same earlier term.</summary>
    public const string AmbiguousSuccessor = "AmbiguousSuccessor";
}

/// <summary>The contract facts the linker reads. One per row of employee_contracts for one employee.</summary>
public sealed record ContractChainFacts(
    Guid Id,
    string Status,
    DateOnly StartDate,
    DateOnly? EndDate,
    int Version,
    Guid? PreviousVersionId,
    Guid? RenewedFromContractId,
    short? RenewalNumber,
    DateOnly? ChainStartedOn,
    string? WorkerNationalityClass,
    DateTime CreatedAtUtc)
{
    public static ContractChainFacts Of(EmployeeContract c) => new(c.Id, c.Status, c.StartDate, c.EndDate, c.Version,
        c.PreviousVersionId, c.RenewedFromContractId, c.RenewalNumber, c.ChainStartedOn, c.WorkerNationalityClass, c.CreatedAtUtc);
}

/// <summary>What the linker concludes for one term. NULL fields stay NULL on the row: nothing is assumed.</summary>
public sealed record ChainStamp(
    Guid ContractId,
    string LinkKind,
    Guid? LinkedToContractId,
    Guid? RenewedFromContractId,
    short? RenewalNumber,
    DateOnly? ChainStartedOn,
    string? WorkerNationalityClass,
    string? GapReason)
{
    public bool IsConfirmed => RenewalNumber is not null && ChainStartedOn is not null;
}

/// <summary>The worker's nationality class from the employee record, or NULL when it cannot be stated safely.</summary>
public static class WorkerNationality
{
    /// <summary>
    /// Saudi / NonSaudi from the employee's declared Qiwa class and recorded nationality. NULL — "confirm it" — when
    /// neither is recorded, when they disagree, or when the worker is a GCC national: GCC nationals have their own
    /// treatment in several Saudi rules, so the class that Article 37 / 55 turns on is confirmed by HR, not guessed.
    /// </summary>
    public static string? ClassOf(string? declared, string? nationality)
    {
        var fromDeclared = declared?.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant() switch
        {
            "SAUDI" => WorkerNationalityClasses.Saudi,
            "NONSAUDI" => WorkerNationalityClasses.NonSaudi,
            _ => null,
        };
        string? fromNationality = null;
        if (!string.IsNullOrWhiteSpace(nationality))
        {
            var gosi = GosiCalculationService.DeriveClassification(nationality);
            if (gosi == GosiClassifications.GCC) return null;
            fromNationality = gosi == GosiClassifications.Saudi ? WorkerNationalityClasses.Saudi : WorkerNationalityClasses.NonSaudi;
        }
        if (fromDeclared is not null && fromNationality is not null && fromDeclared != fromNationality) return null;
        return fromDeclared ?? fromNationality;
    }
}

/// <summary>
/// The pure chain rule (plan §2 R4, rev 8.3.2 §6). Given every row of one employee's contracts it derives, for each
/// TERM (a contract that was ever in force: Active, Expired, Terminated or Superseded), its place in the chain:
/// <list type="bullet">
/// <item>Values already on the row (stamped earlier or recorded by HR) are kept exactly as they are.</item>
/// <item>A supersede version that starts inside its predecessor is an <see cref="ChainLinkKinds.Amendment"/>: same
///   renewal number, same chain start, no renewed_from (a term is renewed at most once — UNIQUE).</item>
/// <item>A term that starts the day after an earlier term ended (by supersede link or by dates) is a
///   <see cref="ChainLinkKinds.Renewal"/> of it: renewal number + 1, same chain start.</item>
/// <item>The first term on file is the <see cref="ChainLinkKinds.Original"/> only when it starts on or before the
///   employee's joining date — otherwise earlier terms may exist off-system and it stays unconfirmed.</item>
/// <item>Anything else (a gap, an overlap, an unconfirmed predecessor, two successors) stays unconfirmed.</item>
/// </list>
/// Deterministic and idempotent: the same rows always give the same stamps.
/// </summary>
public static class ContractChainLinker
{
    private static readonly HashSet<string> TermStatuses = new(StringComparer.Ordinal) { "Active", "Expired", "Terminated", "Superseded" };

    public static bool IsTerm(string status) => TermStatuses.Contains(status);

    public static IReadOnlyDictionary<Guid, ChainStamp> Link(
        IReadOnlyCollection<ContractChainFacts> contracts, DateOnly? joiningDate, string? employeeNationalityClass)
    {
        var terms = contracts.Where(c => IsTerm(c.Status))
            .OrderBy(c => c.StartDate).ThenBy(c => c.Version).ThenBy(c => c.CreatedAtUtc).ThenBy(c => c.Id)
            .ToList();
        var byId = terms.ToDictionary(t => t.Id);
        var result = new Dictionary<Guid, ChainStamp>();
        // Who already renews whom (rows on file), so a derived link never collides with the UNIQUE.
        var claimed = terms.Where(t => t.RenewedFromContractId is not null)
            .GroupBy(t => t.RenewedFromContractId!.Value).ToDictionary(g => g.Key, g => g.First().Id);

        foreach (var term in terms)
        {
            var nationality = term.WorkerNationalityClass ?? employeeNationalityClass;
            if (term.RenewalNumber is not null && term.ChainStartedOn is not null)
            {
                result[term.Id] = new ChainStamp(term.Id, term.RenewedFromContractId is not null ? ChainLinkKinds.Renewal : ChainLinkKinds.Recorded,
                    term.RenewedFromContractId ?? term.PreviousVersionId, term.RenewedFromContractId, term.RenewalNumber, term.ChainStartedOn,
                    nationality, null);
                continue;
            }

            ChainStamp Unconfirmed(string reason, Guid? linkedTo = null) =>
                new(term.Id, ChainLinkKinds.Unconfirmed, linkedTo, term.RenewedFromContractId, null, null, nationality, reason);

            ChainStamp? FromPredecessor(ContractChainFacts predecessor, bool viaVersionLink)
            {
                if (!result.TryGetValue(predecessor.Id, out var p)) return null;
                var amendment = viaVersionLink
                    && term.StartDate >= predecessor.StartDate
                    && (predecessor.EndDate is null || term.StartDate <= predecessor.EndDate.Value);
                if (amendment)
                    return p.IsConfirmed
                        ? new ChainStamp(term.Id, ChainLinkKinds.Amendment, predecessor.Id, null, p.RenewalNumber, p.ChainStartedOn, nationality, null)
                        : Unconfirmed(ChainGapReasons.PredecessorUnconfirmed, predecessor.Id);
                if (predecessor.EndDate is not { } predecessorEnd || term.StartDate != predecessorEnd.AddDays(1))
                    return Unconfirmed(ChainGapReasons.GapOrOverlap, predecessor.Id);
                if (claimed.TryGetValue(predecessor.Id, out var other) && other != term.Id)
                    return Unconfirmed(ChainGapReasons.AmbiguousSuccessor, predecessor.Id);
                if (!p.IsConfirmed) return Unconfirmed(ChainGapReasons.PredecessorUnconfirmed, predecessor.Id);
                claimed[predecessor.Id] = term.Id;
                return new ChainStamp(term.Id, ChainLinkKinds.Renewal, predecessor.Id, predecessor.Id,
                    (short)(p.RenewalNumber!.Value + 1), p.ChainStartedOn, nationality, null);
            }

            ChainStamp stamp;
            if (term.PreviousVersionId is { } previousId && byId.TryGetValue(previousId, out var previous))
            {
                stamp = FromPredecessor(previous, viaVersionLink: true) ?? Unconfirmed(ChainGapReasons.PredecessorUnconfirmed, previousId);
            }
            else
            {
                var earlier = terms.Where(t => t.Id != term.Id && t.StartDate < term.StartDate).ToList();
                // Of several versions of the term that ended the day before, the in-force one is the predecessor.
                var contiguous = earlier
                    .Where(t => t.EndDate is { } e && e.AddDays(1) == term.StartDate)
                    .OrderBy(t => t.Status == "Superseded" ? 1 : 0).ThenByDescending(t => t.Version).ThenByDescending(t => t.CreatedAtUtc)
                    .FirstOrDefault();
                if (contiguous is not null)
                    stamp = FromPredecessor(contiguous, viaVersionLink: false)!;
                else if (earlier.Count > 0)
                    stamp = Unconfirmed(ChainGapReasons.GapOrOverlap, earlier[^1].Id);
                else if (joiningDate is { } joined && term.StartDate <= joined)
                    stamp = new ChainStamp(term.Id, ChainLinkKinds.Original, null, null, 0, term.StartDate, nationality, null);
                else
                    stamp = Unconfirmed(ChainGapReasons.EarlierTermsNotOnFile);
            }
            result[term.Id] = stamp;
        }
        return result;
    }

    /// <summary>
    /// Writes a stamp onto its row, filling only fields that are NULL — never overwriting what was stamped or
    /// recorded before. Returns true when anything changed.
    /// </summary>
    public static bool Apply(EmployeeContract row, ChainStamp stamp)
    {
        var changed = false;
        if (row.RenewalNumber is null && row.ChainStartedOn is null && stamp.IsConfirmed)
        {
            row.RenewalNumber = stamp.RenewalNumber;
            row.ChainStartedOn = stamp.ChainStartedOn;
            if (row.RenewedFromContractId is null && stamp.RenewedFromContractId is not null)
                row.RenewedFromContractId = stamp.RenewedFromContractId;
            changed = true;
        }
        if (row.WorkerNationalityClass is null && stamp.WorkerNationalityClass is not null)
        {
            row.WorkerNationalityClass = stamp.WorkerNationalityClass;
            changed = true;
        }
        return changed;
    }
}

/// <summary>What one census run did.</summary>
public sealed record ChainCensusResult(int Terms, int Stamped, int Confirmed, int Unconfirmed, int NationalityUnknown);

/// <summary>
/// The chain census: links the existing Superseded/Version rows of one tenant into chains and stamps
/// renewal_number, chain_started_on, renewed_from and the worker's nationality class where they are NULL
/// (<see cref="ContractChainLinker"/>). Never overwrites a value already on a row; unlinkable chains stay NULL and
/// their cases open NeedsConfirmation. It changes no schema: the full provisional/renewal_number pairing CHECK is
/// left for the R0b migration (see the R4 PR). Idempotent — a second run stamps nothing. Stages changes on the
/// context; the caller saves. Slice R4.
/// </summary>
public sealed class ContractChainCensus
{
    private readonly ZayraDbContext _db;

    public ContractChainCensus(ZayraDbContext db) => _db = db;

    /// <summary>Runs the census over every employee of <paramref name="tenantId"/> (or one employee).</summary>
    public async Task<ChainCensusResult> RunAsync(Guid tenantId, Guid? employeePublicId, CancellationToken ct)
    {
        var contracts = await _db.EmployeeContracts
            .Where(c => c.TenantId == tenantId && !c.IsDeleted && (employeePublicId == null || c.EmployeeId == employeePublicId))
            .ToListAsync(ct);
        var employeeIds = contracts.Select(c => c.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.PublicId))
            .Select(e => new { e.PublicId, e.JoiningDate, e.SaudiOrNonSaudi, e.Nationality })
            .ToDictionaryAsync(e => e.PublicId, ct);

        int terms = 0, stamped = 0, confirmed = 0, unconfirmed = 0, nationalityUnknown = 0;
        foreach (var group in contracts.GroupBy(c => c.EmployeeId))
        {
            employees.TryGetValue(group.Key, out var employee);
            var stamps = ContractChainLinker.Link(group.Select(ContractChainFacts.Of).ToList(),
                JoiningDateOf(employee?.JoiningDate), WorkerNationality.ClassOf(employee?.SaudiOrNonSaudi, employee?.Nationality));
            foreach (var row in group)
            {
                if (!stamps.TryGetValue(row.Id, out var stamp)) continue;
                terms++;
                if (ContractChainLinker.Apply(row, stamp))
                {
                    row.UpdatedAtUtc = DateTime.UtcNow;
                    stamped++;
                }
                if (AllowedActionsDeriver.IsChainConfirmed(row)) confirmed++; else unconfirmed++;
                if (row.WorkerNationalityClass is null) nationalityUnknown++;
            }
        }
        return new ChainCensusResult(terms, stamped, confirmed, unconfirmed, nationalityUnknown);
    }

    /// <summary>The employee's joining date, or NULL when it was never recorded (the CLR default).</summary>
    public static DateOnly? JoiningDateOf(DateTime? joining) =>
        joining is { } j && j > DateTime.MinValue ? DateOnly.FromDateTime(j) : null;
}
