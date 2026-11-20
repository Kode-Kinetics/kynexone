using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// A4: the attempt ledger. One row per attempt, idempotent on (tenant, body, operation, key), audited through Stage in the same
/// save, bodies never stored (hash and masked preview only), and only a live adapter on the api channel may record a state that
/// says the body has the exchange.
/// </summary>
public sealed class GovernmentExchangeRecorderTests
{
    /// <summary>A recorder writing in its own contexts, and a separate context to read what it wrote, over one in-memory database.</summary>
    internal static (GovernmentExchangeRecorder Recorder, ZayraDbContext Db) Create()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return (new GovernmentExchangeRecorder(() => new ZayraDbContext(options)), new ZayraDbContext(options));
    }
    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    private sealed class LiveDouble : IGovernmentAdapter
    {
        public string Body => GovernmentBodies.BankAnb;
        public string AdapterName => "live-double";
        public bool IsLiveIntegration => true;
    }

    // Synthetic IBAN and ID, the shapes the scrubber must mask.
    private const string SyntheticIban = "SA0380000000608010167519";
    private const string SyntheticId = "1234567890";

    private static GovernmentExchangeAttempt Attempt(Guid tenant, string key = "batch-1", string state = GovernmentExchangeStates.SubmitIntended,
        IGovernmentAdapter? adapter = null, string channel = GovernmentExchangeChannels.Api, byte[]? body = null, string? preview = null) =>
        new(tenant, tenant, GovernmentBodies.BankAnb, "payroll.submit", channel, key, state, Ctx(tenant), adapter,
            Environment: "sandbox", SubjectType: "PaymentBatch", SubjectId: TestBatch.Id($"B-{key}"), RequestBody: body,
            ArtifactSha256: new string('a', 64), PreviewText: preview, AccountFingerprint: "acct-fp-synthetic", BatchReference: $"B-{key}");

    [Fact]
    public async Task Begin_WritesOneRow_WithHashesAndAMaskedPreview_NeverTheBody_AndStagesItsAudit()
    {
        var (recorder, db) = Create();
        await using var _db = db;
        var tenant = Guid.NewGuid();
        var body = System.Text.Encoding.UTF8.GetBytes($"name,iban,id\nمحمد عبدالله الشمري,{SyntheticIban},{SyntheticId}");

        var result = await recorder.BeginAsync(Attempt(tenant, body: body, preview: $"pay {SyntheticIban} to {SyntheticId}"), default);

        result.Created.Should().BeTrue();
        var row = await db.GovernmentExchanges.SingleAsync();
        row.RequestSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        row.ArtifactSha256.Should().Be(new string('a', 64));
        row.MaskedPreview.Should().NotContain(SyntheticIban).And.NotContain(SyntheticId);
        row.IsLive.Should().BeFalse("no adapter declared itself live");
        row.State.Should().Be(GovernmentExchangeStates.SubmitIntended);
        var audit = await db.AuditLogs.SingleAsync();
        audit.Action.Should().Be("government.exchange.recorded");
        audit.EntityId.Should().Be(row.Id.ToString());
        audit.Metadata.Should().NotContain(SyntheticIban);
    }

    [Fact]
    public async Task TheSameIdempotencyKey_ReturnsTheFirstRow_AndWritesNothing()
    {
        var (recorder, db) = Create();
        await using var _db = db;
        var tenant = Guid.NewGuid();

        var first = await recorder.BeginAsync(Attempt(tenant), default);
        var second = await recorder.BeginAsync(Attempt(tenant), default);

        second.Created.Should().BeFalse();
        second.Exchange.Id.Should().Be(first.Exchange.Id);
        (await db.GovernmentExchanges.CountAsync()).Should().Be(1);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
        // The key is per tenant: another tenant's identical key is its own attempt.
        (await recorder.BeginAsync(Attempt(Guid.NewGuid()), default)).Created.Should().BeTrue();
    }

    [Theory]
    [InlineData(GovernmentExchangeStates.Accepted)]
    [InlineData(GovernmentExchangeStates.Validated)]
    [InlineData(GovernmentExchangeStates.InProgress)]
    [InlineData(GovernmentExchangeStates.Completed)]
    [InlineData(GovernmentExchangeStates.Initiated)]
    [InlineData(GovernmentExchangeStates.Rejected)]
    public async Task AStateThatClaimsTheBodyHasIt_IsRefused_WithoutALiveAdapter(string state)
    {
        var (recorder, db) = Create();
        await using var _db = db;
        var tenant = Guid.NewGuid();

        // Not live: the simulator, a manual record, no adapter at all.
        var sandbox = new SandboxQiwaApiAdapter(Microsoft.Extensions.Logging.Abstractions.NullLogger<SandboxQiwaApiAdapter>.Instance);
        foreach (var (adapter, channel) in new (IGovernmentAdapter?, string)[]
                 { (null, GovernmentExchangeChannels.Api), (sandbox, GovernmentExchangeChannels.Api), (new LiveDouble(), GovernmentExchangeChannels.Manual) })
        {
            var act = () => recorder.BeginAsync(Attempt(tenant, key: Guid.NewGuid().ToString(), state: state, adapter: adapter, channel: channel), default);
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith(GovernmentExchangeRecorder.LiveStateRefusedCode);
        }
        (await db.GovernmentExchanges.CountAsync()).Should().Be(0);

        // An attempt that started non-live cannot be promoted to a live state later either.
        var started = await recorder.BeginAsync(Attempt(tenant, key: "k-promote", adapter: sandbox), default);
        var promote = () => recorder.UpdateAsync(tenant, started.Exchange.Id, new GovernmentExchangeOutcome(state, Ctx(tenant)), default);
        await promote.Should().ThrowAsync<InvalidOperationException>();

        // A live adapter on the api channel may, along the transition table (the bank accepts first).
        var live = await recorder.BeginAsync(Attempt(tenant, key: "k-live", adapter: new LiveDouble()), default);
        live.Exchange.IsLive.Should().BeTrue();
        await recorder.UpdateAsync(tenant, live.Exchange.Id, new GovernmentExchangeOutcome(GovernmentExchangeStates.Accepted, Ctx(tenant), HttpStatus: 202,
            ResponseBody: "{\"id\":\"B-1\"}"u8.ToArray(), ExternalReference: "B-1"), default);
        (await recorder.UpdateAsync(tenant, live.Exchange.Id, new GovernmentExchangeOutcome(state, Ctx(tenant), IsPoll: true), default)).State.Should().Be(state);
    }

    [Fact]
    public async Task Update_RecordsTheOutcome_AndStagesAnotherAuditRow_AndAnotherTenantCannotTouchIt()
    {
        var (recorder, db) = Create();
        await using var _db = db;
        var tenant = Guid.NewGuid();
        var started = await recorder.BeginAsync(Attempt(tenant), default);

        var updated = await recorder.UpdateAsync(tenant, started.Exchange.Id, new GovernmentExchangeOutcome(GovernmentExchangeStates.Unknown, Ctx(tenant),
            ErrorCode: "timeout_after_send", PreviewText: $"no answer for {SyntheticIban}", Completed: false), default);

        updated.State.Should().Be(GovernmentExchangeStates.Unknown);
        updated.ErrorCode.Should().Be("timeout_after_send");
        updated.MaskedPreview.Should().NotContain(SyntheticIban);
        (await db.AuditLogs.CountAsync()).Should().Be(2);
        var other = () => recorder.UpdateAsync(Guid.NewGuid(), started.Exchange.Id, new GovernmentExchangeOutcome(GovernmentExchangeStates.Failed, Ctx(Guid.NewGuid())), default);
        await other.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public void TheScrubber_KeepsStatusKeys_MasksText_AndWithholdsTheRest_AndQiwaStillDelegates()
    {
        var raw = $"{{\"status\":\"ok\",\"message\":\"account {SyntheticIban}\",\"id_number\":\"{SyntheticId}\",\"name\":\"x\"}}";

        var scrubbed = GovernmentLogScrubber.ScrubJson(raw, GovernmentLogScrubber.CommonKeptKeys)!;

        scrubbed.Should().Contain("\"status\":\"ok\"").And.NotContain(SyntheticIban).And.NotContain(SyntheticId).And.Contain("withheld_fields");
        QiwaResponseScrubber.Scrub(raw).Should().NotContain(SyntheticIban).And.NotContain(SyntheticId);
        GovernmentLogScrubber.ScrubJson("not json", GovernmentLogScrubber.CommonKeptKeys).Should().Contain("withheld");
        GovernmentLogScrubber.Preview(new string('x', 1000))!.Length.Should().Be(GovernmentLogScrubber.DefaultPreviewLength);

        // A token response, or a body echoing the request: the credential and the payload never survive the scrub.
        var tokenResponse = "{\"access_token\":\"synthetic-access-token-0001\",\"refresh_token\":\"synthetic-refresh-0001\",\"client_secret\":\"synthetic-secret-0001\",\"token_type\":\"bearer\",\"status\":\"ok\",\"body\":\"name,iban\\nمحمد," + SyntheticIban + "\"}";
        foreach (var scrubbed2 in new[] { GovernmentLogScrubber.ScrubJson(tokenResponse, GovernmentLogScrubber.CommonKeptKeys)!, QiwaResponseScrubber.Scrub(tokenResponse)! })
            scrubbed2.Should().NotContainAny("synthetic-access-token", "synthetic-refresh", "synthetic-secret", SyntheticIban, "محمد");
    }

    [Fact]
    public async Task TheLedgerRow_NeverHoldsTheRequestOrResponseBody()
    {
        var (recorder, db) = Create();
        await using var _db = db;
        var tenant = Guid.NewGuid();
        const string name = "عبدالرحمن بن سليمان آل الشيخ العتيبي"; // 35+ characters, the real size of an Arabic name column
        var request = System.Text.Encoding.UTF8.GetBytes($"Authorization: Bearer synthetic-token-0001\n{name},{SyntheticIban},{SyntheticId},98765.43");

        var started = await recorder.BeginAsync(Attempt(tenant, body: request), default);
        await recorder.UpdateAsync(tenant, started.Exchange.Id, new GovernmentExchangeOutcome(GovernmentExchangeStates.Failed, Ctx(tenant),
            HttpStatus: 400, ResponseBody: request, ErrorCode: "rejected"), default);

        var row = await db.GovernmentExchanges.AsNoTracking().SingleAsync();
        var stored = System.Text.Json.JsonSerializer.Serialize(row) + string.Join("|", await db.AuditLogs.Select(a => a.Metadata).ToListAsync());
        stored.Should().NotContainAny("synthetic-token", name, SyntheticIban, SyntheticId, "98765.43");
        row.RequestSha256.Should().HaveLength(64);
        row.ResponseSha256.Should().HaveLength(64);
    }

    // ── E1: a payroll submission is keyed on its PAYMENT BATCH, so it must name one ───────────────────────────────────────

    private static GovernmentExchangeAttempt PaymentBatchSubmission(Guid tenant, string key, string? subjectType = "PaymentBatch", string? subjectId = null,
        string operation = "payroll.submit", string state = GovernmentExchangeStates.SubmitIntended) =>
        Attempt(tenant, key, state, new LiveDouble()) with
        {
            Operation = operation, SubjectType = subjectType, SubjectId = subjectId ?? Guid.NewGuid().ToString(),
        };

    [Theory]
    [InlineData("Employee", "a-guid")]
    [InlineData(null, "a-guid")]
    [InlineData("PaymentBatch", "B-001")]
    [InlineData("PaymentBatch", "42")]
    public async Task E1_ASubmission_ThatDoesNotNameAPaymentBatchById_IsRefused(string? subjectType, string subjectId)
    {
        var (recorder, db) = Create();
        await using var _db = db;
        var attempt = PaymentBatchSubmission(Guid.NewGuid(), "k", subjectType, subjectId == "a-guid" ? Guid.NewGuid().ToString() : subjectId);

        var begin = () => recorder.BeginAsync(attempt, default);

        await begin.Should().ThrowAsync<ArgumentException>("the batch lock is the payment batch's id; without it two submissions of one batch could both send");
        (await db.GovernmentExchanges.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task E1_TheSubjectId_IsStoredInOneCanonicalForm_SoACasingChangeCannotDodgeTheLock()
    {
        var (recorder, db) = Create();
        await using var _db = db;
        var tenant = Guid.NewGuid();
        var batch = Guid.NewGuid();
        await recorder.BeginAsync(PaymentBatchSubmission(tenant, "lower", subjectId: batch.ToString()), default);

        var upper = () => recorder.BeginAsync(PaymentBatchSubmission(tenant, "upper", subjectId: batch.ToString("B").ToUpperInvariant()), default);

        (await upper.Should().ThrowAsync<GovernmentExchangeConflictException>()).Which.Code.Should().Be(GovernmentExchangeRecorder.BatchHeldCode);
        (await db.GovernmentExchanges.SingleAsync()).SubjectId.Should().Be(batch.ToString("D"));
    }

    [Fact]
    public async Task E1_ASubmissionIntent_UnderAnotherOperationName_IsRefused()
    {
        var (recorder, db) = Create();
        await using var _db = db;

        var begin = () => recorder.BeginAsync(PaymentBatchSubmission(Guid.NewGuid(), "k", operation: "payroll.submit.v2"), default);

        await begin.Should().ThrowAsync<ArgumentException>("an intent outside the submission operation would hold no batch");
    }

    [Fact]
    public async Task E1_APayrollSubmission_CannotStartAsAPlainAttempt_WhichWouldEarnAReceiptOutsideTheLock()
    {
        var (recorder, db) = Create();
        await using var _db = db;

        var begin = () => recorder.BeginAsync(PaymentBatchSubmission(Guid.NewGuid(), "k", state: GovernmentExchangeStates.Attempted), default);

        await begin.Should().ThrowAsync<ArgumentException>();
        (await db.GovernmentExchanges.CountAsync()).Should().Be(0);
    }
}
