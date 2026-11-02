using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// PAY-04 — SQLite twin of <see cref="PayrollRunConcurrencyPostgresTests"/> (which needs Docker): the same
/// end state reached through the serialized path, sequentially. SQLite is single-writer, so this proves the
/// state/refusal contract, not the race.
/// </summary>
public class PayrollRunBatchStateTests
{
    [Fact]
    public async Task SecondBatchCreate_IsRefused_AndALiveBatchIsNeverDuplicated()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, Array.Empty<(int, string, string, decimal)>(), net: 5_000m);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();

        var first = await k.Payroll(null, null, null, "payroll.export").CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), default);
        var second = await k.Payroll(null, null, null, "payroll.export").CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), default);

        first.Should().BeOfType<CreatedResult>();
        second.Should().BeOfType<ConflictObjectResult>();
        k.Db.ChangeTracker.Clear();
        (await k.Db.PayrollPaymentBatches.AsNoTracking().CountAsync(b => b.PayrollRunId == run.Id && b.WpsStatus != WpsStatuses.Voided)).Should().Be(1);
    }
}
