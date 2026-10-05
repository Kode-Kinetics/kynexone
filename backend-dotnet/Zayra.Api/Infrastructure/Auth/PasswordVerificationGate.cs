using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>Raised when the password-hashing gate stays full past its short wait. Mapped to 429.</summary>
public sealed class PasswordVerificationBusyException()
    : Exception("Sign-in is busy. Please try again in a moment.");

/// <summary>
/// Bounds how many 600k-iteration PBKDF2 computations (verify or re-hash) run at once in this process.
///
/// <para>Each one costs roughly half a CPU-second on the Starter instance. The per-IP login limiter
/// does not bound CPU — production allows 100 logins/min per IP and many clients arrive through the
/// same proxy — so a burst of logins (or a credential-stuffing run) could pin the CPU and starve every
/// other request. Callers wait briefly for a slot; when none frees up they get a 429 instead of
/// queueing unboundedly. Registered as a singleton; tests construct services without one (no gate).</para>
///
/// Config: <c>Auth:PasswordVerification:MaxConcurrency</c> (default 1 — one PBKDF2 at a time on the
/// half-CPU Starter instance) and <c>Auth:PasswordVerification:MaxWaitMs</c> (default 4500 — about
/// eight queued verifications before a 429).
/// </summary>
public sealed class PasswordVerificationGate : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _maxWait;

    public PasswordVerificationGate(int maxConcurrency = 1, TimeSpan? maxWait = null)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _maxWait = maxWait ?? TimeSpan.FromMilliseconds(4500);
    }

    public static PasswordVerificationGate FromConfiguration(IConfiguration config) => new(
        Math.Clamp(config.GetValue("Auth:PasswordVerification:MaxConcurrency", 1), 1, 64),
        TimeSpan.FromMilliseconds(Math.Clamp(config.GetValue("Auth:PasswordVerification:MaxWaitMs", 4500), 0, 30_000)));

    /// <summary>Runs <paramref name="work"/> inside a slot, or throws <see cref="PasswordVerificationBusyException"/>.</summary>
    public async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        if (!await _slots.WaitAsync(_maxWait, ct)) throw new PasswordVerificationBusyException();
        try { return work(); }
        finally { _slots.Release(); }
    }

    /// <summary>Gate-optional helper: with no gate (tests, tools) the work simply runs.</summary>
    public static Task<T> RunAsync<T>(PasswordVerificationGate? gate, Func<T> work, CancellationToken ct)
        => gate is null ? Task.FromResult(work()) : gate.RunAsync(work, ct);

    public void Dispose() => _slots.Dispose();
}
