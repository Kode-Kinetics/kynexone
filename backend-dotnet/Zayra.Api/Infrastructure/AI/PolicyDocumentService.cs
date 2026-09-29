using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using Zayra.Api.Application.AI;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Infrastructure.AI;

public class PolicyDocumentService : IPolicyDocumentService
{
    private readonly ZayraDbContext _db;
    private readonly ILlmClient _llm;
    private readonly AiOptions _aiOptions;
    private readonly IAiCallRecorder _recorder;
    private readonly ILogger<PolicyDocumentService> _logger;

    public PolicyDocumentService(
        ZayraDbContext db,
        ILlmClient llm,
        AiOptions aiOptions,
        IAiCallRecorder recorder,
        ILogger<PolicyDocumentService> logger)
    {
        _db = db;
        _llm = llm;
        _aiOptions = aiOptions;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<PolicyDocumentDto> UploadAsync(Guid tenantId, Guid? userId, Stream content, string fileName, string mimeType, CancellationToken ct)
    {
        var doc = new PolicyDocument
        {
            TenantId = tenantId,
            FileName = fileName,
            OriginalName = fileName,
            MimeType = mimeType,
            FileSizeBytes = content.Length,
            UploadedByUserId = userId,
            Status = "Processing"
        };
        _db.PolicyDocuments.Add(doc);
        await _db.SaveChangesAsync(ct);

        try
        {
            var text = ExtractText(content, mimeType, fileName);
            var chunks = ChunkText(text, 800);
            foreach (var (chunk, i) in chunks.Select((c, i) => (c, i)))
            {
                _db.DocumentChunks.Add(new DocumentChunk
                {
                    TenantId = tenantId,
                    DocumentId = doc.Id,
                    ChunkIndex = i,
                    Content = chunk,
                    TokenCount = chunk.Length / 4
                });
            }
            doc.ChunkCount = chunks.Count;
            doc.Status = "Ready";
        }
        catch (Exception ex)
        {
            doc.Status = "Failed";
            doc.ErrorMessage = ex.Message;
        }

        doc.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(doc);
    }

    public async Task<IReadOnlyList<PolicyDocumentDto>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        var docs = await _db.PolicyDocuments
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return docs.Select(ToDto).ToList();
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid documentId, CancellationToken ct)
    {
        var doc = await _db.PolicyDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId && !x.IsDeleted, ct);
        if (doc is null) return false;
        doc.IsDeleted = true;
        doc.UpdatedAtUtc = DateTime.UtcNow;
        await _db.DocumentChunks.Where(x => x.DocumentId == documentId).ExecuteDeleteAsync(ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Policy Q&A ──────────────────────────────────────────────────────────

    /// <summary>Stable module/intent keys for the AI usage record. Match AiCallRecord's vocabulary.</summary>
    private const string ModuleKey = "policy";
    private const string IntentKey = "policy_question";

    /// <summary>
    /// How long a user will wait for a model before we hand them the policy text instead.
    /// Deliberately below the HttpClient ceiling so the degrade is ours and explicable, rather
    /// than a socket timeout surfacing as a generic error.
    /// </summary>
    private static readonly TimeSpan LlmBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Output budget for one grounded policy answer.
    ///
    /// <para>1024 (the previous value) is not a safe budget for a REASONING model. deepseek-v4-pro
    /// and friends spend hundreds to a few thousand tokens in <c>thinking</c> before the first
    /// token of <c>content</c>; when <c>num_predict</c> runs out mid-reasoning Ollama returns an
    /// empty completion with <c>done_reason=length</c> — exactly the failure that made the setup
    /// assistant look permanently broken. A policy answer is prose and may quote several clauses,
    /// so this leaves room for the reasoning pass plus roughly 1,500 tokens of answer.</para>
    /// </summary>
    private const int AnswerOutputTokens = 6000;

    /// <summary>
    /// Back-compat entry point. Produces an UNATTRIBUTED usage record (no user id, role "System").
    /// Callers that know who is asking should use the overload below so the audit row names them.
    /// </summary>
    public Task<PolicyAskResponse> AskAsync(Guid tenantId, string question, CancellationToken ct)
        => AskAsync(tenantId, null, string.Empty, question, ct);

    public async Task<PolicyAskResponse> AskAsync(Guid tenantId, Guid? userId, string userRole, string question, CancellationToken ct)
    {
        // Simple keyword retrieval — get chunks containing words from the question
        var words = question.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3).Distinct().Take(8).ToList();

        List<DocumentChunk> relevantChunks;
        if (words.Count == 0)
        {
            relevantChunks = new List<DocumentChunk>();
        }
        else
        {
            // Load all ready chunks for tenant, then filter in memory (no full-text index)
            var allChunks = await _db.DocumentChunks
                .AsNoTracking()
                .Include(x => x.Document)
                .Where(x => x.TenantId == tenantId && !x.Document.IsDeleted && x.Document.Status == "Ready")
                .ToListAsync(ct);

            relevantChunks = allChunks
                .Where(c => words.Any(w => c.Content.ToLowerInvariant().Contains(w)))
                .OrderByDescending(c => words.Count(w => c.Content.ToLowerInvariant().Contains(w)))
                .Take(5)
                .ToList();
        }

        var sources = relevantChunks.Select(c => c.Document.OriginalName).Distinct().ToArray();

        // NON-PII by construction: counts only. The question can name an employee or a medical
        // reason and the excerpts are policy text; neither may reach a log row (Contract C).
        var promptSummary = $"policy question ({question.Trim().Length} chars) · " +
                            $"{relevantChunks.Count} excerpt(s) from {sources.Length} document(s)";

        if (relevantChunks.Count == 0)
        {
            // No model call is attempted here, but the ask still happened and AI still did not
            // answer it. Recording it is what stops a tenant with zero readable documents from
            // looking, on every dashboard, exactly like a tenant whose AI works.
            await RecordAsync(tenantId, userId, userRole, promptSummary, null, null, 0,
                "no policy excerpt matched the question; no model call attempted", ct);

            return new PolicyAskResponse(
                "I couldn't find relevant information in the uploaded policy documents. Please ensure the relevant document has been uploaded and try rephrasing your question.",
                Array.Empty<string>(),
                false)
            {
                Provider = FallbackProvider,
                DegradedReason = "No uploaded policy document matched this question, so no answer was generated."
            };
        }

        var context = string.Join("\n\n---\n\n", relevantChunks.Select((c, i) =>
            $"[Source: {c.Document.OriginalName}, Chunk {c.ChunkIndex + 1}]\n{c.Content}"));

        var systemPrompt = "You are a helpful HR policy assistant for KynexOne. Answer questions using ONLY the provided policy document excerpts. If the answer is not found in the excerpts, say so clearly. Always cite which document your answer comes from. Label your response as advisory — it does not constitute legal advice.";

        var userPrompt = $"""
            Policy document excerpts:
            {context}

            Question: {question}

            Answer:
            """;

        // Who can actually answer — NOT simply the configured provider. AI_PROVIDER=ollama with no
        // base URL is "configured" and answers nothing; reporting that call as "ollama" is the same
        // class of lie as the setup banner that said "unavailable" about a provider that was up.
        var provider = ResolveProvider();
        if (provider == FallbackProvider)
        {
            await RecordAsync(tenantId, userId, userRole, promptSummary, null, null, 0,
                "no AI provider configured; no model call attempted", ct);
            return Deterministic(relevantChunks, sources,
                "No AI assistant is configured on this environment, so no written answer was produced.");
        }

        var request = new LlmRequest(
            Provider: provider,
            Model: ResolveModel(provider),
            SystemPrompt: systemPrompt,
            UserPrompt: userPrompt,
            MaxOutputTokens: AnswerOutputTokens,
            // Contract A does NOT apply here: this answer is prose read by a human and is never
            // parsed. Forcing JSON would make the model wrap the answer in an object that the UI
            // would then render verbatim. Leave it false, deliberately.
            RequireJson: false);

        LlmResponse? response = null;
        string? recordedFailure = null;   // technical, goes to the audit row
        string? userFailure = null;       // plain language, goes to the user

        var timer = Stopwatch.StartNew();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(LlmBudget);
            response = await _llm.CompleteAsync(request, budget.Token);

            if (!response.Success)
            {
                // Contract B: Success==false now covers an empty completion, and Error says whether
                // the token budget was eaten by reasoning. That detail belongs in the audit row.
                recordedFailure = $"provider '{response.Provider}' returned no usable answer: {Truncate(response.Error)}";
                userFailure = "The AI assistant could not produce an answer for this question.";
            }
            else if (string.IsNullOrWhiteSpace(response.Text))
            {
                // Defensive: a client that reports Success with blank text must not become an
                // empty answer bubble. Distinct reason so it is not confused with the case above.
                recordedFailure = $"provider '{response.Provider}' reported success but returned no text";
                userFailure = "The AI assistant replied without any usable text.";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            recordedFailure = $"provider '{provider}' did not respond within {LlmBudget.TotalSeconds:0}s";
            userFailure = $"The AI assistant did not respond within {LlmBudget.TotalSeconds:0} seconds.";
        }
        catch (Exception ex)
        {
            recordedFailure = $"provider '{provider}' could not be reached: {Truncate(ex.Message)}";
            userFailure = "The AI assistant could not be reached.";
        }
        timer.Stop();

        await RecordAsync(tenantId, userId, userRole, promptSummary, request, response,
            (int)timer.ElapsedMilliseconds, recordedFailure, ct);

        if (userFailure is null)
        {
            // Provider and Model come from the RESPONSE, so they name whoever actually answered.
            return new PolicyAskResponse(response!.Text, sources, true)
            {
                Provider = response.Provider,
                Model = response.Model
            };
        }

        // The raw provider error is deliberately NOT shown: any user holding ai.query can reach
        // this endpoint, and provider internals are not their problem. It is in the audit row and
        // the log, which is where two days of debugging should have been able to find it.
        _logger.LogWarning("Policy ask degraded to document excerpts ({Provider}/{Model}): {Reason}",
            provider, request.Model, recordedFailure);
        return Deterministic(relevantChunks, sources, userFailure);
    }

    private const string FallbackProvider = "fallback";

    /// <summary>
    /// The deterministic path: no model wrote this, so the user gets the matching policy text
    /// verbatim plus the reason the assistant did not summarise it. Still grounded — it IS the
    /// document — but <see cref="PolicyAskResponse.Provider"/> says "fallback", never the
    /// configured provider.
    /// </summary>
    private static PolicyAskResponse Deterministic(List<DocumentChunk> chunks, string[] sources, string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine(reason);
        sb.AppendLine();
        sb.AppendLine("These are the passages from your policy documents that match your question — please read them directly:");
        foreach (var chunk in chunks)
        {
            sb.AppendLine();
            sb.AppendLine($"— {chunk.Document.OriginalName} (section {chunk.ChunkIndex + 1}):");
            sb.AppendLine(chunk.Content);
        }
        sb.AppendLine();
        sb.AppendLine("This is document text, not an AI-written answer. Verify anything important with HR.");

        return new PolicyAskResponse(sb.ToString().TrimEnd(), sources, true)
        {
            Provider = FallbackProvider,
            DegradedReason = reason
        };
    }

    /// <summary>Recording is bookkeeping: it must never fail the answer the user is waiting for.</summary>
    private async Task RecordAsync(
        Guid tenantId, Guid? userId, string userRole, string promptSummary,
        LlmRequest? request, LlmResponse? response, int elapsedMs, string? failureReason, CancellationToken ct)
    {
        try
        {
            await _recorder.RecordAsync(
                new AiCallRecord(tenantId, userId, userRole, ModuleKey, IntentKey, promptSummary,
                    request, response, elapsedMs, failureReason),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the policy AI call for tenant {TenantId}.", tenantId);
        }
    }

    /// <summary>The provider that can actually answer, or "fallback". Mirrors SetupAssistantService.</summary>
    private string ResolveProvider()
    {
        var configured = _aiOptions.EffectiveProvider;
        if (configured == "anthropic" && !string.IsNullOrWhiteSpace(_aiOptions.AnthropicApiKey)) return "anthropic";
        if (configured == "openai" && !string.IsNullOrWhiteSpace(_aiOptions.OpenAIApiKey)) return "openai";
        if (configured == "ollama" && !string.IsNullOrWhiteSpace(_aiOptions.OllamaBaseUrl)) return "ollama";
        return FallbackProvider;
    }

    private string ResolveModel(string provider)
    {
        if (!string.IsNullOrWhiteSpace(_aiOptions.Model)) return _aiOptions.Model;
        return provider switch
        {
            "anthropic" => "claude-sonnet-4-20250514",
            "openai" => "gpt-5",
            "ollama" => "llama3.1",
            _ => string.Empty
        };
    }

    private static string Truncate(string? value, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(value)) return "no detail reported";
        var flat = Regex.Replace(value.Trim(), @"\s+", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string ExtractText(Stream stream, string mimeType, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".pdf" || mimeType == "application/pdf")
        {
            using var pdf = PdfDocument.Open(stream);
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages())
                sb.AppendLine(page.Text);
            return sb.ToString();
        }
        if (ext is ".docx" or ".doc" || mimeType.Contains("wordprocessingml") || mimeType.Contains("msword"))
        {
            using var doc = WordprocessingDocument.Open(stream, false);
            var sb = new StringBuilder();
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body != null)
                foreach (var text in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>())
                    sb.AppendLine(text.Text);
            return sb.ToString();
        }
        // Plain text fallback
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static List<string> ChunkText(string text, int maxChunkSize)
    {
        var chunks = new List<string>();
        var sentences = text.Split(new[] { ". ", ".\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (current.Length + sentence.Length > maxChunkSize && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            current.Append(sentence).Append(". ");
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        return chunks.Count > 0 ? chunks : new List<string> { text.Trim() };
    }

    private static PolicyDocumentDto ToDto(PolicyDocument d) =>
        new(d.Id, d.OriginalName, d.MimeType, d.FileSizeBytes, d.Status, d.ChunkCount, d.ErrorMessage, d.CreatedAtUtc);
}
