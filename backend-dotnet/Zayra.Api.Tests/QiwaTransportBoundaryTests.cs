using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.Qiwa;

namespace Zayra.Api.Tests;

/// <summary>Caller cancellation and network timeout behavior retained by the backend-only release.</summary>
public sealed class QiwaTransportBoundaryTests
{
    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw error;
    }
    private sealed class Factory(Exception error) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler(error)) { BaseAddress = new Uri("https://example.invalid") };
    }
    private static LiveQiwaApiAdapter Adapter(Exception error) => new(new Factory(error), NullLogger<LiveQiwaApiAdapter>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [QiwaLiveAdapterPolicy.PartnerAgreementKey] = "synthetic-test-agreement" }).Build());
    private static async Task<object?> Call(LiveQiwaApiAdapter adapter, string operation, CancellationToken token)
    {
        return operation switch
        {
            "token" => await adapter.AcquireAccessTokenAsync("synthetic-id", "synthetic-credential", "test", token),
            "push" => await adapter.PushEmployeeAsync("synthetic-token", new("E", "1", "NationalId", "SA", "Saudi", "1", "7-1", "W", "C"), Guid.NewGuid(), token),
            "status" => await adapter.GetEmployeeStatusAsync("synthetic-token", "7-1", "1", token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    [Theory]
    [InlineData("token")]
    [InlineData("push")]
    [InlineData("status")]
    public async Task CallerCancellation_PropagatesForEveryQiwaOperation(string operation)
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var call = () => Call(Adapter(new OperationCanceledException(cancelled.Token)), operation, cancelled.Token);
        await call.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("token")]
    [InlineData("push")]
    [InlineData("status")]
    public async Task HttpTimeout_RemainsAHandledNetworkFailure(string operation)
    {
        var result = await Call(Adapter(new TaskCanceledException("synthetic timeout", new TimeoutException())), operation, CancellationToken.None);
        if (operation == "token") result.Should().BeNull();
        else
        {
            var failure = result.Should().BeOfType<QiwaApiResult>().Subject;
            failure.Success.Should().BeFalse(); failure.ErrorCode.Should().Be("NETWORK_ERROR");
        }
    }

}
