using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Application.AI;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Tests;

/// <summary>
/// Pins the two shared client contracts every AI call site now depends on.
///
/// Both exist because of a live incident. Production runs a REASONING model
/// (deepseek-v4-pro:cloud) on Ollama Cloud. The setup assistant asked it for JSON without
/// constraining the output format, and treated an empty completion as a success — so when the
/// model spent its whole token budget reasoning and returned no content, the caller reported
/// "AI provider unavailable" for a provider that was up and answering chat. These tests make
/// both behaviours impossible to regress silently.
/// </summary>
public class LlmClientContractTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public string? LastBody { get; private set; }
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(_handler(request));
        }
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AiOptions Options(string provider = "ollama") => new(
        provider, "deepseek-v4-pro:cloud",
        AnthropicApiKey: provider == "anthropic" ? "k" : string.Empty,
        OpenAIApiKey: provider == "openai" ? "k" : string.Empty,
        OllamaBaseUrl: "https://ollama.com", OllamaApiKey: "k",
        MaxContextTokens: 4096, RequireHumanReview: true, LogPrompts: false);

    private static (LlmClient Client, StubHandler Handler) Build(string body, string provider = "ollama")
    {
        var handler = new StubHandler(_ => Json(body));
        return (new LlmClient(new HttpClient(handler), Options(provider)), handler);
    }

    private static LlmRequest Request(bool requireJson) =>
        new("ollama", "deepseek-v4-pro:cloud", "system", "user", 8000, RequireJson: requireJson);

    // ── Contract A: structured output is constrained ────────────────────────

    [Fact]
    public async Task Ollama_AsksForJson_WhenTheCallerWillParseIt()
    {
        var (client, handler) = Build("""{"message":{"content":"{\"ok\":true}"}}""");

        await client.CompleteAsync(Request(requireJson: true), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        sent.RootElement.GetProperty("format").GetString().Should().Be("json");
    }

    [Fact]
    public async Task Ollama_OmitsFormat_ForOrdinaryChat()
    {
        // Chat requests must serialise exactly as before this change.
        var (client, handler) = Build("""{"message":{"content":"hello"}}""");

        await client.CompleteAsync(Request(requireJson: false), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        sent.RootElement.TryGetProperty("format", out _).Should().BeFalse();
    }

    [Fact]
    public async Task OpenAi_UsesJsonObjectFormat_WhenTheCallerWillParseIt()
    {
        var (client, handler) = Build("""{"output_text":"{}"}""", "openai");

        await client.CompleteAsync(
            new LlmRequest("openai", "gpt-5", "system", "user", 2000, RequireJson: true), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        sent.RootElement.GetProperty("text").GetProperty("format").GetProperty("type")
            .GetString().Should().Be("json_object");
    }

    // ── Contract B: an empty completion is a failure, with a usable reason ──

    [Fact]
    public async Task Ollama_EmptyContent_IsAFailure_NotAnEmptySuccess()
    {
        var (client, _) = Build("""{"message":{"content":""},"done_reason":"stop"}""");

        var result = await client.CompleteAsync(Request(true), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("empty content");
    }

    [Fact]
    public async Task Ollama_BudgetExhaustedByReasoning_SaysSo()
    {
        // The live failure mode: the whole num_predict budget went on chain-of-thought and the
        // answer never started. The error has to name that, or the next person debugs the
        // network instead of the token budget.
        var (client, _) = Build("""
            {"message":{"content":"","thinking":"still working it out..."},
             "done_reason":"length","eval_count":8000}
            """);

        var result = await client.CompleteAsync(Request(true), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("8000-token budget");
        result.Error.Should().Contain("reasoning");
    }

    [Fact]
    public async Task Ollama_NeverReturnsChainOfThoughtAsTheAnswer()
    {
        var (client, _) = Build("""{"message":{"content":"","thinking":"secret reasoning"}}""");

        var result = await client.CompleteAsync(Request(true), CancellationToken.None);

        result.Text.Should().BeEmpty();
        result.Text.Should().NotContain("secret reasoning");
    }

    [Fact]
    public async Task Ollama_RealContent_StillSucceedsWithTokenCounts()
    {
        var (client, _) = Build("""
            {"message":{"content":"{\"departments\":[]}"},"prompt_eval_count":11,"eval_count":7}
            """);

        var result = await client.CompleteAsync(Request(true), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Text.Should().Contain("departments");
        result.InputTokens.Should().Be(11);
        result.OutputTokens.Should().Be(7);
    }

    [Fact]
    public async Task OpenAi_EmptyText_IsAFailure()
    {
        var (client, _) = Build("""{"output_text":"","id":"resp_1"}""", "openai");

        var result = await client.CompleteAsync(
            new LlmRequest("openai", "gpt-5", "s", "u", 2000), CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Anthropic_EmptyText_IsAFailure()
    {
        var (client, _) = Build("""{"content":[{"text":""}],"id":"msg_1"}""", "anthropic");

        var result = await client.CompleteAsync(
            new LlmRequest("anthropic", "claude", "s", "u", 2000), CancellationToken.None);

        result.Success.Should().BeFalse();
    }
}
