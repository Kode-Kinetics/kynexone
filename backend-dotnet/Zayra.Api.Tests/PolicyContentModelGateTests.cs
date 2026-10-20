using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Setup;

namespace Zayra.Api.Tests;

/// <summary>
/// TEST-ONLY gate that lets policy content through, so the extraction and Q&amp;A pipelines behind the gate keep their
/// coverage for the day the owner reopens them. Production registers <see cref="PolicyContentNeverLeaves"/> and nothing
/// else (<see cref="PolicyContentModelGateTests.ProductionRegistersOnlyTheClosedGate"/>).
/// </summary>
internal sealed class OpenPolicyContentGateForTests : IPolicyContentModelGate
{
    public static readonly OpenPolicyContentGateForTests Instance = new();
    public bool MaySendPolicyContent(AiOptions options) => true;
}

/// <summary>
/// Owner decision (2026-10-20): company policy content never reaches the model gateway, for ANY provider — external
/// (Anthropic, OpenAI, Ollama Cloud) or apparently local (a loopback Ollama, which can still offload "-cloud" models).
/// Independent of the policy_intake flag: these call the services directly, with a model that counts its calls.
/// </summary>
public sealed class PolicyContentModelGateTests
{
    private sealed class CountingModel : ILlmClient
    {
        public int Calls { get; private set; }
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse(true, request.Provider, request.Model, "{\"proposals\":[]}", 10, 10));
        }
    }

    private sealed class CountingRecorder : IAiCallRecorder
    {
        public List<AiCallRecord> Records { get; } = [];
        public Task RecordAsync(AiCallRecord record, CancellationToken ct) { Records.Add(record); return Task.CompletedTask; }
    }

    public static TheoryData<string, AiOptions> Providers => new()
    {
        { "anthropic", new("anthropic", "claude-test", "sk-ant-test", "", "", "", 4096, true, false) },
        { "openai", new("openai", "gpt-test", "", "sk-test", "", "", 4096, true, false) },
        { "ollama cloud", new("ollama", "gpt-oss:120b", "", "", "https://ollama.com", "key", 4096, true, false) },
        { "ollama loopback, cloud-offloaded model", new("ollama", "gpt-oss:120b-cloud", "", "", "http://localhost:11434", "", 4096, true, false) },
        { "ollama loopback", new("ollama", "llama3.1", "", "", "http://127.0.0.1:11434", "", 4096, true, false) },
    };

    private const string Policy = "Annual leave\nEmployees receive thirty days of annual leave per year.\n";

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PolicyExtraction_NeverCallsTheGateway_ForAnyProvider(string name, AiOptions options)
    {
        var model = new CountingModel(); var recorder = new CountingRecorder();
        var service = new PolicyExtractionService(model, options, recorder, PolicyContentNeverLeaves.Instance);

        var result = await service.ExtractAsync(new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash",
            Policy, SetupConfigurationTests.Profile(), CancellationToken.None);

        model.Calls.Should().Be(0, $"{name}: policy text must not reach the model gateway");
        result.Provider.Should().Be("fallback");
        result.Proposals.Should().BeEmpty();
        result.Message.Should().Be(PolicyContentNeverLeaves.Reason);
        // Still observable: the refusal is an AI usage record with no request and the reason.
        var record = recorder.Records.Should().ContainSingle().Subject;
        record.Request.Should().BeNull();
        record.FailureReason.Should().Be(PolicyContentNeverLeaves.Reason);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PolicyQuestions_NeverCallTheGateway_ForAnyProvider_AndStillShowThePassages(string name, AiOptions options)
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var doc = new PolicyDocument { TenantId = tenant, FileName = "leave.txt", OriginalName = "leave.txt", MimeType = "text/plain", Status = "Ready", ChunkCount = 1 };
        db.PolicyDocuments.Add(doc);
        db.DocumentChunks.Add(new DocumentChunk { TenantId = tenant, DocumentId = doc.Id, Document = doc, ChunkIndex = 0, Content = Policy, TokenCount = 10 });
        await db.SaveChangesAsync();
        var model = new CountingModel(); var recorder = new CountingRecorder();
        var service = new PolicyDocumentService(db, model, options, recorder, NullLogger<PolicyDocumentService>.Instance, PolicyContentNeverLeaves.Instance);

        var answer = await service.AskAsync(tenant, Guid.NewGuid(), "HR Officer", "How many days of annual leave?", CancellationToken.None);

        model.Calls.Should().Be(0, $"{name}: policy excerpts must not reach the model gateway");
        answer.Provider.Should().Be("fallback");
        answer.Answer.Should().Contain("thirty days").And.Contain("not an AI-written answer");
        answer.DegradedReason.Should().Contain("never sent to an AI provider");
        recorder.Records.Should().ContainSingle().Which.Request.Should().BeNull();
    }

    [Fact]
    public void ProductionRegistersOnlyTheClosedGate()
    {
        // The only implementation shipped in the API assembly is the closed one...
        typeof(PolicyContentNeverLeaves).Assembly.GetTypes()
            .Where(t => typeof(IPolicyContentModelGate).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Should().Equal(typeof(PolicyContentNeverLeaves));
        PolicyContentNeverLeaves.Instance.MaySendPolicyContent(new("anthropic", "m", "k", "k", "http://localhost", "", 4096, true, false))
            .Should().BeFalse();
        // ...and the composed application resolves it.
        var connection = $"Data Source=file:policy-gate-{Guid.NewGuid():N}?mode=memory&cache=shared";
        using var anchor = new Microsoft.Data.Sqlite.SqliteConnection(connection);
        anchor.Open();
        using var host = new Security.AuthorizationPipelineHost(connection);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPolicyContentModelGate>().Should().BeSameAs(PolicyContentNeverLeaves.Instance);
    }
}
