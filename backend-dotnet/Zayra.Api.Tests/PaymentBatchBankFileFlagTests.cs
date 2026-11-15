using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The payment-batch list says, per batch, whether the Saudi bank export applies to the batch's legal entity
/// (<c>bankFileAvailable</c>) and whether a bank export exists for it (<c>bankFileGenerated</c>). For a Saudi
/// company the generated XML is an INTERNAL payroll register, not a bank file, and it sets WpsStatus to Generated;
/// the run pipeline reads these two flags so the register alone never reads as "Paid / Exported: Done".
///
/// <para>Boundary: EF InMemory and the controller called directly, like <see cref="KsaWpsReviewFixesTests"/>.</para>
/// </summary>
public class PaymentBatchBankFileFlagTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, Guid Batch, SaudiBankExportService Svc)> ArrangeSaudi()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses());
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (db, tenant, company, batch, svc);
    }

    private static async Task<JsonElement> BatchView(PayrollController ctrl, Guid batchId)
    {
        var list = await ctrl.ListPaymentBatches(null, default);
        var json = JsonSerializer.Serialize(list.Should().BeOfType<OkObjectResult>().Subject.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == batchId);
    }

    private static async Task GenerateRegister(ZayraDbContext db, Guid tenant, Guid batch) =>
        (await KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid()).GenerateWps(batch, true, false, default))
            .Should().BeOfType<OkObjectResult>("the internal register is generated through the same endpoint the screen uses");

    private static async Task GenerateAnb(SaudiBankExportService svc, Guid tenant, Guid batch)
    {
        var gen = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, SaudiBankExportTestData.Request(), default);
        gen.Outcome.Should().Be(SaudiBankExportOutcome.Ok, string.Join("; ", gen.Validation?.Errors.Select(e => $"{e.Code}: {e.Message}") ?? Array.Empty<string>()));
    }

    [Fact]
    public async Task A_saudi_batch_with_only_the_internal_register_has_no_bank_file()
    {
        var (db, tenant, _, batch, _) = await ArrangeSaudi();
        await GenerateRegister(db, tenant, batch);

        var view = await BatchView(KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid()), batch);

        view.GetProperty("wpsStatus").GetString().Should().Be(WpsStatuses.Generated, "generating the register is what sets Generated");
        view.GetProperty("bankFileAvailable").GetBoolean().Should().BeTrue("the batch's company is Saudi, so its bank file is the bank export");
        view.GetProperty("bankFileGenerated").GetBoolean().Should().BeFalse("only the internal register exists; no bank file was generated");
    }

    [Fact]
    public async Task A_saudi_batch_with_an_anb_export_has_its_bank_file_with_or_without_the_register()
    {
        var (db, tenant, _, batch, svc) = await ArrangeSaudi();
        await GenerateAnb(svc, tenant, batch);

        var anbOnly = await BatchView(KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid()), batch);
        anbOnly.GetProperty("bankFileAvailable").GetBoolean().Should().BeTrue();
        anbOnly.GetProperty("bankFileGenerated").GetBoolean().Should().BeTrue();

        await GenerateRegister(db, tenant, batch);
        var both = await BatchView(KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid()), batch);
        both.GetProperty("bankFileGenerated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_batch_of_a_company_that_is_not_saudi_has_no_bank_export_available()
    {
        var (db, tenant, company, batch, _) = await ArrangeSaudi();
        var c = await db.Companies.SingleAsync(x => x.Id == company);
        c.CountryCode = "AE";
        c.DefaultCurrency = "AED";
        await db.SaveChangesAsync();

        var view = await BatchView(KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid()), batch);

        view.GetProperty("bankFileAvailable").GetBoolean().Should().BeFalse("the Saudi bank export does not apply to this company");
        view.GetProperty("bankFileGenerated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_saudi_batch_is_unavailable_when_the_platform_switched_the_export_off_for_the_tenant()
    {
        var (db, tenant, _, batch, _) = await ArrangeSaudi();
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenant, FeatureKey = FeatureKeys.SaudiBankExport, IsEnabled = false });
        await db.SaveChangesAsync();
        await GenerateRegister(db, tenant, batch);

        var view = await BatchView(KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid()), batch);

        view.GetProperty("bankFileAvailable").GetBoolean().Should().BeFalse();
        view.GetProperty("bankFileGenerated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_saudi_batch_is_unavailable_when_the_operator_killed_the_export_everywhere()
    {
        var (db, tenant, _, batch, _) = await ArrangeSaudi();
        var killed = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{SaudiBankExportActivation.Section}:Enabled"] = "false" })
            .Build();

        var view = await BatchView(KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid(), configuration: killed), batch);

        view.GetProperty("bankFileAvailable").GetBoolean().Should().BeFalse("the operator's switch closes the export for every tenant");
    }

    [Fact]
    public async Task Every_batch_of_the_page_carries_its_own_flags()
    {
        var (db, tenant, company, exported, svc) = await ArrangeSaudi();
        var registerOnly = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 10);
        await GenerateAnb(svc, tenant, exported);
        await GenerateRegister(db, tenant, registerOnly);
        var otherTenant = Guid.NewGuid();
        var foreignCompany = await SaudiBankExportTestData.SeedCompanyAsync(db, otherTenant);
        await SaudiBankExportTestData.SeedBatchAsync(db, otherTenant, foreignCompany, 9);

        var ctrl = KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid());
        var list = await ctrl.ListPaymentBatches(null, default);
        var json = JsonSerializer.Serialize(((ObjectResult)list).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var rows = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();

        rows.Should().HaveCount(2, "another tenant's batch is never listed");
        rows.Single(r => r.GetProperty("id").GetGuid() == exported).GetProperty("bankFileGenerated").GetBoolean().Should().BeTrue();
        rows.Single(r => r.GetProperty("id").GetGuid() == registerOnly).GetProperty("bankFileGenerated").GetBoolean().Should().BeFalse();
        rows.Should().OnlyContain(r => r.GetProperty("bankFileAvailable").GetBoolean());
    }
}

/// <summary>
/// Real Postgres: the new read translates (Guid arrays, a nullable company id), keeps the flags right per batch, and the
/// number of SQL reads for the page does not grow with the number of batches (no per-batch round trip).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PaymentBatchBankFileFlagPostgresTests
{
    private readonly PostgresFixture _fx;
    public PaymentBatchBankFileFlagPostgresTests(PostgresFixture fx) => _fx = fx;

    private ZayraDbContext CountingDb(List<string> sink) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
            .AddInterceptors(new ReadCapture(sink))
            .Options);

    private sealed class ReadCapture(List<string> sink) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (sink) sink.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private async Task<(List<JsonElement> Rows, int Reads)> ListAsync(Guid tenant)
    {
        var sql = new List<string>();
        await using var db = CountingDb(sql);
        var list = await KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid()).ListPaymentBatches(null, default);
        var json = JsonSerializer.Serialize(list.Should().BeOfType<OkObjectResult>().Subject.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (JsonDocument.Parse(json).RootElement.EnumerateArray().ToList(), sql.Count);
    }

    [Fact]
    public async Task The_flags_are_right_on_postgres_and_the_reads_do_not_grow_with_the_batches()
    {
        Guid tenant, saudi, exported;
        await using (var db = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(db);
            saudi = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
            var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses());
            (await svc.SaveSettingsAsync(tenant, saudi, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
                .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
            exported = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, saudi, 9);
        }
        await using (var db = _fx.CreateDb())
            (await new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses())
                .GenerateAsync(tenant, Guid.NewGuid(), exported, SaudiBankExportTestData.Request($"{Random.Shared.Next(1_000_000, 9_999_999)}"), default))
                .Outcome.Should().Be(SaudiBankExportOutcome.Ok);

        var (one, readsForOne) = await ListAsync(tenant);
        one.Should().ContainSingle();
        one[0].GetProperty("bankFileAvailable").GetBoolean().Should().BeTrue();
        one[0].GetProperty("bankFileGenerated").GetBoolean().Should().BeTrue();

        Guid notExported, nonSaudiBatch;
        await using (var db = _fx.CreateDb())
        {
            notExported = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, saudi, 10);
            var other = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
            var c = await db.Companies.SingleAsync(x => x.Id == other);
            c.CountryCode = "AE";
            await db.SaveChangesAsync();
            nonSaudiBatch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, other, 9);
        }

        var (three, readsForThree) = await ListAsync(tenant);
        three.Should().HaveCount(3);
        var byId = three.ToDictionary(r => r.GetProperty("id").GetGuid());
        byId[exported].GetProperty("bankFileGenerated").GetBoolean().Should().BeTrue();
        byId[notExported].GetProperty("bankFileAvailable").GetBoolean().Should().BeTrue();
        byId[notExported].GetProperty("bankFileGenerated").GetBoolean().Should().BeFalse();
        byId[nonSaudiBatch].GetProperty("bankFileAvailable").GetBoolean().Should().BeFalse();
        byId[nonSaudiBatch].GetProperty("bankFileGenerated").GetBoolean().Should().BeFalse();
        readsForThree.Should().Be(readsForOne, "the page is read set-based: three batches cost the same reads as one");
    }
}
