using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Tests;

/// <summary>
/// A fresh deployment must be able to send and accept an offer without anyone setting an environment
/// variable: both offer switches default ON in appsettings.json, an explicit false still turns them off
/// with a clear message, and the effective values are visible in the readiness detail.
/// No database or network: configuration and the in-memory provider only.
/// </summary>
public sealed class OfferFlagDefaultsTests
{
    private static IConfiguration ShippedSettings(params (string Key, string Value)[] overrides)
    {
        var file = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "appsettings.json");
        var builder = new ConfigurationBuilder().AddJsonFile(file, optional: false);
        if (overrides.Length > 0)
            builder.AddInMemoryCollection(overrides.ToDictionary(o => o.Key, o => (string?)o.Value));
        return builder.Build();
    }

    [Fact]
    public void Shipped_appsettings_turn_both_offer_switches_on()
    {
        var config = ShippedSettings();
        config.GetValue<bool>(OfferLifecycleService.EnablementKey).Should().BeTrue();
        config.GetValue<bool>(OfferDispatchWorker.EnablementKey).Should().BeTrue();
        new OfferLifecycleService(null!, config).Enabled.Should().BeTrue();
    }

    [Fact]
    public void An_explicit_false_still_disables_the_managed_lifecycle_with_the_clear_message()
    {
        var config = ShippedSettings((OfferLifecycleService.EnablementKey, "false"));
        var service = new OfferLifecycleService(null!, config);
        service.Enabled.Should().BeFalse();
        var act = () => service.RequireEnabled();
        act.Should().Throw<OfferLifecycleException>()
            .Where(e => e.Code == "managed_offer_lifecycle_disabled")
            .WithMessage("Managed offer creation and issuance are not enabled for this deployment.");
    }

    [Fact]
    public async Task Readiness_detail_shows_the_effective_offer_switch_values()
    {
        await using var on = InMemory();
        var enabled = await ProductionReadinessEvidence.BuildReadinessAsync(on, ShippedSettings(), CancellationToken.None);
        enabled.Dependencies.OfferLifecycle.Should().NotBeNull();
        enabled.Dependencies.OfferLifecycle!.ManagedLifecycleEnabled.Should().BeTrue();
        enabled.Dependencies.OfferLifecycle.DispatchWorkerEnabled.Should().BeTrue();

        await using var off = InMemory();
        var disabled = await ProductionReadinessEvidence.BuildReadinessAsync(off,
            ShippedSettings((OfferLifecycleService.EnablementKey, "false"), (OfferDispatchWorker.EnablementKey, "false")), CancellationToken.None);
        disabled.Dependencies.OfferLifecycle!.ManagedLifecycleEnabled.Should().BeFalse();
        disabled.Dependencies.OfferLifecycle.DispatchWorkerEnabled.Should().BeFalse();
    }

    private static ZayraDbContext InMemory() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase($"offer-flags-{Guid.NewGuid():N}").Options);
}
