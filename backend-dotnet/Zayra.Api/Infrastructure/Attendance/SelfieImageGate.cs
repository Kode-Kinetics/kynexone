namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// The process-wide limit on selfie image work (decode, re-encode). Registered as a singleton: at most
/// <see cref="Concurrency"/> uploads decode at once on this instance, and an upload that finds every slot taken is
/// answered at once with 429 <c>selfie_busy</c> rather than queued (a queue of decodes is how the memory runs out).
/// </summary>
public sealed class SelfieImageGate
{
    public const int DefaultConcurrency = 2;

    private readonly SemaphoreSlim _slots;

    public SelfieImageGate(int concurrency = DefaultConcurrency)
    {
        Concurrency = Math.Clamp(concurrency, 1, 64);
        _slots = new SemaphoreSlim(Concurrency, Concurrency);
    }

    public int Concurrency { get; }

    /// <summary>Takes a slot without waiting; false when every slot is busy.</summary>
    public bool TryEnter() => _slots.Wait(0);

    public void Exit() => _slots.Release();
}
