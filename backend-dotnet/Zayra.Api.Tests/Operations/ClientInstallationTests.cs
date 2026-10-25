using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Controllers.Public;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Tests.Licensing;

namespace Zayra.Api.Tests.Operations;

/// <summary>
/// WP7/WP8 for a client installation: the anonymous installation endpoint, the local document root rule and
/// maintenance mode.
/// </summary>
public class ClientInstallationTests : IDisposable
{
    private readonly ServiceProvider _services;

    public ClientInstallationTests()
    {
        var databaseName = $"installation-{Guid.NewGuid():N}";
        var collection = new ServiceCollection();
        collection.AddDbContext<ZayraDbContext>(o => o.UseInMemoryDatabase(databaseName));
        _services = collection.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private ZayraDbContext NewDb() => _services.CreateScope().ServiceProvider.GetRequiredService<ZayraDbContext>();

    private static IConfiguration Config(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
        .Build();

    private static async Task<PublicInstallationInfo> GetInstallation(ZayraDbContext db, IConfiguration config)
    {
        var controller = new PublicInstallationController(db, FixedLicenceService.Unlimited(), config);
        var result = await controller.Get(CancellationToken.None);
        return (PublicInstallationInfo)((OkObjectResult)result.Result!).Value!;
    }

    // ── GET /api/public/installation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Before_setup_the_installation_says_so()
    {
        var info = await GetInstallation(NewDb(), Config(("APP_URL", "https://hr.example.local")));

        info.SetupComplete.Should().BeFalse();
        info.OrganisationName.Should().BeNull();
    }

    [Fact]
    public async Task After_setup_it_names_the_organisation_and_the_public_device_address()
    {
        var db = NewDb();
        db.Tenants.Add(new Tenant { Name = "Al Noor Trading", Slug = "main" });
        await db.SaveChangesAsync();

        var info = await GetInstallation(db, Config(("APP_URL", "https://hr.example.local/")));

        info.SetupComplete.Should().BeTrue();
        info.OrganisationName.Should().Be("Al Noor Trading");
        info.PublicBaseUrl.Should().Be("https://hr.example.local");
        info.DeviceIngestUrl.Should().Be("https://hr.example.local/api/attendance/ingest");
        info.PushEnabled.Should().BeTrue();
        info.Licence.State.Should().Be("Valid");
    }

    /// <summary>
    /// The endpoint is anonymous: it may say no more than these fields. A new one (a limit, the installation id, a
    /// count, an internal host) is a deliberate decision, made by changing this list.
    /// </summary>
    [Fact]
    public async Task The_anonymous_answer_carries_exactly_the_published_fields()
    {
        var info = await GetInstallation(NewDb(), Config(("APP_URL", "https://hr.example.local")));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "setupComplete", "organisationName", "publicBaseUrl", "deviceIngestUrl", "pushEnabled", "licence");
        json.RootElement.GetProperty("licence").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("state");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("not-a-bool", true)]
    public void Push_is_on_unless_switched_off(string? setting, bool expected)
    {
        var config = setting is null ? Config() : Config((PublicInstallationController.PushEnabledConfigKey, setting));
        PublicInstallationController.IsPushEnabled(config).Should().Be(expected);
    }

    // ── Local document storage ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_local_document_root_must_be_absolute_existing_and_writable()
    {
        DocumentStorageRegistration.LocalRootProblem(null).Should().Contain("not set");
        DocumentStorageRegistration.LocalRootProblem("documents").Should().Contain("not an absolute path");
        DocumentStorageRegistration.LocalRootProblem(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"))
            .Should().Contain("does not exist");

        var root = Directory.CreateTempSubdirectory("kynexone-documents-");
        try
        {
            DocumentStorageRegistration.LocalRootProblem(root.FullName).Should().BeNull();
            root.EnumerateFileSystemInfos().Should().BeEmpty("the write probe cleans up after itself");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // ── PDF engine licence (WP14) ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Community")]
    [InlineData("Trial")]
    public void Outside_development_the_pdf_engine_needs_a_commercial_licence(string? configured)
    {
        var act = () => DocumentFonts.ConfigureLicence(configured, isDevelopment: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*QuestPdf__License*");
    }

    [Fact]
    public void A_commercial_licence_is_accepted_and_development_may_use_community()
    {
        try
        {
            DocumentFonts.ConfigureLicence("professional", isDevelopment: false).Should().Be(QuestPDF.Infrastructure.LicenseType.Professional);
            DocumentFonts.ConfigureLicence("Enterprise", isDevelopment: false).Should().Be(QuestPDF.Infrastructure.LicenseType.Enterprise);
            DocumentFonts.ConfigureLicence(null, isDevelopment: true).Should().Be(QuestPDF.Infrastructure.LicenseType.Community);
        }
        finally
        {
            DocumentFonts.ConfigureLicence(null, isDevelopment: true);
        }
    }

    // ── Maintenance mode ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/employees", false)]
    [InlineData("/api/payroll/runs", false)]
    [InlineData("/api/public/installation", true)]
    [InlineData("/api/organisation-admin/maintenance", true)]
    [InlineData("/api/auth/login", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/", true)]
    public void Maintenance_blocks_the_api_but_never_the_way_back(string path, bool exempt)
    {
        MaintenanceModeGate.IsExempt(new PathString(path)).Should().Be(exempt);
    }

    [Fact]
    public async Task Maintenance_is_off_until_switched_on_and_keeps_its_message()
    {
        (await MaintenanceState.ReadAsync(NewDb(), CancellationToken.None)).Should().Be(new MaintenanceStatus(false, null));

        await MaintenanceState.WriteAsync(NewDb(), true, "  Upgrading to 1.2.0  ", CancellationToken.None);
        (await MaintenanceState.ReadAsync(NewDb(), CancellationToken.None)).Should().Be(new MaintenanceStatus(true, "Upgrading to 1.2.0"));

        await MaintenanceState.WriteAsync(NewDb(), true, null, CancellationToken.None);
        (await MaintenanceState.ReadAsync(NewDb(), CancellationToken.None)).Message.Should().Be(MaintenanceState.DefaultMessage);

        await MaintenanceState.WriteAsync(NewDb(), false, null, CancellationToken.None);
        (await MaintenanceState.ReadAsync(NewDb(), CancellationToken.None)).On.Should().BeFalse();
    }

    [Fact]
    public async Task While_on_the_gate_answers_503_with_the_message_and_a_retry_hint()
    {
        var state = new MaintenanceState(_services.GetRequiredService<IServiceScopeFactory>());
        await state.SetAsync(NewDb(), true, "Back at 10:00", null, CancellationToken.None);
        var services = new ServiceCollection().AddSingleton<IMaintenanceState>(state).BuildServiceProvider();

        var blocked = await Invoke("/api/employees", services);
        blocked.Context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        blocked.Context.Response.Headers.RetryAfter.ToString().Should().Be("300");
        blocked.NextCalled.Should().BeFalse();
        using (var body = JsonDocument.Parse(blocked.Body))
        {
            body.RootElement.GetProperty("error").GetString().Should().Be(MaintenanceModeGate.ErrorCode);
            body.RootElement.GetProperty("message").GetString().Should().Be("Back at 10:00");
        }

        (await Invoke("/api/organisation-admin/maintenance", services)).NextCalled.Should().BeTrue();

        await state.SetAsync(NewDb(), false, null, null, CancellationToken.None);
        (await Invoke("/api/employees", services)).NextCalled.Should().BeTrue();
    }

    private static async Task<(HttpContext Context, bool NextCalled, string Body)> Invoke(string path, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        var stream = new MemoryStream();
        context.Response.Body = stream;
        var nextCalled = false;
        await MaintenanceModeGate.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });
        return (context, nextCalled, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }
}
