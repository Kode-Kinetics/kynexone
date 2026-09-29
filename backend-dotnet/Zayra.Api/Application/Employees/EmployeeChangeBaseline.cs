using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>
/// What the approver saw. When a change is approved with a FUTURE effective date, the value of every
/// column it will write is recorded at that moment; on the effective date the job compares the columns
/// with that record before writing anything. If any of them moved in between (another change was
/// applied, the payroll profile was edited, an import filled a blank), the approved value is no longer
/// a decision about the record as it now stands, so it goes back for review instead of overwriting.
///
/// <para>WHY ENCRYPTED. The record holds the same values the change touches — IBANs, passport numbers,
/// salary, date of birth — and it is stored on an <see cref="EmployeeHistory"/> row whose SnapshotJson
/// the history endpoint returns. A digest would not do: salary and date of birth are low-entropy and a
/// hash of either is reversed by enumeration. The values are sealed with the application's Data
/// Protection key ring (persisted in PostgreSQL, shared by every instance), bound to the change id so a
/// blob cannot be replayed onto another change, and never logged. A blob that cannot be opened is
/// treated as "cannot verify", which sends the change to review — never as "unchanged".</para>
/// </summary>
public static class EmployeeChangeBaseline
{
    /// <summary>EmployeeHistory.EventType of the row written when a future-dated change is approved.</summary>
    public const string ScheduledEventType = "SensitiveChangeScheduled";

    /// <summary>EmployeeHistory.EventType of the row the effective-date job writes when it applies a change.</summary>
    public const string AppliedEventType = "SensitiveChangeApplied";

    /// <summary>
    /// EmployeeHistory.EventType of the row the effective-date job writes when it sends a change back to the
    /// Approval Center. Its snapshot carries a sealed baseline of what the reviewer is shown, so approving the
    /// re-review can check nothing moved in the meantime (<see cref="CheckUnchangedSinceReviewAsync"/>).
    /// </summary>
    public const string ReturnedForReviewEventType = "SensitiveChangeReturnedForReview";

    /// <summary>Data Protection purpose. Changing it makes every stored baseline unreadable.</summary>
    public const string ProtectorPurpose = "Zayra.Api.EmployeeChangeRequest.Baseline.v1";

    private const string EmployeePrefix = "employee:";
    private const string ProfilePrefix = "profile:";

    public static IDataProtector CreateProtector(IDataProtectionProvider provider) =>
        provider.CreateProtector(ProtectorPurpose);

    /// <summary>
    /// The stored value of every column the patch writes, keyed <c>employee:{key}</c> and, for the keys
    /// payroll also reads (bank details, social-insurance reference), <c>profile:{key}</c>. Returns null
    /// when any key cannot be read — a baseline that silently skipped a field would later report "no
    /// drift" for exactly the field it could not see.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Capture(
        Employee employee, EmployeePayrollProfile? profile, IEnumerable<string> keys)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!EmployeeChangeApplier.PayrollProfileKeys.Contains(key))
            {
                if (!TryReadEmployee(employee, key, out var value)) return null;
                values[EmployeePrefix + key] = value;
            }
            if (ProfileColumn(key) is { } column)
                values[ProfilePrefix + key] = profile is null ? string.Empty : Canonical(column(profile));
        }
        return values;
    }

    public static string Protect(IDataProtector protector, Guid changeId, IReadOnlyDictionary<string, string> values) =>
        protector.Protect(JsonSerializer.Serialize(new Envelope(changeId, new Dictionary<string, string>(values))));

    /// <summary>Opens a sealed baseline. Null when it cannot be opened or belongs to another change.</summary>
    public static IReadOnlyDictionary<string, string>? Unprotect(IDataProtector protector, Guid changeId, string? sealedBaseline)
    {
        if (string.IsNullOrWhiteSpace(sealedBaseline)) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(protector.Unprotect(sealedBaseline));
            return envelope is not null && envelope.ChangeId == changeId ? envelope.Values : null;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The field names whose value on file no longer matches the baseline. A column present on one side
    /// only counts as moved. Names only — never values — so the result is safe to log and to show.
    /// </summary>
    /// <param name="alsoExpected">Per column, one more value that is NOT drift: what an earlier-dated change
    /// of the same schedule wrote when it took effect after this one was approved (see
    /// <see cref="Projected"/>). Without it the second of two approved future changes to one field always
    /// read its own predecessor as "changed since approval".</param>
    public static IReadOnlyList<string> Drifted(
        IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string>? current,
        IReadOnlyDictionary<string, string>? alsoExpected = null)
    {
        if (current is null) return baseline.Keys.Select(FieldName).Distinct(StringComparer.Ordinal).ToList();
        return baseline.Keys.Union(current.Keys, StringComparer.Ordinal)
            .Where(k => !current.TryGetValue(k, out var now)
                        || !((baseline.TryGetValue(k, out var before) && string.Equals(before, now, StringComparison.Ordinal))
                             || (alsoExpected is not null && alsoExpected.TryGetValue(k, out var scheduled)
                                 && string.Equals(scheduled, now, StringComparison.Ordinal))))
            .Select(FieldName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The columns a change set leaves on the record once applied, keyed like <see cref="Capture"/>: the
    /// patch is run through <see cref="EmployeeChangeApplier.Apply"/> on a scratch employee, and the bank
    /// keys are mirrored onto a scratch profile the way <see cref="EmployeeBankProfileSync"/> does. Null when
    /// the patch cannot be applied (unknown key, malformed value) — then nothing is "expected" from it.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Projected(IReadOnlyDictionary<string, JsonElement> changes)
    {
        var employee = new Employee();
        try
        {
            if (EmployeeChangeApplier.Apply(employee, changes).Count > 0) return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
        var profile = new EmployeePayrollProfile { BankName = employee.BankName, Iban = employee.BankIban };
        if (changes.TryGetValue("socialInsuranceReference", out var reference))
            profile.SocialInsuranceReference = reference.ValueKind == JsonValueKind.Null ? string.Empty : reference.GetString() ?? string.Empty;
        return Capture(employee, profile, changes.Keys);
    }

    /// <summary>The value on file for one patch key, as <see cref="Capture"/> reads it — the payroll-profile
    /// column when the key is paid or filed from there and a profile exists, else the employee column.</summary>
    public static string? ValueOf(IReadOnlyDictionary<string, string>? values, string key) =>
        values is null ? null
        : values.TryGetValue(ProfilePrefix + key, out var onProfile) && (EmployeeChangeApplier.PayrollProfileKeys.Contains(key) || onProfile.Length > 0)
            ? onProfile
            : values.TryGetValue(EmployeePrefix + key, out var onEmployee) ? onEmployee : null;

    /// <summary>What <see cref="CheckUnchangedSinceReviewAsync"/> found.</summary>
    /// <param name="Verified">False when the change was returned for review but what the reviewer was shown
    /// cannot be read back — the approval must not proceed on an unverifiable record.</param>
    /// <param name="ChangedFields">Patch keys whose value moved after the re-review was raised.</param>
    public sealed record ReviewCheck(bool Verified, IReadOnlyList<string> ChangedFields)
    {
        public bool Unchanged => Verified && ChangedFields.Count == 0;

        /// <summary>Why the approval is refused, in plain words. <paramref name="labels"/> turns
        /// "bankIban,salary" into display names.</summary>
        public string Refusal(Func<string?, string?> labels) => !Verified
            ? "This re-review cannot be checked against the values it showed, so approving it could overwrite a newer value. "
              + "Reject it and submit the change again."
            : $"{labels(string.Join(',', ChangedFields)) ?? string.Join(", ", ChangedFields)} changed after this re-review was raised, "
              + "so approving it would overwrite a newer value. Reject it and submit the change again.";
    }

    /// <summary>
    /// For a change the effective-date job sent back for review: re-checks, at approval, that every column
    /// still holds what the re-review showed. A change that was never returned for review passes untouched —
    /// immediate approvals behave exactly as before. Read-only.
    /// </summary>
    public static async Task<ReviewCheck> CheckUnchangedSinceReviewAsync(
        ZayraDbContext db, IDataProtector? protector, Guid tenantId, Guid changeId, Employee employee,
        IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var snapshots = await db.EmployeeHistories.AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.EmployeeId == employee.Id && h.EventType == ReturnedForReviewEventType)
            .OrderByDescending(h => h.CreatedAtUtc)
            .Select(h => h.SnapshotJson)
            .ToListAsync(ct);
        foreach (var json in snapshots)
        {
            if (!TryReadSnapshot(json, out var id, out var sealedReview) || id != changeId) continue;
            var reviewed = protector is null ? null : Unprotect(protector, changeId, sealedReview);
            if (reviewed is null) return new ReviewCheck(false, keys.ToList());
            var profile = await db.EmployeePayrollProfiles
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && !x.IsDeleted, ct);
            return new ReviewCheck(true, Drifted(reviewed, Capture(employee, profile, keys)));
        }
        return new ReviewCheck(true, Array.Empty<string>());
    }

    /// <summary>SnapshotJson of the <see cref="ScheduledEventType"/> history row.</summary>
    public static string SnapshotJson(Guid changeId, string? sealedBaseline) =>
        JsonSerializer.Serialize(new ScheduledSnapshot(changeId, sealedBaseline));

    /// <summary>Reads a <see cref="ScheduledEventType"/> history row's SnapshotJson.</summary>
    public static bool TryReadSnapshot(string? snapshotJson, out Guid changeId, out string? sealedBaseline)
    {
        changeId = Guid.Empty;
        sealedBaseline = null;
        if (string.IsNullOrWhiteSpace(snapshotJson)) return false;
        try
        {
            var snapshot = JsonSerializer.Deserialize<ScheduledSnapshot>(snapshotJson);
            if (snapshot is null || snapshot.ChangeRequestId == Guid.Empty) return false;
            changeId = snapshot.ChangeRequestId;
            sealedBaseline = snapshot.Baseline;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>True when <paramref name="key"/> names a readable column (used by the wiring test).</summary>
    public static bool CanRead(string key) =>
        EmployeeChangeApplier.PayrollProfileKeys.Contains(key) ? ProfileColumn(key) is not null : EmployeeProperty(key) is not null;

    private static bool TryReadEmployee(Employee employee, string key, out string value)
    {
        value = string.Empty;
        var property = EmployeeProperty(key);
        if (property is null) return false;
        value = Canonical(property.GetValue(employee));
        return true;
    }

    // The patch keys are the camelCase form of the Employee property the applier writes
    // ("bankIban" -> BankIban). EmployeeChangeBaselineTests pins that for every editable key.
    private static PropertyInfo? EmployeeProperty(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var name = char.ToUpperInvariant(key[0]) + key[1..];
        var property = typeof(Employee).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property is { CanRead: true } ? property : null;
    }

    // Columns on EmployeePayrollProfile that payroll pays or files from. Bank details live in both
    // homes (EmployeeBankProfileSync), so a bank key is compared on BOTH: an IBAN edited on the profile
    // alone is still a change to the account payroll pays into.
    private static Func<EmployeePayrollProfile, object?>? ProfileColumn(string key) => key switch
    {
        "bankIban" => p => p.Iban,
        "bankName" => p => p.BankName,
        "socialInsuranceReference" => p => p.SocialInsuranceReference,
        _ => null,
    };

    private static string Canonical(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        // PostgreSQL keeps microseconds; compare at that precision so a round trip is not a change.
        DateTime dt => new DateTime(dt.Ticks - dt.Ticks % 10, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.############################", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FieldName(string key)
    {
        var colon = key.IndexOf(':');
        return colon < 0 ? key : key[(colon + 1)..];
    }

    private sealed record Envelope(Guid ChangeId, Dictionary<string, string> Values);

    private sealed record ScheduledSnapshot(
        [property: System.Text.Json.Serialization.JsonPropertyName("changeRequestId")] Guid ChangeRequestId,
        [property: System.Text.Json.Serialization.JsonPropertyName("baseline")] string? Baseline);
}
