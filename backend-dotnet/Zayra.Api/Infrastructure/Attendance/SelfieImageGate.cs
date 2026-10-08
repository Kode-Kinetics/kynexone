namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// The process-wide limit on selfie image work (decode, re-encode). Registered as a singleton with
/// <see cref="ConfigKey"/> slots (default <see cref="DefaultConcurrency"/>: one decode at a time on a small instance).
/// An upload waits up to <see cref="DefaultWait"/> for a slot and is then answered 429 <c>selfie_busy</c> rather than
/// queued without limit (a queue of decodes is how the memory runs out).
/// </summary>
public sealed class SelfieImageGate
{
    /// <summary>Configuration key for the slot count (review 2, item 3).</summary>
    public const string ConfigKey = "Selfie:ImageConcurrency";
    public const int DefaultConcurrency = 1;
    /// <summary>How long an upload waits for a free slot before it is answered busy (review 2, item 7).</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _slots;

    public SelfieImageGate(int concurrency = DefaultConcurrency)
    {
        Concurrency = Math.Clamp(concurrency, 1, 64);
        _slots = new SemaphoreSlim(Concurrency, Concurrency);
    }

    public int Concurrency { get; }

    /// <summary>Takes a slot without waiting; false when every slot is busy.</summary>
    public bool TryEnter() => _slots.Wait(0);

    /// <summary>Waits up to <paramref name="wait"/> for a slot; false when none came free in time.</summary>
    public Task<bool> EnterAsync(TimeSpan wait, CancellationToken ct) => _slots.WaitAsync(wait, ct);

    public void Exit() => _slots.Release();
}
