namespace Zayra.Api.Infrastructure.Documents;

// Singleton semaphore that caps concurrent QuestPDF renders.
// Prevents OOM on the 512MB Render free tier when multiple users trigger bulk export or previews simultaneously.
public sealed class PdfRenderGate(int capacity)
{
    private readonly SemaphoreSlim _sem = new(capacity, capacity);

    public int Capacity => capacity;
    public int Available => _sem.CurrentCount;

    public async Task<T> RenderAsync<T>(Func<Task<T>> render, CancellationToken ct)
    {
        if (!await _sem.WaitAsync(TimeSpan.FromSeconds(30), ct))
            throw new PdfConcurrencyException(
                $"PDF render queue is full ({capacity} concurrent renders). Retry in a moment.");
        try { return await render(); }
        finally { _sem.Release(); }
    }
}

public sealed class PdfConcurrencyException(string message) : Exception(message);
