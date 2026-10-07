using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>How a term joins the chain before it.</summary>
public static class ChainLinkKinds
{
    /// <summary>The first term of the chain: renewal_number 0, the chain starts on its start date.</summary>
    public const string Original = "Original";
    /// <summary>Starts the day after an earlier term of the same employer ended: renewal_number + 1, same chain start (Art. 56).</summary>
    public const string Renewal = "Renewal";
    /// <summary>A new version of the same term (supersede inside the term, ending no later): same renewal number and chain start.</summary>
    public const string Amendment = "Amendment";
    /// <summary>HR stated the history (chain/confirm), or a value already on the row; kept as recorded.</summary>
    public const string Recorded = "Recorded";
    /// <summary>Could not be linked; nothing is assumed (case opens NeedsConfirmation).</summary>
    public const string Unconfirmed = "Unconfirmed";
}

/// <summary>Why a term could not be linked. Plain-language mapping lives in the UI (renewals strings).</summary>
public static class ChainGapReasons
{
    /// <summary>An earlier term exists but this one does not start the day after it ended.</summary>
    public const string GapOrOverlap = "GapOrOverlap";
    /// <summary>Another term in force covers this term's start date (a stray or duplicate row).</summary>
    public const string Overlap = "Overlap";
    /// <summary>A "new version" that ends later than the term it replaces: an extension is a renewal decision, not an amendment.</summary>
    public const string ExtendsTerm = "ExtendsTerm";
    /// <summary>The earlier term was with another employer (or names none): the chain is per employer (group transfer).</summary>
    public const string CompanyChanged = "CompanyChanged";
    /// <summary>The earlier term itself is unconfirmed, so this one cannot be counted.</summary>
    public const string PredecessorUnconfirmed = "PredecessorUnconfirmed";
    /// <summary>The first term on file starts after the employee joined: earlier terms are not on file.</summary>
    public const string EarlierTermsNotOnFile = "EarlierTermsNotOnFile";
    /// <summary>The first term on file starts before the employee's joining date: one of the two dates is wrong.</summary>
    public const string StartsBeforeJoining = "StartsBeforeJoining";
    /// <summary>The employee's joining date is not recorded, so the first term cannot be shown to be the original.</summary>
    public const string JoiningDateUnknown = "JoiningDateUnknown";
    /// <summary>Two later terms both claim the same earlier term.</summary>
    public const string AmbiguousSuccessor = "AmbiguousSuccessor";
    /// <summary>A new version (supersede link) that starts after the version it replaces ended: a re-papered term is not a
    /// renewal by itself — HR confirms what it was.</summary>
    public const string VersionNotRenewal = "VersionNotRenewal";
    /// <summary>The earlier term was terminated: what followed is a new engagement or a correction, never counted by itself.</summary>
    public const string PredecessorTerminated = "PredecessorTerminated";
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
    DateTime CreatedAtUtc,
    Guid? CompanyId = null,
    string? ChainSource = null,
    DateOnly? TerminatedOn = null)
{
    /// <param name="terminatedOn">The day a Terminated term actually stopped (from its status change), when known.</param>
    public static ContractChainFacts Of(EmployeeContract c, DateOnly? terminatedOn = null) => new(c.Id, c.Status, c.StartDate, c.EndDate, c.Version,
        c.PreviousVersionId, c.RenewedFromContractId, c.RenewalNumber, c.ChainStartedOn, c.WorkerNationalityClass, c.CreatedAtUtc, c.CompanyId,
        c.ChainSource, terminatedOn);

    /// <summary>The last day the term was actually in force: the termination day for a Terminated term (when known and
    /// earlier than the end date), else the end date.</summary>
    public DateOnly? EffectiveEnd =>
        Status == "Terminated" && TerminatedOn is { } t && (EndDate is null || t < EndDate.Value) ? t : EndDate;
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
    // Demonyms for the countries of IsoReference (the reference list the product supports). Saudi and GCC are not here:
    // Saudi goes through SaudiNationality, GCC is never classified automatically.
    private static readonly Dictionary<string, string> Demonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Egyptian"] = "EG", ["Jordanian"] = "JO", ["Lebanese"] = "LB", ["Iraqi"] = "IQ", ["Yemeni"] = "YE", ["Yemenite"] = "YE",
        ["Syrian"] = "SY", ["Palestinian"] = "PS", ["Moroccan"] = "MA", ["Algerian"] = "DZ", ["Tunisian"] = "TN", ["Libyan"] = "LY",
        ["Sudanese"] = "SD", ["Turkish"] = "TR", ["Indian"] = "IN", ["Pakistani"] = "PK", ["Bangladeshi"] = "BD", ["Sri Lankan"] = "LK",
        ["Nepali"] = "NP", ["Nepalese"] = "NP", ["Filipino"] = "PH", ["Filipina"] = "PH", ["Philippine"] = "PH", ["Indonesian"] = "ID",
        ["American"] = "US", ["British"] = "GB", ["Canadian"] = "CA", ["Australian"] = "AU", ["German"] = "DE", ["French"] = "FR",
        ["Italian"] = "IT", ["Spanish"] = "ES", ["Dutch"] = "NL", ["Swiss"] = "CH", ["Chinese"] = "CN", ["Japanese"] = "JP",
        ["Singaporean"] = "SG", ["Malaysian"] = "MY", ["South African"] = "ZA", ["Nigerian"] = "NG", ["Kenyan"] = "KE",
        ["Ethiopian"] = "ET",
        // Arabic demonyms of the largest expatriate groups in the Kingdom.
        ["مصري"] = "EG", ["أردني"] = "JO", ["سوداني"] = "SD", ["يمني"] = "YE", ["سوري"] = "SY", ["هندي"] = "IN", ["باكستاني"] = "PK",
        ["بنغلاديشي"] = "BD", ["فلبيني"] = "PH", ["إندونيسي"] = "ID", ["نيبالي"] = "NP", ["سريلانكي"] = "LK",
    };

    private static readonly HashSet<string> GccIso2 = new(StringComparer.OrdinalIgnoreCase) { "AE", "KW", "QA", "BH", "OM" };

    /// <summary>
    /// Saudi / NonSaudi from the employee's RECORDED nationality, checked against the declared Qiwa class. Saudi only through
    /// the shared <see cref="SaudiNationality"/> normaliser; NonSaudi only for a RECOGNISED non-Saudi country (ISO code,
    /// the reference country name or its demonym). A declared class never stands alone. NULL — "HR confirms" — for
    /// anything else: no nationality recorded, an unrecognised value ("Unknown", "-", a typo), a contradiction with the
    /// declared class, or
    /// a GCC national (GCC nationals have their own treatment in several Saudi rules). This stamp
    /// (<c>worker_nationality_class</c>) is the single source Release A reads, so it is never guessed.
    /// </summary>
    public static string? ClassOf(string? declared, string? nationality)
    {
        // A nationality must be on record: a declared class alone is never enough (R4 re-review P3).
        if (string.IsNullOrWhiteSpace(nationality)) return null;
        var fromDeclared = declared?.Trim().Replace("-", "").Replace(" ", "").Replace("_", "").ToUpperInvariant() switch
        {
            "SAUDI" => WorkerNationalityClasses.Saudi,
            "NONSAUDI" => WorkerNationalityClasses.NonSaudi,
            _ => null,
        };
        var iso = IsoOf(nationality);
        string fromNationality;
        if (SaudiNationality.IsSaudi(nationality)) fromNationality = WorkerNationalityClasses.Saudi;
        else if (GosiCalculationService.DeriveClassification(nationality) == GosiClassifications.GCC || (iso is not null && GccIso2.Contains(iso)))
            return null;
        else if (iso is not null) fromNationality = WorkerNationalityClasses.NonSaudi;
        else return null; // an unrecognised value ("Unknown", "-", a typo) is never read as either class
        if (fromDeclared is not null && fromDeclared != fromNationality) return null;
        return fromNationality;
    }

    /// <summary>The ISO-2 code of a recognised non-Saudi nationality (code, reference country name or demonym), else NULL.</summary>
    public static string? IsoOf(string nationality)
    {
        var v = nationality.Trim();
        if (CountryCodeStandard.NormalizeToIso2(v) is { } code) return code == "SA" ? null : code;
        var byName = IsoReference.Countries.FirstOrDefault(c => string.Equals(c.Name, v, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName.Code == "SA" ? null : byName.Code;
        return Demonyms.TryGetValue(v, out var iso) ? iso : null;
    }
}

/// <summary>
/// The pure chain rule (plan §2 R4, rev 8.3.2 §6; corrected in the R4 review). Given every row of one employee's
/// contracts it derives, for each TERM (a contract that was ever in force: Active, Expired, Terminated or Superseded),
/// its place in the Article 55 chain. The chain is per EMPLOYER: every link requires the same company on both terms.
/// <list type="bullet">
/// <item>Values already on the row (stamped earlier or recorded by HR) are kept exactly as they are.</item>
/// <item>A term whose start date is covered by another term in force (not its own version family, not a version that
///   was itself replaced) is unconfirmed — a stray or duplicate row makes any count unsafe.</item>
/// <item>A supersede version that starts inside its predecessor AND ends no later is an
///   <see cref="ChainLinkKinds.Amendment"/>: same renewal number and chain start, no renewed_from. One that ends later is
///   an extension — a renewal decision — and stays unconfirmed.</item>
/// <item>A term that starts the day after an earlier term of the same employer ended (by supersede link or by dates;
///   a version that was replaced by a newer version is never a candidate) is a <see cref="ChainLinkKinds.Renewal"/>.</item>
/// <item>The first term on file is the <see cref="ChainLinkKinds.Original"/> only when it starts ON the joining date
///   (or up to <c>toleranceDays</c> after it). Starting before the joining date is bad data; after it, earlier terms
///   may exist off-system. Both stay unconfirmed.</item>
/// </list>
/// Deterministic and idempotent: the same rows always give the same stamps.
/// </summary>
public static class ContractChainLinker
{
    private static readonly HashSet<string> TermStatuses = new(StringComparer.Ordinal) { "Active", "Expired", "Terminated", "Superseded" };

    public static bool IsTerm(string status) => TermStatuses.Contains(status);

    /// <param name="toleranceDays">Days a first term may start after the joining date and still be the original (tenant rule).</param>
    /// <param name="gapToleranceDays">Days of gap between two terms still read as continuous (tenant rule, default 0).</param>
    public static IReadOnlyDictionary<Guid, ChainStamp> Link(
        IReadOnlyCollection<ContractChainFacts> contracts, DateOnly? joiningDate, string? employeeNationalityClass, int toleranceDays = 0,
        int gapToleranceDays = 0)
    {
        var terms = contracts.Where(c => IsTerm(c.Status))
            .OrderBy(c => c.StartDate).ThenBy(c => c.Version).ThenBy(c => c.CreatedAtUtc).ThenBy(c => c.Id)
            .ToList();
        var byId = terms.ToDictionary(t => t.Id);
        // A term another term names as its previous version has been replaced: never a renewal predecessor, never an overlap.
        var replaced = terms.Where(t => t.PreviousVersionId is { } p && byId.ContainsKey(p)).Select(t => t.PreviousVersionId!.Value).ToHashSet();
        var family = terms.ToDictionary(t => t.Id, t => FamilyRoot(t, byId));
        var gap = Math.Max(0, gapToleranceDays);

        // Overlaps flag BOTH terms: two terms in force on the same day (outside one version family) make every count
        // that touches either of them unsafe. A Terminated term is in force only until it was terminated.
        var overlappedBy = new Dictionary<Guid, Guid>();
        var live = terms.Where(t => !replaced.Contains(t.Id)).ToList();
        for (var i = 0; i < live.Count; i++)
            for (var j = i + 1; j < live.Count; j++)
            {
                var (x, y) = (live[i], live[j]);
                if (family[x.Id] == family[y.Id]) continue;
                var xEnd = x.EffectiveEnd ?? DateOnly.MaxValue;
                var yEnd = y.EffectiveEnd ?? DateOnly.MaxValue;
                if (x.StartDate <= yEnd && y.StartDate <= xEnd)
                {
                    overlappedBy.TryAdd(x.Id, y.Id);
                    overlappedBy.TryAdd(y.Id, x.Id);
                }
            }

        var result = new Dictionary<Guid, ChainStamp>();
        var claimed = terms.Where(t => t.RenewedFromContractId is not null)
            .GroupBy(t => t.RenewedFromContractId!.Value).ToDictionary(g => g.Key, g => g.First().Id);

        foreach (var term in terms)
        {
            var nationality = term.WorkerNationalityClass ?? employeeNationalityClass;
            if (term.RenewalNumber is not null && term.ChainStartedOn is not null)
            {
                // Kept as stamped. "Recorded" only for what HR confirmed; a derived stamp keeps the kind it was derived as.
                var kind = term.ChainSource == ChainSources.Recorded ? ChainLinkKinds.Recorded
                    : term.RenewedFromContractId is not null ? ChainLinkKinds.Renewal
                    : term.PreviousVersionId is not null && byId.ContainsKey(term.PreviousVersionId.Value) ? ChainLinkKinds.Amendment
                    : term.RenewalNumber == 0 ? ChainLinkKinds.Original
                    : ChainLinkKinds.Recorded;
                result[term.Id] = new ChainStamp(term.Id, kind, term.RenewedFromContractId ?? term.PreviousVersionId, term.RenewedFromContractId,
                    term.RenewalNumber, term.ChainStartedOn, nationality, null);
                continue;
            }

            ChainStamp Unconfirmed(string reason, Guid? linkedTo = null) =>
                new(term.Id, ChainLinkKinds.Unconfirmed, linkedTo, term.RenewedFromContractId, null, null, nationality, reason);

            if (overlappedBy.TryGetValue(term.Id, out var overlapping))
            {
                result[term.Id] = Unconfirmed(ChainGapReasons.Overlap, overlapping);
                continue;
            }

            bool Continues(ContractChainFacts predecessor) =>
                predecessor.EffectiveEnd is { } end
                && term.StartDate.DayNumber - end.DayNumber - 1 is var days && days >= 0 && days <= gap;

            ChainStamp FromPredecessor(ContractChainFacts predecessor, bool viaVersionLink)
            {
                if (term.CompanyId is null || predecessor.CompanyId != term.CompanyId)
                    return Unconfirmed(ChainGapReasons.CompanyChanged, predecessor.Id);
                if (!result.TryGetValue(predecessor.Id, out var p)) return Unconfirmed(ChainGapReasons.PredecessorUnconfirmed, predecessor.Id);
                var startsInside = term.StartDate >= predecessor.StartDate
                                   && (predecessor.EndDate is null || term.StartDate <= predecessor.EndDate.Value);
                if (viaVersionLink)
                {
                    // A new version is only ever an amendment of the term it replaces; anything else is HR's to confirm.
                    if (!startsInside) return Unconfirmed(ChainGapReasons.VersionNotRenewal, predecessor.Id);
                    var endsNoLater = predecessor.EndDate is null || (term.EndDate is { } e && e <= predecessor.EndDate.Value);
                    if (!endsNoLater) return Unconfirmed(ChainGapReasons.ExtendsTerm, predecessor.Id);
                    return p.IsConfirmed
                        ? new ChainStamp(term.Id, ChainLinkKinds.Amendment, predecessor.Id, null, p.RenewalNumber, p.ChainStartedOn, nationality, null)
                        : Unconfirmed(ChainGapReasons.PredecessorUnconfirmed, predecessor.Id);
                }
                if (predecessor.Status == "Terminated") return Unconfirmed(ChainGapReasons.PredecessorTerminated, predecessor.Id);
                if (!Continues(predecessor)) return Unconfirmed(ChainGapReasons.GapOrOverlap, predecessor.Id);
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
                stamp = FromPredecessor(previous, viaVersionLink: true);
            }
            else
            {
                var earlier = terms.Where(t => t.Id != term.Id && t.StartDate < term.StartDate).ToList();
                var contiguous = earlier.Where(t => !replaced.Contains(t.Id) && Continues(t)).ToList();
                if (contiguous.Count > 1)
                    stamp = Unconfirmed(ChainGapReasons.Overlap, contiguous[1].Id);
                else if (contiguous.Count == 1)
                    stamp = FromPredecessor(contiguous[0], viaVersionLink: false);
                else if (earlier.Count > 0)
                    stamp = Unconfirmed(ChainGapReasons.GapOrOverlap, earlier[^1].Id);
                else if (joiningDate is not { } joined)
                    stamp = Unconfirmed(ChainGapReasons.JoiningDateUnknown);
                else if (term.StartDate < joined)
                    stamp = Unconfirmed(ChainGapReasons.StartsBeforeJoining);
                else if (term.StartDate.DayNumber - joined.DayNumber <= Math.Max(0, toleranceDays))
                    stamp = new ChainStamp(term.Id, ChainLinkKinds.Original, null, null, 0, term.StartDate, nationality, null);
                else
                    stamp = Unconfirmed(ChainGapReasons.EarlierTermsNotOnFile);
            }
            result[term.Id] = stamp;
        }
        return result;
    }

    private static Guid FamilyRoot(ContractChainFacts t, IReadOnlyDictionary<Guid, ContractChainFacts> byId)
    {
        var current = t;
        var seen = new HashSet<Guid> { t.Id };
        while (current.PreviousVersionId is { } p && byId.TryGetValue(p, out var prev) && seen.Add(prev.Id))
            current = prev;
        return current.Id;
    }

    /// <summary>
    /// Writes a stamp onto its row, filling only fields that are NULL — never overwriting what was stamped or
    /// recorded before — and marks the fields <see cref="ChainSources.Derived"/>. Returns true when anything changed.
    /// </summary>
    public static bool Apply(EmployeeContract row, ChainStamp stamp)
    {
        var changed = false;
        if (row.RenewalNumber is null && row.ChainStartedOn is null && stamp.IsConfirmed)
        {
            row.RenewalNumber = stamp.RenewalNumber;
            row.ChainStartedOn = stamp.ChainStartedOn;
            row.ChainSource = ChainSources.Derived;
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

    /// <summary>
    /// Clears a DERIVED stamp so it can be re-derived (HR corrected an earlier term). A Recorded stamp is never cleared.
    /// Returns true when the row was cleared.
    /// </summary>
    public static bool ClearDerived(EmployeeContract row)
    {
        if (row.ChainSource != ChainSources.Derived) return false;
        row.RenewalNumber = null;
        row.ChainStartedOn = null;
        row.RenewedFromContractId = null;
        row.ChainSource = null;
        return true;
    }
}

/// <summary>What one census run did.</summary>
public sealed record ChainCensusResult(int Terms, int Stamped, int Confirmed, int Unconfirmed, int NationalityUnknown);

/// <summary>
/// The chain census: links the existing Superseded/Version rows of one tenant into chains and stamps
/// renewal_number, chain_started_on, renewed_from, chain_source = Derived and the worker's nationality class where
/// they are NULL (<see cref="ContractChainLinker"/>). Never overwrites a value already on a row; unlinkable chains stay
/// NULL and their cases open NeedsConfirmation. Idempotent — a second run stamps nothing. Stages changes on the
/// context; the caller saves. Slice R4.
/// </summary>
public sealed class ContractChainCensus
{
    private readonly ZayraDbContext _db;
    private readonly ITenantClock? _clock;

    /// <param name="clock">Tenant-local today for the rules in force; injected by DI (optional for direct constructions).</param>
    public ContractChainCensus(ZayraDbContext db, ITenantClock? clock = null)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Runs the census over every employee of <paramref name="tenantId"/> (or one employee).</summary>
    /// <param name="toleranceDays">The original-term joining tolerance; NULL reads it from the tenant's rules.</param>
    public async Task<ChainCensusResult> RunAsync(Guid tenantId, Guid? employeePublicId, CancellationToken ct, int? toleranceDays = null)
    {
        var today = _clock is not null ? await _clock.TodayAsync(tenantId, ct) : DateOnly.FromDateTime(DateTime.UtcNow);
        var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, today, ct);
        if (toleranceDays is { } overrideTolerance) rules = rules with { OriginalTermJoiningToleranceDays = overrideTolerance };
        var contracts = await _db.EmployeeContracts
            .Where(c => c.TenantId == tenantId && !c.IsDeleted && (employeePublicId == null || c.EmployeeId == employeePublicId))
            .ToListAsync(ct);
        var employeeIds = contracts.Select(c => c.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.PublicId))
            .Select(e => new { e.PublicId, e.JoiningDate, e.SaudiOrNonSaudi, e.Nationality })
            .ToDictionaryAsync(e => e.PublicId, ct);
        var terminated = await TerminationDatesAsync(_db, tenantId, contracts, ct);
        int terms = 0, stamped = 0, confirmed = 0, unconfirmed = 0, nationalityUnknown = 0;
        foreach (var group in contracts.GroupBy(c => c.EmployeeId))
        {
            employees.TryGetValue(group.Key, out var employee);
            var stamps = ContractChainLinker.Link(group.Select(c => ContractChainFacts.Of(c, terminated.GetValueOrDefault(c.Id))).ToList(),
                JoiningDateOf(employee?.JoiningDate), WorkerNationality.ClassOf(employee?.SaudiOrNonSaudi, employee?.Nationality),
                rules.OriginalTermJoiningToleranceDays, rules.ChainGapToleranceDays);
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

    /// <summary>
    /// The day each Terminated contract actually stopped, from its status-change audit row (ContractsController writes
    /// {from, to:"Terminated"}). A contract terminated without that row (an import) keeps its end date — the
    /// conservative reading for the overlap check.
    /// </summary>
    public static async Task<Dictionary<Guid, DateOnly>> TerminationDatesAsync(ZayraDbContext db, Guid tenantId,
        IEnumerable<EmployeeContract> contracts, CancellationToken ct)
    {
        var ids = contracts.Where(c => c.Status == "Terminated").Select(c => c.Id.ToString()).ToList();
        if (ids.Count == 0) return [];
        var rows = await db.ComplianceAuditLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EntityType == "Contract" && l.Action == "StatusChanged" && ids.Contains(l.EntityId))
            .Select(l => new { l.EntityId, l.CreatedAtUtc, l.MetadataJson })
            .ToListAsync(ct);
        // metadata_json is a json column (no LIKE): the few status-change rows of terminated contracts are read and parsed here.
        return rows.Where(r => r.MetadataJson.Contains("\"to\":\"Terminated\"", StringComparison.Ordinal))
            .GroupBy(r => Guid.Parse(r.EntityId))
            .ToDictionary(g => g.Key, g => DateOnly.FromDateTime(g.Min(r => r.CreatedAtUtc)));
    }

    /// <summary>
    /// The chain rule for ONE employee with everything it reads: termination days, joining date, nationality and the
    /// tenant's two tolerances. The single entry point for the stamper, the chain endpoint and chain confirm.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, ChainStamp>> LinkEmployeeAsync(ZayraDbContext db, Guid tenantId, Guid employeePublicId,
        IReadOnlyCollection<EmployeeContract> contracts, RenewalRuleSet rules, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PublicId == employeePublicId)
            .Select(e => new { e.JoiningDate, e.SaudiOrNonSaudi, e.Nationality })
            .FirstOrDefaultAsync(ct);
        var terminated = await TerminationDatesAsync(db, tenantId, contracts, ct);
        return ContractChainLinker.Link(contracts.Select(c => ContractChainFacts.Of(c, terminated.GetValueOrDefault(c.Id))).ToList(),
            JoiningDateOf(employee?.JoiningDate), WorkerNationality.ClassOf(employee?.SaudiOrNonSaudi, employee?.Nationality),
            rules.OriginalTermJoiningToleranceDays, rules.ChainGapToleranceDays);
    }

    /// <summary>The employee's nationality class as Release A reads it (NULL = HR confirms).</summary>
    public static async Task<string?> EmployeeClassAsync(ZayraDbContext db, Guid tenantId, Guid employeePublicId, CancellationToken ct)
    {
        var e = await db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PublicId == employeePublicId)
            .Select(x => new { x.SaudiOrNonSaudi, x.Nationality }).FirstOrDefaultAsync(ct);
        return WorkerNationality.ClassOf(e?.SaudiOrNonSaudi, e?.Nationality);
    }

    /// <summary>The employee's joining date, or NULL when it was never recorded (the CLR default).</summary>
    public static DateOnly? JoiningDateOf(DateTime? joining) =>
        joining is { } j && j > DateTime.MinValue ? DateOnly.FromDateTime(j) : null;
}
