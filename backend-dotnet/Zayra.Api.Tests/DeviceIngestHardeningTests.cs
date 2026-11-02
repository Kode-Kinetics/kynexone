using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F2 - the anonymous device webhook under retries, load and abuse. SQLite (a real unique-index engine, unlike the EF
/// in-memory provider) so the ON-CONFLICT-DO-NOTHING behaviour is exercised; the HTTP-hosted pieces (rate limiting, raw-body
/// signing over the wire, 413) are covered by DeviceIngestHardeningHostTests, which needs Postgres + the real pipeline.
/// </summary>
public sealed class DeviceIngestHardeningTests
{
    private const string Key = "knx_f2_device_key";
    private const string Master = "test-master-key-for-device-signing";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class NullHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private sealed class Rig : IAsyncDisposable
    {
        public SqliteConnection Conn = new("DataSource=:memory:");
        public Guid TenantId = Guid.NewGuid();
        public Guid DeviceId;
        public DeviceIngestOptions Options;
        public Rig(DeviceIngestOptions? options = null) { Options = options ?? new DeviceIngestOptions { SigningMasterKey = Master }; }
        public ZayraDbContext Db(params IInterceptor[] interceptors)
        {
            var b = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(Conn);
            if (interceptors.Length > 0) b.AddInterceptors(interceptors);
            return new ZayraDbContext(b.Options);
        }
        public AttendanceService Service(ZayraDbContext db) =>
            new(db, new NullNotifications(), new NullHttp(), new FixedClock(Now), Options);
        public async Task SeedAsync(Action<AttendanceDevice>? device = null, params string[] employeeCodes)
        {
            Conn.Open();
            await using var db = Db();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = TenantId, Name = "F2", Slug = $"f2-{TenantId:N}" });
            var d = new AttendanceDevice
            {
                TenantId = TenantId, DeviceName = "Gate", SerialNumber = $"SN-{Guid.NewGuid():N}", IsActive = true,
                ApiKeyReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))),
            };
            device?.Invoke(d);
            DeviceId = d.Id;
            db.AttendanceDevices.Add(d);
            foreach (var code in employeeCodes)
                db.Employees.Add(new Employee { TenantId = TenantId, EmployeeCode = code, FullName = code, EnglishName = code,
                    Status = EmployeeStatuses.Active, JoiningDate = Now.UtcDateTime.AddYears(-2), Salary = 9_000m });
            await db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync() => await Conn.DisposeAsync();
    }

    private static DeviceIngestPunch P(string code, DateTime at, string dir = "In") => new(code, at, dir, null, null, null, null, null, null);
    private static DeviceIngestRequest Req(params DeviceIngestPunch[] p) => new(p, AutoProcess: false);
    private static DateTime T(int minutesAgo) => Now.UtcDateTime.AddMinutes(-minutesAgo);

    // ── idempotency ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ResendingABatch_IsFullyDeduplicated_ForMatchedAndUnmatchedPunches()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(null, "E1");
        var batch = Req(P("E1", T(60)), P("E1", T(30), "Out"), P("GHOST", T(60)), P("GHOST", T(30), "Out"));

        await using (var db = rig.Db())
        {
            var first = await rig.Service(db).IngestByDeviceKeyAsync(Key, batch, "1.1.1.1", default);
            first!.Accepted.Should().Be(4); first.Duplicates.Should().Be(0); first.Unmatched.Should().Be(2);
        }
        await using (var db = rig.Db())
        {
            var second = await rig.Service(db).IngestByDeviceKeyAsync(Key, batch, "1.1.1.1", default);
            second!.Accepted.Should().Be(0);
            second.Duplicates.Should().Be(4, "an unmatched punch (employee id NULL) must de-duplicate too: NULLs are distinct to a plain unique index");
        }
        await using var check = rig.Db();
        (await check.AttendanceRawEvents.CountAsync()).Should().Be(4);
        check.AttendanceRawEvents.Where(e => e.EmployeeId == null).Should().HaveCount(2);
    }

    [Fact]
    public async Task TheSamePunchTwiceInOneRequest_IsStoredOnce()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(null, "E1");
        await using var db = rig.Db();
        var r = await rig.Service(db).IngestByDeviceKeyAsync(Key, Req(P("E1", T(10)), P("E1", T(10)), P("GHOST", T(10)), P("GHOST", T(10))), null, default);
        r!.Accepted.Should().Be(2); r.Duplicates.Should().Be(2);
    }

    /// <summary>The race: another request stores some of the same punches between this one's duplicate check and its insert.
    /// Previously the unique index failed the whole SaveChanges (HTTP 500, and the new punches in the batch were lost).</summary>
    private sealed class RacingWriter(Func<Task> inject) : SaveChangesInterceptor
    {
        private int _done;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AttendanceRawEvent>().Any(e => e.State == EntityState.Added)
                && Interlocked.Exchange(ref _done, 1) == 0)
                await inject();
            return result;
        }
    }

    [Fact]
    public async Task ConcurrentRetryOfTheSameBatch_NeverThrows_AndEveryNewPunchIsStoredExactlyOnce()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(null, "E1");
        var shared = new[] { P("E1", T(50)), P("GHOST", T(50)) };           // stored by the "other request"
        var fresh = new[] { P("E1", T(40)), P("GHOST", T(40)), P("E1", T(30), "Out") };
        var batch = Req(shared.Concat(fresh).ToArray());

        var racer = new RacingWriter(async () =>
        {
            await using var other = rig.Db();
            await rig.Service(other).IngestByDeviceKeyAsync(Key, Req(shared), null, default);
        });
        await using var db = rig.Db(racer);
        var result = await rig.Service(db).IngestByDeviceKeyAsync(Key, batch, null, default);

        result!.Accepted.Should().Be(3, "the three punches nobody else stored");
        result.Duplicates.Should().Be(2, "the two the racing request won");
        await using var check = rig.Db();
        (await check.AttendanceRawEvents.CountAsync()).Should().Be(5);
        (await check.AttendanceRawEvents.Select(e => e.DedupKey).Distinct().CountAsync()).Should().Be(5);
    }

    [Fact]
    public async Task TwoParallelRetriesOfOneBatch_BothSucceed_OneStoredCopy()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(null, "E1");
        var batch = Req(Enumerable.Range(1, 20).Select(i => P(i % 2 == 0 ? "E1" : "GHOST", T(100 - i))).ToArray());

        // Interleave on one connection: A is held at its insert while B runs the whole batch, then A proceeds into the conflict.
        var racer = new RacingWriter(async () =>
        {
            await using var other = rig.Db();
            (await rig.Service(other).IngestByDeviceKeyAsync(Key, batch, null, default))!.Accepted.Should().Be(20);
        });
        await using var db = rig.Db(racer);
        var a = await rig.Service(db).IngestByDeviceKeyAsync(Key, batch, null, default);

        a!.Accepted.Should().Be(0); a.Duplicates.Should().Be(20);
        await using var check = rig.Db();
        (await check.AttendanceRawEvents.CountAsync()).Should().Be(20);
    }

    // ── bounds ───────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ABatchAboveTheCap_IsRefusedWhole_AndNothingIsStored()
    {
        await using var rig = new Rig(new DeviceIngestOptions { MaxBatchSize = 5, SigningMasterKey = Master });
        await rig.SeedAsync(null, "E1");
        await using var db = rig.Db();
        var punches = Enumerable.Range(1, 6).Select(i => P("E1", T(i))).ToArray();
        var ex = await Assert.ThrowsAsync<AttendanceRefusalException>(() => rig.Service(db).IngestByDeviceKeyAsync(Key, Req(punches), null, default));
        ex.Refusal.Code.Should().Be("batch_too_large");
        (await rig.Db().AttendanceRawEvents.CountAsync()).Should().Be(0);
        // exactly at the cap is fine
        (await rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(punches.Take(5).ToArray()), null, default))!.Accepted.Should().Be(5);
    }

    [Fact]
    public async Task TimestampSanityWindow_RejectsPerRow_AndStoresTheRest()
    {
        await using var rig = new Rig(new DeviceIngestOptions { MaxFutureMinutes = 10, MaxPastDays = 30, SigningMasterKey = Master });
        await rig.SeedAsync(null, "E1");
        await using var db = rig.Db();
        var r = await rig.Service(db).IngestByDeviceKeyAsync(Key, Req(
            P("E1", T(60)),                          // 0 ok
            P("E1", Now.UtcDateTime.AddHours(2)),    // 1 future
            P("E1", Now.UtcDateTime.AddMinutes(9)),  // 2 inside the future tolerance
            P("E1", Now.UtcDateTime.AddDays(-31)),   // 3 too old
            P("E1", default),                        // 4 missing
            P(" ", T(5)),                            // 5 no code
            P("E1", Now.UtcDateTime.AddDays(-29))),  // 6 old but inside the window
            "1.1.1.1", default);

        r!.Received.Should().Be(7);
        r.Accepted.Should().Be(3);
        r.Rejected.Should().Be(4);
        r.Rejections!.Select(x => (x.Index, x.Reason)).Should().BeEquivalentTo(new[]
        {
            (1, "timestamp_in_future"), (3, "timestamp_too_old"), (4, "timestamp_missing"), (5, "employee_code_required"),
        });
    }

    // ── signing ──────────────────────────────────────────────────────────────────────────────────────
    private const string IngestPath = "/api/attendance/ingest";
    private static int _nonce;
    private static DeviceRequestEnvelope Signed(Guid deviceId, int version, string body, long ts, string? master = Master,
        string method = "POST", string path = IngestPath, string? nonce = null)
    {
        nonce ??= "n" + Interlocked.Increment(ref _nonce);
        return new(ts.ToString(), DeviceSigning.Sign(DeviceSigning.DeriveSecret(master!, deviceId, version), ts, nonce, method, path, body),
            body, null, method, path, nonce);
    }

    [Fact]
    public void Signature_MatchesTheBridgeVector()
    {
        // kynexbridge/test/bridge.test.js asserts the same vector; this pins the cross-language contract.
        DeviceSigning.Sign("s3cret", 1700000000, "nonce-1", "POST", IngestPath, "{\"a\":1}")
            .Should().Be("v2=19ddc14db30ec8263d6a0412e149558b8b196282deee0d5036a040c8fd4c0ebd");
    }

    [Fact]
    public async Task DeviceWithoutASecret_KeepsWorkingOnTheStaticKey_EvenIfASignatureIsSent()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(null, "E1");
        await using var db = rig.Db();
        var env = new DeviceRequestEnvelope(Now.ToUnixTimeSeconds().ToString(), "v2=garbage", "{}");
        (await rig.Service(db).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default, env))!.Accepted.Should().Be(1);
        (await rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(6))), null, default))!.Accepted.Should().Be(1);
    }

    [Fact]
    public async Task DeviceWithASecret_RefusesUnsigned_AcceptsAValidSignature_AndRefusesBadOnes()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(d => d.SigningKeyVersion = 1, "E1");
        var ts = Now.ToUnixTimeSeconds();
        var body = "{\"punches\":[]}";

        // H1: once a secret exists an unsigned request (a leaked static key) is refused, flag or no flag
        await Assert.ThrowsAsync<DeviceSignatureException>(() => rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default));
        (await rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(6))), null, default,
            Signed(rig.DeviceId, 1, body, ts)))!.Accepted.Should().Be(1);
        await Assert.ThrowsAsync<DeviceSignatureException>(() => rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(7))), null, default,
            Signed(rig.DeviceId, 1, body, ts) with { Signature = "v2=" + new string('0', 64) }));
        await Assert.ThrowsAsync<DeviceSignatureException>(() => rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(8))), null, default,
            Signed(rig.DeviceId, 2, body, ts)));   // an old/rotated secret
        await Assert.ThrowsAsync<DeviceSignatureException>(() => rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(9))), null, default,
            Signed(rig.DeviceId, 1, body, ts - 3600)));   // outside the window
        (await rig.Db().AttendanceRawEvents.CountAsync()).Should().Be(1, "a refused request stores nothing");
    }

    [Fact]
    public async Task ASignedRequest_CannotBeReplayed_NorMovedToAnotherEndpoint()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(d => d.SigningKeyVersion = 1, "E1");
        var ts = Now.ToUnixTimeSeconds();
        var env = Signed(rig.DeviceId, 1, "{}", ts, nonce: "fixed-nonce");
        (await rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default, env))!.Accepted.Should().Be(1);
        await Assert.ThrowsAsync<DeviceSignatureException>(() =>
            rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default, env));          // same nonce

        // a body signed for /ingest does not verify as a /heartbeat
        var hb = Signed(rig.DeviceId, 1, "{}", ts) with { Path = "/api/attendance/heartbeat" };
        await Assert.ThrowsAsync<DeviceSignatureException>(() =>
            rig.Service(rig.Db()).HeartbeatByDeviceKeyAsync(Key, new DeviceHeartbeatRequest(null, null, null, null, null, null), null, default, hb));
        // and a request with no nonce is refused outright
        await Assert.ThrowsAsync<DeviceSignatureException>(() =>
            rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default, Signed(rig.DeviceId, 1, "{}", ts) with { Nonce = null }));
    }

    [Fact]
    public void SigningMasterKey_MustBeLongEnough()
    {
        Assert.Throws<InvalidOperationException>(() => DeviceIngestOptions.ValidMasterKey("too-short"));
        DeviceIngestOptions.ValidMasterKey(null).Should().BeNull();
        DeviceIngestOptions.ValidMasterKey(new string('k', 32)).Should().HaveLength(32);
    }

    [Fact]
    public void FailedAuthThrottle_BlocksAnAddressPastItsBudget_ThenRecoversNextMinute()
    {
        var t = new DeviceAuthFailureThrottle();
        for (var i = 0; i < 3; i++) t.RecordFailure("9.9.9.9", Now);
        t.IsBlocked("9.9.9.9", Now, 3).Should().BeTrue();
        t.IsBlocked("8.8.8.8", Now, 3).Should().BeFalse();
        t.IsBlocked("9.9.9.9", Now.AddMinutes(2), 3).Should().BeFalse();
    }

    [Fact]
    public async Task ADeviceBoundToACompany_ResolvesOnlyThatCompanysEmployees()
    {
        var mine = Guid.NewGuid(); var other = Guid.NewGuid();
        await using var rig = new Rig();
        await rig.SeedAsync(d => d.CompanyId = mine);
        await using (var seed = rig.Db())
        {
            seed.Employees.Add(new Employee { TenantId = rig.TenantId, CompanyId = mine, EmployeeCode = "A1", FullName = "A", EnglishName = "A",
                Status = EmployeeStatuses.Active, JoiningDate = Now.UtcDateTime.AddYears(-2), Salary = 1m });
            seed.Employees.Add(new Employee { TenantId = rig.TenantId, CompanyId = other, EmployeeCode = "B1", FullName = "B", EnglishName = "B",
                Status = EmployeeStatuses.Active, JoiningDate = Now.UtcDateTime.AddYears(-2), Salary = 1m });
            await seed.SaveChangesAsync();
        }
        var r = await rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("A1", T(5)), P("B1", T(5))), null, default);
        r!.Accepted.Should().Be(2);
        r.Unmatched.Should().Be(1, "company A's door cannot punch company B's employee");
    }

    [Fact]
    public async Task RequireSignature_RefusesUnsignedRequests()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(d => { d.SigningKeyVersion = 1; d.RequireSignature = true; }, "E1");
        await Assert.ThrowsAsync<DeviceSignatureException>(() => rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default));
        (await rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default,
            Signed(rig.DeviceId, 1, "{}", Now.ToUnixTimeSeconds())))!.Accepted.Should().Be(1);
    }

    [Fact]
    public async Task RequireSignature_WithoutAServerMasterKey_FailsClosed()
    {
        await using var rig = new Rig(new DeviceIngestOptions());
        await rig.SeedAsync(d => { d.SigningKeyVersion = 1; d.RequireSignature = true; }, "E1");
        await Assert.ThrowsAsync<DeviceSignatureException>(() => rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default));
    }

    [Fact]
    public async Task UnknownOrInactiveKey_IsStillUnauthorized()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(d => d.IsActive = false, "E1");
        (await rig.Service(rig.Db()).IngestByDeviceKeyAsync(Key, Req(P("E1", T(5))), null, default)).Should().BeNull();
        (await rig.Service(rig.Db()).IngestByDeviceKeyAsync("nope", Req(P("E1", T(5))), null, default)).Should().BeNull();
    }

    [Fact]
    public async Task RotatingTheSecret_ReturnsItOnce_BumpsTheVersion_AndInvalidatesTheOldOne()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(null, "E1");
        var ctx = new RequestContext("1.1.1.1", "test", Guid.NewGuid(), rig.TenantId);
        var r1 = await rig.Service(rig.Db()).RotateDeviceSigningSecretAsync(rig.TenantId, rig.DeviceId, ctx, default);
        r1!.Version.Should().Be(1);
        r1.Secret.Should().Be(DeviceSigning.DeriveSecret(Master, rig.DeviceId, 1));
        var r2 = await rig.Service(rig.Db()).RotateDeviceSigningSecretAsync(rig.TenantId, rig.DeviceId, ctx, default);
        r2!.Secret.Should().NotBe(r1.Secret);

        // enforcing needs a secret; with one, it is allowed
        (await rig.Service(rig.Db()).SetDeviceSignatureRequiredAsync(rig.TenantId, rig.DeviceId, true, ctx, default)).Should().BeTrue();
    }

    [Fact]
    public async Task EnforcingSigning_WithoutASecret_IsRefused()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(null);
        var ctx = new RequestContext("1.1.1.1", "test", Guid.NewGuid(), rig.TenantId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service(rig.Db()).SetDeviceSignatureRequiredAsync(rig.TenantId, rig.DeviceId, true, ctx, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Rig(new DeviceIngestOptions()).Service(rig.Db()).RotateDeviceSigningSecretAsync(rig.TenantId, rig.DeviceId, ctx, default));
    }

    // ── heartbeat ────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Heartbeat_RecordsLivenessVersionSkewAndError()
    {
        await using var rig = new Rig();
        await rig.SeedAsync(null);
        var res = await rig.Service(rig.Db()).HeartbeatByDeviceKeyAsync(Key,
            new DeviceHeartbeatRequest("0.2.0", "Asia/Riyadh", Now.UtcDateTime.AddSeconds(42), 17, null, "ETIMEDOUT talking to device"), "1.1.1.1", default);
        res!.ClockSkewSeconds.Should().Be(42);
        await using var check = rig.Db();
        var d = await check.AttendanceDevices.SingleAsync();
        d.LastHeartbeatAtUtc.Should().Be(Now.UtcDateTime);
        d.BridgeVersion.Should().Be("0.2.0");
        d.LastClockSkewSeconds.Should().Be(42);
        d.ErrorLog.Should().Contain("ETIMEDOUT");
        (await rig.Service(rig.Db()).HeartbeatByDeviceKeyAsync("bad", new DeviceHeartbeatRequest(null, null, null, null, null, null), null, default)).Should().BeNull();
    }

    // ── key + options ────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void PunchKey_IsStable_IgnoresCaseKindAndSubMicrosecondNoise_AndSeparatesDirectionDeviceAndCode()
    {
        var dev = Guid.NewGuid();
        var t = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        var k = DevicePunchKey.Compute(dev, "e1", t, "In");
        DevicePunchKey.Compute(dev, " E1 ", DateTime.SpecifyKind(t, DateTimeKind.Unspecified), "In").Should().Be(k);
        DevicePunchKey.Compute(dev, "E1", t.AddTicks(5), "In").Should().Be(k, "Postgres keeps microseconds");
        DevicePunchKey.Compute(dev, "E1", t, "Out").Should().NotBe(k);
        DevicePunchKey.Compute(Guid.NewGuid(), "E1", t, "In").Should().NotBe(k);
        DevicePunchKey.Compute(dev, "E2", t, "In").Should().NotBe(k);
        DevicePunchKey.Compute(dev, "E1", t.AddMilliseconds(1), "In").Should().NotBe(k);
    }

    [Fact]
    public void Options_ReadFromConfiguration_WithBoundedDefaults()
    {
        var d = DeviceIngestOptions.From(new ConfigurationBuilder().Build());
        (d.MaxBatchSize, d.MaxFutureMinutes, d.MaxPastDays, d.SigningMasterKey).Should().Be((500, 10, 45, (string?)null));
        var c = DeviceIngestOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Attendance:DeviceIngest:MaxBatchSize"] = "100000", ["Attendance:DeviceIngest:MaxPastDays"] = "30",
            ["Attendance:DeviceIngest:SigningMasterKey"] = new string((char)107, 32),
        }).Build());
        c.MaxBatchSize.Should().Be(5000, "clamped");
        c.MaxPastDays.Should().Be(30);
        c.SigningMasterKey.Should().Be(new string((char)107, 32));
    }
}
