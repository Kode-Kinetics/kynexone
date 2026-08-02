using System.Text;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// AUTHORITATIVE, DB-free work-email derivation. The single implementation shared by single-create
/// (EmployeeManagementService), the bulk importer (EmployeeImportRowResolver / EmployeesController.Import)
/// and the modal preview endpoint (EmployeesController.DeriveWorkEmail) so the preview a user sees is
/// byte-identical to what is committed.
///
/// Design notes / verified constraints:
///  - The name → local-part step reuses the EXISTING <see cref="NameNormalizer.Normalize"/> helper
///    (lowercase, strip diacritics, letters + single spaces) and then keeps ONLY ASCII [a-z] tokens
///    BEFORE selecting first/last. NameNormalizer keeps ANY <c>char.IsLetter</c> (Arabic letters, ß/ł/ø),
///    so filtering afterwards would let an Arabic token win "first" and silently drop the real name.
///  - The project's TransliterationService is Latin→Arabic only (verified) — it cannot romanize an
///    Arabic-only name into an ASCII local part. Such names therefore yield an EMPTY local part and the
///    caller falls back to manual entry (never blocked, never a garbage address).
///  - Callers supply the collision predicate (DB set ∪ batch-claimed set) so the collision SCOPE is
///    owned by the caller (tenant × normalized-address) and this stays pure and unit-testable. There is
///    NO DB unique constraint backing work_email (ix_employees_tenant_work_email is NON-UNIQUE by design,
///    like id_number — the importer must be able to LAND duplicate provided values and flag them), so
///    uniqueness here is best-effort application-level; the real login backstop is the unique
///    User.(TenantId, NormalizedEmail) at provisioning.
///  - This sets the email STRING only (the HR record / login identity). It does NOT provision a mailbox.
/// </summary>
public static class WorkEmailDeriver
{
    /// <summary>
    /// Build the local part from a name per <paramref name="pattern"/>. Returns "" when no ASCII token
    /// exists (pure-Arabic / blank / unromanizable) — the caller treats "" as "needs manual entry".
    /// </summary>
    public static string BuildLocalPart(string? englishName, string? arabicName, string pattern)
    {
        // Normalize the English name via the existing helper, then keep ONLY ASCII-letter tokens.
        var tokens = NameNormalizer.Normalize(englishName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(KeepAsciiLetters)
            .Where(t => t.Length > 0)
            .ToList();

        // No ASCII token (e.g. an Arabic-only name — the Arabic name is intentionally NOT romanized here
        // because the project has no Arabic→Latin transliterator): signal manual entry.
        if (tokens.Count == 0) return string.Empty;

        var first = tokens[0];
        var last = tokens.Count > 1 ? tokens[^1] : string.Empty;

        var local = WorkEmailPatterns.Normalize(pattern) switch
        {
            WorkEmailPatterns.Flast => last.Length > 0 ? $"{first[..1]}{last}" : first,
            WorkEmailPatterns.First => first,
            WorkEmailPatterns.FirstUnderscoreLast => last.Length > 0 ? $"{first}_{last}" : first,
            _ /* first.last (default) */ => last.Length > 0 ? $"{first}.{last}" : first,
        };
        return Sanitize(local);
    }

    /// <summary>
    /// The single server-authoritative resolution shared by create, update, the PATCH edit, and the preview
    /// endpoint so they can NEVER diverge. Given a company <paramref name="domain"/> (already known non-empty),
    /// the provided value (blank / local part / full address), the name (for derive-when-blank), and the
    /// caller's normalized collision predicate:
    ///  - blank + a romanizable name → DERIVE and auto-suffix on collision (never throws);
    ///  - blank + no romanizable name (e.g. Arabic-only) → outcome "manual", returns "" (leave for manual);
    ///  - a provided value → the domain is authoritative: extract the local part and RE-ASSEMBLE on the
    ///    company domain (a foreign/stale domain is coerced, surfaced via <paramref name="coercedFrom"/>),
    ///    then a collision throws <see cref="WorkEmailConflictException"/> (the one deliberate stop).
    /// </summary>
    public static string Resolve(
        string? providedWorkEmail, string? englishName, string? arabicName,
        string domain, string pattern, Func<string, bool> isTaken,
        out string outcome, out string? coercedFrom)
    {
        coercedFrom = null;
        var provided = (providedWorkEmail ?? string.Empty).Trim();
        if (provided.Length == 0)
        {
            var local = BuildLocalPart(englishName, arabicName, pattern);
            if (local.Length == 0) { outcome = "manual"; return string.Empty; }
            outcome = "derived";
            return Uniqueify(local, domain, isTaken);
        }
        var localPart = ExtractLocalPart(provided);
        var (matches, providedDomain) = ValidateAgainstDomain(provided, domain);
        var assembled = Assemble(localPart, domain);
        if (!matches && !string.IsNullOrEmpty(providedDomain)) coercedFrom = provided;
        if (isTaken(assembled))
            throw new WorkEmailConflictException(assembled, Uniqueify(localPart, domain, isTaken));
        outcome = coercedFrom is null ? "assembled" : "coerced";
        return assembled;
    }

    /// <summary>Assemble a full address from a local part and a domain (domain lowercased/trimmed).</summary>
    public static string Assemble(string localPart, string domain) =>
        $"{localPart.Trim()}@{domain.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Return the first non-taken address for <paramref name="localPart"/>@<paramref name="domain"/>,
    /// auto-suffixing on collision: <c>john.smith</c> → <c>john.smith2</c> → <c>john.smith3</c> …
    /// <paramref name="isTaken"/> is the caller's collision predicate (already normalized on its side).
    /// </summary>
    public static string Uniqueify(string localPart, string domain, Func<string, bool> isTaken)
    {
        var baseAddr = Assemble(localPart, domain);
        if (!isTaken(baseAddr)) return baseAddr;
        for (var i = 2; i < 100000; i++)
        {
            var candidate = Assemble($"{localPart}{i}", domain);
            if (!isTaken(candidate)) return candidate;
        }
        return baseAddr; // astronomically unreachable — the caller's own guard is the final backstop
    }

    /// <summary>Extract the local part of an address (everything before the last '@'; the whole trimmed
    /// value when there is no '@'). Used to coerce a submitted address onto the authoritative domain.</summary>
    public static string ExtractLocalPart(string workEmail)
    {
        var value = (workEmail ?? string.Empty).Trim();
        var at = value.LastIndexOf('@');
        return at < 0 ? value : value[..at];
    }

    /// <summary>
    /// Check a full work email against the company domain (import mismatch flag). Returns whether the
    /// address's domain matches <paramref name="companyDomain"/> and the domain that was found (null when
    /// the value has no '@').
    /// </summary>
    public static (bool Matches, string? DomainOf) ValidateAgainstDomain(string workEmail, string companyDomain)
    {
        var value = (workEmail ?? string.Empty).Trim();
        var at = value.LastIndexOf('@');
        if (at < 0) return (false, null);
        var dom = value[(at + 1)..].Trim().ToLowerInvariant();
        return (string.Equals(dom, (companyDomain ?? string.Empty).Trim().ToLowerInvariant(), StringComparison.Ordinal), dom);
    }

    // Keep only ASCII a-z (input already lowercased/diacritic-stripped by NameNormalizer.Normalize).
    private static string KeepAsciiLetters(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var ch in token)
            if (ch >= 'a' && ch <= 'z') sb.Append(ch);
        return sb.ToString();
    }

    // Keep [a-z0-9] and the pattern separators ('.', '_'); collapse repeated separators; trim edges.
    private static string Sanitize(string local)
    {
        var sb = new StringBuilder(local.Length);
        char prev = '\0';
        foreach (var raw in local.ToLowerInvariant())
        {
            var ch = raw;
            var keep = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '_';
            if (!keep) continue;
            var isSep = ch == '.' || ch == '_';
            if (isSep && (sb.Length == 0 || prev == '.' || prev == '_')) continue; // no leading / doubled sep
            sb.Append(ch);
            prev = ch;
        }
        // trim a trailing separator
        while (sb.Length > 0 && (sb[^1] == '.' || sb[^1] == '_')) sb.Length--;
        return sb.ToString();
    }
}

/// <summary>
/// Raised when a user-SUPPLIED work-email local part collides with an existing address in the tenant
/// (the one deliberate stop the "never silently duplicate" rule mandates — auto-derived values are
/// suffixed instead and never raise this). Carries the next free address so the UI can offer it.
/// Auto-derived values never raise this.
/// </summary>
public sealed class WorkEmailConflictException : Exception
{
    public string Attempted { get; }
    public string Suggestion { get; }

    public WorkEmailConflictException(string attempted, string suggestion)
        : base($"Work email '{attempted}' is already used by another employee in this tenant. Try '{suggestion}'.")
    {
        Attempted = attempted;
        Suggestion = suggestion;
    }
}
