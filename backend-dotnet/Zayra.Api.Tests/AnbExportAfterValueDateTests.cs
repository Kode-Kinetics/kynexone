using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests;

/// <summary>
/// Final gate P2-20: the value-date checks (Friday/Saturday, a past date) apply only when a NEW export is generated. Asking for
/// the same ANB export again after its value date has passed returns the frozen export, as on main, never
/// "export_inputs_changed". A new export with a past or weekend date is still refused. Synthetic data.
/// </summary>
public class AnbExportAfterValueDateTests
{
    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SaudiBankExportBatchRequest Request(string paymentDate, string reference = "2026111701") =>
        new() { BatchReference = reference, PaymentDate = paymentDate };

    private static async Task<(ZayraDbContext Db, SaudiBankExportService Svc, MovableTime Clock, Guid Tenant, Guid Batch)> Arrange()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 11);
        // 09:00 UTC on Tuesday 17 November 2026 (12:00 in Riyadh).
        var clock = new MovableTime(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses(), clock);
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (db, svc, clock, tenant, batch);
    }

    [Fact]
    public async Task TheSameExportAskedAgainAfterItsValueDatePassed_ReturnsTheFrozenExport()
    {
        var (db, svc, clock, tenant, batch) = await Arrange();
        var request = Request("2026-11-19");   // Thursday
        var first = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, request, default);
        first.Outcome.Should().Be(SaudiBankExportOutcome.Ok, first.Error);

        clock.Now = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);   // the following Monday: the value date is past
        var again = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, request, default);

        again.Outcome.Should().Be(SaudiBankExportOutcome.Ok, again.Error);
        again.Value!.Id.Should().Be(first.Value!.Id);
        again.Value.Files.Should().BeEquivalentTo(first.Value.Files);
        db.BankTransferFiles.Count(f => f.PaymentBatchId == batch).Should().Be(1);
    }

    [Fact]
    public async Task ANewExport_WithAPastValueDate_IsStillRefused()
    {
        var (_, svc, _, tenant, batch) = await Arrange();

        var past = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, Request("2026-11-16"), default);

        past.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        past.Validation!.Errors.Should().Contain(e => e.Code == AnbConnectCsvGenerator.ValueDateInPast);
    }

    [Fact]
    public async Task ANewExport_WithAWeekendValueDate_IsStillRefused()
    {
        var (_, svc, _, tenant, batch) = await Arrange();

        var friday = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, Request("2026-11-20"), default);

        friday.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        friday.Validation!.Errors.Should().Contain(e => e.Code == AnbConnectCsvGenerator.ValueDateWeekend);
    }

    [Fact]
    public async Task ADifferentRequestAfterTheValueDatePassed_IsStillTheBatchAlreadyExportedConflict()
    {
        var (_, svc, clock, tenant, batch) = await Arrange();
        (await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, Request("2026-11-19"), default)).Outcome.Should().Be(SaudiBankExportOutcome.Ok);

        clock.Now = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        var other = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, Request("2026-11-19", "99"), default);

        other.Outcome.Should().Be(SaudiBankExportOutcome.Conflict);
        other.Error.Should().Be("batch_already_exported");
    }
}
