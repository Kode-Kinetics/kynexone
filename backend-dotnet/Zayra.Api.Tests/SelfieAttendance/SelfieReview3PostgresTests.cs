using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Selfie attendance v2, third review, on real PostgreSQL with truly parallel callers: one upload in flight per employee
/// (so parallel uploads cannot manufacture server failures), one waiver for at most one of several concurrent punches,
/// and the purge's fallback query that a late-locking month can no longer starve. Written only against types the
/// reviewed head (45ed1c9e) already had, so it compiles there and its tests fail there.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SelfieReview3PostgresTests
{
    private readonly PostgresFixture _fx;
    public SelfieReview3PostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Items1to3_ParallelUploadsWhileTheServerIsBusy_ManufactureOneWaiverAtMost()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync(required: true);
        // The server really is busy (someone else's selfie holds the only image slot).
        var imageGate = new SelfieImageGate(concurrency: 1);
        Assert.True(imageGate.TryEnter());

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await UploadAsync(storage, tenantId, employeeId, imageGate);
        })).ToList();
        start.SetResult();
        var answers = await Task.WhenAll(attempts);
        imageGate.Exit();

        // One attempt was in flight and was answered busy; the other four were refused while it was in flight.
        Assert.Equal(1, answers.Count(a => a.Status == 429));
        Assert.Equal(4, answers.Count(a => a.Status == 409 && a.Code == "selfie_upload_in_progress"));
        Assert.Equal(1, answers.Count(a => a.PunchWithoutSelfie));

        // So one punch goes through without the required selfie, and the next does not.
        var first = await PunchAsync(tenantId, employeeId, "In");
        var second = await PunchAsync(tenantId, employeeId, "Out");
        Assert.Equal(AttendanceVerificationMethods.None, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(first.Result).Value).VerificationMethod);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(second.Result));
    }

    [Fact]
    public async Task Item3_FourConcurrentPunches_UseOneServerFailureWaiverAtMost()
    {
        var storage = new MemoryDocumentStorage { FailPuts = true };
        var (tenantId, employeeId) = await SeedEmployeeAsync(required: true);
        var failed = await UploadAsync(storage, tenantId, employeeId, new SelfieImageGate());
        Assert.Equal(503, failed.Status);
        Assert.True(failed.PunchWithoutSelfie);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] directions = ["In", "Out", "BreakOut", "BreakIn"];
        var punches = directions.Select(d => Task.Run(async () =>
        {
            await start.Task;
            return await PunchAsync(tenantId, employeeId, d);
        })).ToList();
        start.SetResult();
        var outcomes = await Task.WhenAll(punches);

        Assert.Equal(1, outcomes.Count(o => o.Result is OkObjectResult));
        Assert.All(outcomes.Where(o => o.Result is not OkObjectResult), o => Assert.Equal("selfie_required", SelfieWorld.CodeOf(o.Result)));
        await using var verify = _fx.CreateDb();
        Assert.Equal(1, await verify.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId));
    }

    [Fact]
    public async Task Item5_1200RowsOfALateLockingMonth_DoNotDelayTheDueRowsOfACompanyThatNeverLocks()
    {
        var now = DateTime.UtcNow;
        Guid tenantId, dueId;
        int lateEmployee, neverEmployee;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var lateCompany = new Company { TenantId = tenantId, LegalNameEn = "Locks late", CountryCode = "SA" };
            var neverCompany = new Company { TenantId = tenantId, LegalNameEn = "Never locks", CountryCode = "SA" };
            db.Companies.AddRange(lateCompany, neverCompany);
            await db.SaveChangesAsync();
            lateEmployee = await AddEmployeeAsync(db, tenantId, lateCompany.Id);
            neverEmployee = await AddEmployeeAsync(db, tenantId, neverCompany.Id);

            // 1,200 selfies used ~130 days ago, in a month the late company locked only 20 days ago: not due until
            // lock + 90 days. They are older than the due row, so "oldest use first" put all of them in front of it.
            var lateUse = now.AddDays(-130);
            if (lateUse.AddMinutes(-30).Month != lateUse.Month) lateUse = lateUse.AddHours(1); // keep all 1,200 in one month
            db.PayrollRuns.Add(new PayrollRun
            {
                TenantId = tenantId, CompanyId = lateCompany.Id, Year = lateUse.Year, Month = lateUse.Month, Status = "Locked",
                RunType = PayrollRunTypes.Regular, LockedAtUtc = now.AddDays(-20),
            });
            for (var i = 0; i < 1200; i++)
            {
                var usedAt = lateUse.AddSeconds(-i);
                var lateRaw = new AttendanceRawEvent { TenantId = tenantId, EmployeeId = lateEmployee, PunchTimestampUtc = usedAt, PunchDirection = "In", VerificationMethod = "Selfie" };
                db.AttendanceRawEvents.Add(lateRaw);
                db.AttendanceEvidence.Add(new AttendanceEvidence
                {
                    TenantId = tenantId, EmployeeId = lateEmployee, StorageKey = $"storage/documents/{tenantId:N}/late{i}.jpg", Sha256 = new string('e', 64),
                    ByteSize = 3, CreatedAtUtc = usedAt.AddMinutes(-1), ExpiresAtUtc = usedAt.AddMinutes(9), UsedAtUtc = usedAt, UsedByRawEventId = lateRaw.Id,
                    PurgeState = AttendanceEvidencePurgeStates.Active,
                });
            }
            await db.SaveChangesAsync();

            // One selfie of the never-locking company, used 122 days ago: due at work date + 120 days.
            var dueUse = now.AddDays(-122);
            var raw = new AttendanceRawEvent { TenantId = tenantId, EmployeeId = neverEmployee, PunchTimestampUtc = dueUse, PunchDirection = "In", VerificationMethod = "Selfie" };
            db.AttendanceRawEvents.Add(raw);
            var due = new AttendanceEvidence
            {
                TenantId = tenantId, EmployeeId = neverEmployee, StorageKey = $"storage/documents/{tenantId:N}/due.jpg", Sha256 = new string('d', 64), ByteSize = 3,
                CreatedAtUtc = dueUse.AddMinutes(-1), ExpiresAtUtc = dueUse.AddMinutes(9), UsedAtUtc = dueUse, UsedByRawEventId = raw.Id,
                PurgeState = AttendanceEvidencePurgeStates.Active,
            };
            db.AttendanceEvidence.Add(due);
            await db.SaveChangesAsync();
            dueId = due.Id;
        }

        await using var scan = _fx.CreateDb();
        var found = await new SelfieEvidencePurger(scan, new MemoryDocumentStorage()).FindDueAsync(tenantId, now, 1000, default);

        Assert.Contains(dueId, found);
        Assert.Single(found); // and none of the late company's rows: they are not due yet
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid TenantId, int EmployeeId)> SeedEmployeeAsync(bool required)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employeeId = await AddEmployeeAsync(db, tenantId, null);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId = tenantId, FeatureKey = SelfieWorld.SelfieKey, IsEnabled = true,
            ConfigJson = SelfieWorld.SignedOffConfig(requireSelfieForConsented: required),
        });
        db.BiometricConsents.Add(new BiometricConsent { TenantId = tenantId, EmployeeId = employeeId, PolicyVersion = "1", Channel = BiometricConsentChannels.Mobile });
        await db.SaveChangesAsync();
        return (tenantId, employeeId);
    }

    private static async Task<int> AddEmployeeAsync(ZayraDbContext db, Guid tenantId, Guid? companyId)
    {
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"SELFIE3-{Guid.NewGuid():N}"[..20], FullName = "Selfie Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    private static ClaimsPrincipal Employee(Guid tenantId, int employeeId) => new(new ClaimsIdentity(
    [
        new Claim("tenant_id", tenantId.ToString()),
        new Claim("sub", Guid.NewGuid().ToString()),
        new Claim("employee_id", employeeId.ToString()),
        new Claim("access_mode", AccessModes.Mobile),
    ], "Test"));

    internal sealed record UploadAnswer(int? Status, string? Code, bool PunchWithoutSelfie);

    private async Task<UploadAnswer> UploadAsync(IDocumentStorage storage, Guid tenantId, int employeeId, SelfieImageGate gate)
    {
        await using var db = _fx.CreateDb();
        var controller = new AttendanceEvidenceController(db, storage, new AttendanceVerificationService(db, SelfieWorld.ResidentKsa), gate)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Employee(tenantId, employeeId) } },
        };
        var bytes = SelfieAttendanceTests.SelfieJpeg();
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        controller.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection { new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "s.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" } });
        var result = await controller.UploadSelfie();
        var body = result is ObjectResult { Value: { } v } ? JsonSerializer.SerializeToElement(v) : default;
        return new UploadAnswer((result as ObjectResult)?.StatusCode, SelfieWorld.CodeOf(result),
            body.ValueKind == JsonValueKind.Object && body.TryGetProperty("punchWithoutSelfie", out var p) && p.ValueKind == JsonValueKind.True);
    }

    private async Task<ActionResult<AttendanceRawEvent>> PunchAsync(Guid tenantId, int employeeId, string direction)
    {
        await using var db = _fx.CreateDb();
        var controller = new AttendanceController(new AttendanceService(db, new NullNotifications(), new NullHttpClients()),
            new DataScopeService(db), new HrmHierarchyService(db, new NullAudit()), db, new AttendanceVerificationService(db, SelfieWorld.ResidentKsa))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Employee(tenantId, employeeId) } },
        };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        return await controller.MobilePunch(new WebPunchRequest(0, direction, null, null, null), default);
    }
}

/// <summary>
/// Memory storage whose write blocks until <see cref="Release"/>, so an upload can be held in flight while other callers
/// run (the parallel in-flight tests).
/// </summary>
internal sealed class BlockingDocumentStorage : IDocumentStorage
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MemoryDocumentStorage Inner { get; } = new();
    public Task WriteStarted => _started.Task;
    public void Release() => _release.TrySetResult();

    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => Inner.SaveAsync(tenantId, file, cancellationToken);
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Inner.GetBytesAsync(tenantId, storageUrl, ct);
    public string ResolvePath(string storageUrl) => Inner.ResolvePath(storageUrl);
    public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Inner.TryDeleteAsync(tenantId, storageUrl, ct);
    public string TenantKey(Guid tenantId, string relativeName) => Inner.TenantKey(tenantId, relativeName);
    public Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Inner.DeleteStrictAsync(tenantId, storageUrl, ct);

    public async Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default)
    {
        _started.TrySetResult();
        await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        await Inner.PutAtAsync(tenantId, storageKey, content, contentType, ct);
    }
}
