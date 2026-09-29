using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests;

/// <summary>
/// Real-Postgres proof that Saudi bank-export generation is serialized by the company-level
/// pg_advisory_xact_lock inside an execution-strategy transaction — not by check-then-act. Each
/// contender uses its own DbContext, exactly like two concurrent requests.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class SaudiBankExportIntegrationTests
{
    private readonly PostgresFixture _fx;
    public SaudiBankExportIntegrationTests(PostgresFixture fx) => _fx = fx;

    private async Task<(Guid Tenant, Guid Company)> SeedTenantAsync()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var saved = await new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses())
            .SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default);
        saved.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (tenant, company);
    }

    private async Task<SaudiBankExportResult<SaudiBankExportGeneratedDto>> GenerateOnOwnContext(
        Guid tenant, Guid batch, SaudiBankExportBatchRequest request)
    {
        await using var db = _fx.CreateDb();
        return await new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses())
            .GenerateAsync(tenant, Guid.NewGuid(), batch, request, default);
    }

    [Fact]
    public async Task ParallelGenerate_SameBatchSameRequest_PersistsExactlyOneArtifact()
    {
        var (tenant, company) = await SeedTenantAsync();
        Guid batch;
        await using (var db = _fx.CreateDb())
            batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var request = SaudiBankExportTestData.Request("7001");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => GenerateOnOwnContext(tenant, batch, request)));

        results.Should().OnlyContain(r => r.Outcome == SaudiBankExportOutcome.Ok,
            "the loser(s) wait on the advisory lock, then see the frozen artifact and return it");
        results.Select(r => r.Value!.Id).Distinct().Should().ContainSingle();

        await using var check = _fx.CreateDb();
        (await check.BankTransferFiles.CountAsync(f => f.TenantId == tenant && f.PaymentBatchId == batch)).Should().Be(1);
        (await check.PayrollAuditLogs.CountAsync(a => a.TenantId == tenant && a.Action == "payroll.bank_export.generated"))
            .Should().Be(1, "the artifact and its audit row are written atomically, once");
    }

    [Fact]
    public async Task ParallelGenerate_TwoBatchesSameReference_ExactlyOneWins()
    {
        var (tenant, company) = await SeedTenantAsync();
        Guid batchA, batchB;
        await using (var db = _fx.CreateDb())
        {
            batchA = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
            batchB = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 10);
        }
        var request = SaudiBankExportTestData.Request("7002");

        var results = await Task.WhenAll(
            GenerateOnOwnContext(tenant, batchA, request),
            GenerateOnOwnContext(tenant, batchB, request));

        results.Count(r => r.Outcome == SaudiBankExportOutcome.Ok).Should().Be(1);
        var loser = results.Single(r => r.Outcome != SaudiBankExportOutcome.Ok);
        loser.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        loser.Validation!.Errors.Should().Contain(e => e.Code == "batch_reference_in_use");

        await using var check = _fx.CreateDb();
        (await check.BankTransferFiles.CountAsync(f => f.TenantId == tenant && f.FileName.StartsWith(SaudiBankExportService.ArtifactPrefix)))
            .Should().Be(1);
    }

    [Fact]
    public async Task Download_AfterGenerate_ReturnsVerifiedZip_OnRetryingProvider()
    {
        var (tenant, company) = await SeedTenantAsync();
        Guid batch;
        await using (var db = _fx.CreateDb())
            batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 11);
        (await GenerateOnOwnContext(tenant, batch, SaudiBankExportTestData.Request("7003"))).Outcome
            .Should().Be(SaudiBankExportOutcome.Ok);

        await using var dlDb = _fx.CreateDb();
        var dl = await new SaudiBankExportService(dlDb).DownloadAsync(tenant, Guid.NewGuid(), batch, default);

        dl.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        dl.Value!.ZipBytes.Should().NotBeEmpty();
    }
}
