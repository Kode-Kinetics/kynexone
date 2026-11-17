using System.Net;
using FluentAssertions;
using Zayra.Api.Application.AI;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Tests;

/// <summary>A provider's API key is sent to that provider only. A missing key is a configuration error, never a reason to use another vendor's key.</summary>
public class LlmClientKeyIsolationTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static AiOptions Options(string anthropicKey, string openAiKey) => new(
        "anthropic", "model", anthropicKey, openAiKey, string.Empty, string.Empty, 4096, true, false);

    [Fact]
    public async Task Anthropic_WithOnlyAnOpenAiKey_FailsWithAConfigurationError_AndSendsNothing()
    {
        var handler = new RecordingHandler();
        var client = new LlmClient(new HttpClient(handler), Options(anthropicKey: string.Empty, openAiKey: "sk-openai-only"));

        var response = await client.CompleteAsync(new LlmRequest("anthropic", "model", "system", "user", 256), CancellationToken.None);

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("Anthropic API key not configured");
        handler.Requests.Should().BeEmpty("no request, and so no key, may leave for the other vendor");
    }

    [Fact]
    public async Task OpenAi_WithOnlyAnAnthropicKey_FailsWithAConfigurationError_AndSendsNothing()
    {
        var handler = new RecordingHandler();
        var client = new LlmClient(new HttpClient(handler), Options(anthropicKey: "sk-anthropic-only", openAiKey: string.Empty));

        var response = await client.CompleteAsync(new LlmRequest("openai", "model", "system", "user", 256), CancellationToken.None);

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("OpenAI API key not configured");
        handler.Requests.Should().BeEmpty("no request, and so no key, may leave for the other vendor");
    }
}
