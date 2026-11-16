using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.Leave;

namespace Zayra.Api.Tests;

/// <summary>The scheduled leave accrual ships OFF (CTO decision 2026-11-12): a deploy must never change every tenant's balances silently.</summary>
public sealed class LeaveAccrualDefaultOffTests
{
    private sealed class ForbiddenScopeFactory : IServiceScopeFactory
    {
        public int Opened;
        public IServiceScope CreateScope() { Opened++; throw new InvalidOperationException("The scheduler must not touch the database while it is off."); }
    }

    private static LeaveAccrualOptions Bind(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build()
               .GetSection(LeaveAccrualOptions.SectionName).Get<LeaveAccrualOptions>() ?? new LeaveAccrualOptions();

    [Fact]
    public void WithNoSetting_TheJobIsOff()
        => Bind([]).Enabled.Should().BeFalse();

    [Fact]
    public void OnlyAnExplicitSetting_SwitchesItOn()
    {
        Bind(new() { ["LeaveAccrual:Enabled"] = "true" }).Enabled.Should().BeTrue();
        Bind(new() { ["LeaveAccrual:Enabled"] = "false" }).Enabled.Should().BeFalse("the kill switch keeps its meaning");
    }

    [Fact]
    public async Task WithNoSetting_TheSchedulerEnqueuesNothing_AndNeverOpensAScope()
    {
        var scopes = new ForbiddenScopeFactory();
        var scheduler = new LeaveAccrualScheduler(scopes, new LeaveAccrualOptions { InitialDelay = TimeSpan.Zero }, TimeProvider.System,
            NullLogger<LeaveAccrualScheduler>.Instance);

        await scheduler.StartAsync(default);
        await Task.Delay(200);
        await scheduler.StopAsync(default);

        scopes.Opened.Should().Be(0);
    }
}
