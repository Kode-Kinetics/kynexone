using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// Serialises concurrent creates of the SAME person. The pre-check in <c>CreateAsync</c> is a read; two requests
/// that arrive together both read "no match" and both insert, because the identity columns are deliberately not
/// unique (an import must land and flag both rows; a rehire or a second legal entity can legitimately repeat an ID).
/// A transaction-scoped advisory lock on a hash of the normalised identity closes only the check-then-insert window:
/// the second request waits for the first to commit, then re-reads and sees the committed row.
///
/// <para>One key per non-blank strong identity value (<c>EMPDUP|tenant|field|value</c>), or one name+date-of-birth
/// key when the request carries none. Keys are sorted before locking so two requests that share several values can
/// never deadlock on each other. A no-op on a non-relational provider. Must run inside the create transaction
/// (<c>AdvisoryXactLockGuardInterceptor</c> refuses it otherwise).</para>
/// </summary>
public static class EmployeeIdentityLock
{
    /// <summary>The sorted, de-duplicated 64-bit advisory keys for a probe. Pure: no database access.</summary>
    public static IReadOnlyList<long> KeysFor(Guid tenantId, DuplicateProbe probe)
    {
        var keys = new SortedSet<long>();
        foreach (var (field, value) in probe.IdentityValues)
        {
            var canonical = IdentityValueNormalizer.Canonical(value);
            if (canonical.Length == 0) continue;
            keys.Add(Hash($"EMPDUP|{tenantId:N}|{field.Trim().ToLowerInvariant()}|{canonical}"));
        }
        if (keys.Count == 0)
        {
            var name = NormalisedName(probe.EnglishName);
            if (name.Length == 0) name = NormalisedName(probe.ArabicName);
            if (name.Length > 0)
                keys.Add(Hash($"EMPDUP|{tenantId:N}|name|{name}|{probe.DateOfBirth?.ToString("yyyy-MM-dd") ?? "-"}"));
        }
        return keys.ToList();
    }

    /// <summary>Takes every key in order. Call inside the create transaction, BEFORE the second duplicate read.</summary>
    public static async Task AcquireAsync(ZayraDbContext db, Guid tenantId, DuplicateProbe probe, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        foreach (var key in KeysFor(tenantId, probe))
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    /// <summary>
    /// The in-transaction re-check. Takes the identity locks, then re-reads the detector. A match that was NOT in the
    /// pre-check (<paramref name="alreadySeen"/>) was committed by a concurrent create while this one waited: unless
    /// the caller acknowledged duplicates, refuse with the same <see cref="DuplicatePersonException"/> the pre-check
    /// raises (the controller maps it to the existing advisory 409), so the second of two racing creates never lands.
    /// </summary>
    public static async Task AcquireAndRecheckAsync(
        ZayraDbContext db, IEmployeeDuplicateDetector detector, Guid tenantId, DuplicateProbe probe,
        IReadOnlyCollection<int> alreadySeen, bool acknowledged, CancellationToken ct)
    {
        await AcquireAsync(db, tenantId, probe, ct);
        if (acknowledged || !db.Database.IsNpgsql()) return;
        var fresh = (await detector.FindAsync(tenantId, probe, ct))
            .Where(m => !alreadySeen.Contains(m.EmployeeId))
            .ToList();
        if (fresh.Count > 0) throw new DuplicatePersonException(fresh);
    }

    private static string NormalisedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.ToString();
    }

    private static long Hash(string text) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
}
