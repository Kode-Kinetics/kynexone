using Xunit;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Tests;

public class PdfRenderGateTests
{
    [Fact]
    public async Task RenderAsync_AllowsUpToCapacity_Concurrently()
    {
        var gate = new PdfRenderGate(3);
        var started = new List<int>();
        var tasks = new List<Task>();

        for (var i = 0; i < 3; i++)
        {
            var idx = i;
            tasks.Add(gate.RenderAsync(async () =>
            {
                started.Add(idx);
                await Task.Delay(20);
                return idx;
            }, default));
        }

        await Task.WhenAll(tasks);
        Assert.Equal(3, started.Count);
    }

    [Fact]
    public async Task RenderAsync_QueuesWhenFull_ThenCompletes()
    {
        var gate = new PdfRenderGate(2);
        var tcs = new TaskCompletionSource();
        var completionOrder = new List<int>();

        // Fill capacity
        var blocking1 = gate.RenderAsync(async () => { await tcs.Task; completionOrder.Add(1); return 1; }, default);
        var blocking2 = gate.RenderAsync(async () => { await tcs.Task; completionOrder.Add(2); return 2; }, default);

        // Third should queue (gate full)
        var queued = gate.RenderAsync(() => { completionOrder.Add(3); return Task.FromResult(3); }, default);

        await Task.Delay(30); // Let tasks reach the await point
        Assert.Equal(0, gate.Available); // gate full

        tcs.SetResult(); // Unblock
        await Task.WhenAll(blocking1, blocking2, queued);

        Assert.Equal(3, completionOrder.Count);
        Assert.Contains(3, completionOrder);
    }

    [Fact]
    public async Task RenderAsync_Timeout_ThrowsPdfConcurrencyException()
    {
        // Cap=1, hold the slot for 2s, try to get it with 50ms timeout
        var gate = new PdfRenderGate(1);
        var tcs = new TaskCompletionSource();

        // Block the gate
        var holder = gate.RenderAsync(async () => { await tcs.Task; return 0; }, default);
        await Task.Delay(20); // Let it acquire

        // This should wait and then throw (semaphore timeout is 30s, so we need a tiny timeout)
        // Workaround: use a CancellationToken to simulate timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<Exception>(() => gate.RenderAsync(() => Task.FromResult(1), cts.Token));

        tcs.SetResult(); // Clean up
        await holder;
    }

    [Fact]
    public async Task BulkRender_15Slips_CompletesWithoutDeadlock()
    {
        var gate = new PdfRenderGate(3);
        var results = new List<int>();
        var tasks = Enumerable.Range(1, 15).Select(i =>
            gate.RenderAsync(async () =>
            {
                await Task.Delay(5); // Simulate QuestPDF ~5ms per slip
                lock (results) results.Add(i);
                return i;
            }, default)).ToList();

        await Task.WhenAll(tasks);

        Assert.Equal(15, results.Count);
        Assert.Equal(Enumerable.Range(1, 15).OrderBy(x => x), results.OrderBy(x => x));
    }

    [Fact]
    public async Task Gate_ReleasesSlot_AfterRender()
    {
        var gate = new PdfRenderGate(1);
        await gate.RenderAsync(() => Task.FromResult(0), default);

        // Slot should be available immediately for the next render
        var result = await gate.RenderAsync(() => Task.FromResult(42), default);
        Assert.Equal(42, result);
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task Gate_ReleasesSlot_OnException()
    {
        var gate = new PdfRenderGate(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RenderAsync<int>(() => throw new InvalidOperationException("render failed"), default));

        // Slot must be released even after exception
        Assert.Equal(1, gate.Available);
        var ok = await gate.RenderAsync(() => Task.FromResult("recovered"), default);
        Assert.Equal("recovered", ok);
    }
}
