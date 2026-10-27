using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Application.Esign;
using Zayra.Api.Data;
using Zayra.Api.Data.Esign;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Esign;

/// <summary>A step-up stand-in: the token IS its own jti, so a test decides exactly which proof each request carries.</summary>
public sealed class FakeStepUp : IStepUpService
{
    public Task<StepUpResult> VerifyAsync(Guid userId, string purpose, StepUpCredential credential, CancellationToken ct) => throw new NotSupportedException();
    public bool IsFresh(string? token, ClaimsPrincipal principal, string purpose, TimeSpan max) => Guid.TryParseExact(token, "N", out _);
    public Task<StepUpResult> VerifyDeviceAsync(Guid employeeUserId, Guid witnessUserId, string bind, StepUpCredential credential, CancellationToken ct) => throw new NotSupportedException();
    public bool IsFresh(string? token, Guid userId, Guid tenantId, string purpose, TimeSpan max, string? bind = null, Guid? witness = null) => Guid.TryParseExact(token, "N", out _);
    public string? ReadJti(string? token, Guid userId, Guid tenantId, string purpose, string? bind = null, Guid? witness = null) => Guid.TryParseExact(token, "N", out _) ? token : null;
}

/// <summary>P4 on real PostgreSQL: the append-only trigger and grants, the chain under concurrency, the evidence constraints.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EsignPostgresTests(PostgresFixture fx)
{
    private const string AppRole = "kx_p4_app";

    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Installs the trigger for one test and removes it after, so the shared database stays as other tests expect it.</summary>
    private async Task<IAsyncDisposable> WithTriggerAsync()
    {
        await ExecAsync(AckAppendOnlySql.CreateFunction);
        await ExecAsync(AckAppendOnlySql.CreateTrigger);
        return new Cleanup(() => ExecAsync(AckAppendOnlySql.Drop));
    }

    private sealed class Cleanup(Func<Task> action) : IAsyncDisposable { public async ValueTask DisposeAsync() => await action(); }

    private static EmployeePolicyAcknowledgement Legacy(Guid tenant, int employee, Guid? policy = null) =>
        new() { TenantId = tenant, EmployeeId = employee, PolicyId = policy ?? Guid.NewGuid(), UserId = Guid.NewGuid() };

    private static EmployeePolicyAcknowledgement Esign(Guid tenant, int employee, long seq, string method = "esign_ess", Action<EmployeePolicyAcknowledgement>? tweak = null)
    {
        var a = new EmployeePolicyAcknowledgement
        {
            TenantId = tenant, EmployeeId = employee, PolicyId = Guid.NewGuid(), UserId = Guid.NewGuid(), Method = method,
            DocumentSha256 = new string('a', 64), TypedName = "Layla Hassan", IpAddress = "10.0.0.1", RecordHash = new string('b', 64), PrevHash = EsignConstants.ZeroHash,
            ChainSeq = seq, StepUpJti = Guid.NewGuid(), ConsentVersion = "ack-1", ConsentTextSha256 = new string('c', 64), WitnessUserId = method == "esign_hr_device" ? Guid.NewGuid() : null,
        };
        tweak?.Invoke(a);
        return a;
    }

    private static async Task<(Guid Tenant, int[] Employees)> SeedEmployeesAsync(ZayraDbContext db, int count, string english = "Layla Hassan", string arabic = "ليلى حسن")
    {
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var ids = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var e = new Employee { TenantId = tenant, EmployeeCode = $"P4{i}-{Guid.NewGuid():N}"[..14], FullName = english, EnglishName = english, ArabicName = arabic, Status = "Active", JoiningDate = DateTime.UtcNow.Date };
            db.Employees.Add(e);
            await db.SaveChangesAsync();
            ids.Add(e.Id);
        }
        return (tenant, [.. ids]);
    }

    private static byte[] Pdf(params byte[] extra) => [.. EsignFixture.PdfBytes, .. extra];

    private static InMemoryDocumentStorage NewStorage() => new();

    private static async Task<Guid> NewDocAsync(ZayraDbContext db, InMemoryDocumentStorage storage, Guid tenant, int employee, byte[]? bytes = null)
    {
        var id = Guid.NewGuid();
        var key = $"storage/documents/{tenant:N}/{id:N}.pdf";
        storage.Objects[key] = bytes ?? Pdf(Guid.NewGuid().ToByteArray());
        db.EmployeeDocuments.Add(new EmployeeDocument
        {
            Id = id, TenantId = tenant, EmployeeId = employee, DocumentType = "Employment contract", DocumentCategory = "Contract", FileName = "c.pdf", ContentType = "application/pdf",
            StorageUrl = key, ApprovalStatus = "Approved", SignatureRequiredAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private SignCommand Cmd(Guid tenant, int employee, Guid doc, byte[] bytes, Guid? user = null, string? token = null, string name = "Layla Hassan")
    {
        var u = user ?? Guid.NewGuid();
        return new SignCommand(tenant, employee, u, u, null, doc, new EsignSignBody(EsignFixture.Sha(bytes), ConsentCatalog.Current, name, "en", "pg"),
            token ?? Guid.NewGuid().ToString("N"), "10.1.1.1", "PgTest/1");
    }

    [Fact]
    public async Task Ack_UpdateAlwaysRaises_DeleteOnlyWithRetentionPurgeFlag()
    {
        await using var trigger = await WithTriggerAsync();
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var legacy = Legacy(tenant, 9001); var signed = Esign(tenant, 9002, 1);
        db.EmployeePolicyAcknowledgements.AddRange(legacy, signed);
        await db.SaveChangesAsync();   // INSERT is allowed

        foreach (var id in new[] { legacy.Id, signed.Id })   // legacy rows are covered too
        {
            var update = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync($"UPDATE employee_policy_acknowledgements SET user_id = NULL WHERE id = '{id}'"));
            update.MessageText.Should().Contain("append-only");
            var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync($"DELETE FROM employee_policy_acknowledgements WHERE id = '{id}'"));
            delete.MessageText.Should().Contain("append-only");
        }
        // The flag lifts DELETE only, and only inside the transaction that sets it; UPDATE never.
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await using (var tx = await c.BeginTransactionAsync())
        {
            await new NpgsqlCommand("SET LOCAL app.retention_purge = 'on'", c, tx).ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand($"UPDATE employee_policy_acknowledgements SET user_id = NULL WHERE id = '{legacy.Id}'", c, tx).ExecuteNonQueryAsync());
        }
        await using (var tx = await c.BeginTransactionAsync())
        {
            await new NpgsqlCommand("SET LOCAL app.retention_purge = 'on'", c, tx).ExecuteNonQueryAsync();
            (await new NpgsqlCommand($"DELETE FROM employee_policy_acknowledgements WHERE id = '{legacy.Id}'", c, tx).ExecuteNonQueryAsync()).Should().Be(1);
            await tx.CommitAsync();
        }
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync($"DELETE FROM employee_policy_acknowledgements WHERE id = '{signed.Id}'"));   // the flag did not outlive its transaction
    }

    [Fact]
    public async Task DocumentSignatures_AppRoleCannotUpdateOrDelete()
    {
        await using var trigger = await WithTriggerAsync();
        await ExecAsync($"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN CREATE ROLE {AppRole} NOLOGIN; END IF; END $$;");
        await ExecAsync($"GRANT ALL ON employee_policy_acknowledgements TO {AppRole}");
        await ExecAsync(AckAppendOnlySql.RevokeFromRole(AppRole));
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var ack = Esign(tenant, 9100, 1);
        db.EmployeePolicyAcknowledgements.Add(ack);
        await db.SaveChangesAsync();
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await new NpgsqlCommand($"SET ROLE {AppRole}", c).ExecuteNonQueryAsync();
        // layer two (grants): the role is refused before the trigger is even consulted
        var update = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand($"UPDATE employee_policy_acknowledgements SET user_id = NULL WHERE id = '{ack.Id}'", c).ExecuteNonQueryAsync());
        update.SqlState.Should().Be("42501");
        var delete = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand($"DELETE FROM employee_policy_acknowledgements WHERE id = '{ack.Id}'", c).ExecuteNonQueryAsync());
        delete.SqlState.Should().Be("42501");
        await new NpgsqlCommand("RESET ROLE", c).ExecuteNonQueryAsync();
        await ExecAsync($"REVOKE ALL ON employee_policy_acknowledgements FROM {AppRole}");
    }

    [Fact]
    public async Task MigrationRevoke_TakesUpdateAndDeleteAwayFromTheRuntimeRole()  // CD61
    {
        await using var seed = fx.CreateDb();
        var (tenant, employees) = await SeedEmployeesAsync(seed, 1);
        await ExecAsync("DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kynex_app') THEN CREATE ROLE kynex_app NOLOGIN; END IF; END $$;");
        await ExecAsync("GRANT ALL ON employee_policy_acknowledgements TO kynex_app");
        try
        {
            await ExecAsync(AckAppendOnlySql.RevokeFromAppRole);
            seed.EmployeePolicyAcknowledgements.Add(Esign(tenant, employees[0], 1));
            await seed.SaveChangesAsync();
            await using var c = new NpgsqlConnection(fx.ConnectionString);
            await c.OpenAsync();
            await new NpgsqlCommand("SET ROLE kynex_app", c).ExecuteNonQueryAsync();
            var update = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("UPDATE employee_policy_acknowledgements SET typed_name = 'x'", c).ExecuteNonQueryAsync());
            update.SqlState.Should().Be("42501");
            var remove = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("DE" + "LETE FROM employee_policy_acknowledgements", c).ExecuteNonQueryAsync());
            remove.SqlState.Should().Be("42501");
            (await new NpgsqlCommand("SELECT count(*) FROM employee_policy_acknowledgements", c).ExecuteScalarAsync()).Should().NotBeNull("the runtime role can still read");
            await ExecAsync(AckAppendOnlySql.GrantToAppRole);   // the migration's Down restores the baseline
        }
        finally { await ExecAsync("REVOKE ALL ON employee_policy_acknowledgements FROM kynex_app"); }
    }

    [Fact]
    public async Task Ack_LegacyRows_Untouched_ByMigration()
    {
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var legacy = Legacy(tenant, 9200);
        db.EmployeePolicyAcknowledgements.Add(legacy);
        await db.SaveChangesAsync();
        await using var trigger = await WithTriggerAsync();   // the migration's trigger arrives after the row exists
        await using var read = fx.CreateDb();
        var row = await read.EmployeePolicyAcknowledgements.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == legacy.Id);
        row.Method.Should().Be("legacy_click");
        new object?[] { row.DocumentSha256, row.TypedName, row.IpAddress, row.RecordHash, row.PrevHash, row.ChainSeq, row.StepUpJti, row.WitnessUserId, row.CertificateDocumentId, row.ConsentVersion }
            .Should().AllSatisfy(v => v.Should().BeNull());
        row.UserId.Should().Be(legacy.UserId); row.AcknowledgedAtUtc.Should().BeCloseTo(legacy.AcknowledgedAtUtc, TimeSpan.FromMilliseconds(5));
        // and a new legacy row can still be written alongside (the mobile route)
        await using var more = fx.CreateDb();
        more.EmployeePolicyAcknowledgements.Add(Legacy(tenant, 9201));
        await more.SaveChangesAsync();
    }

    [Fact]
    public async Task Migration_P4_ExpandOnly_ConstraintsHoldForNewRowsOnly()
    {
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        db.EmployeePolicyAcknowledgements.AddRange(Legacy(tenant, 9300), Esign(tenant, 9301, 1), Esign(tenant, 9302, 2, "esign_hr_device"));
        await db.SaveChangesAsync();

        async Task<string?> Violation(Action<EmployeePolicyAcknowledgement> tweak, string method = "esign_ess", long seq = 50)
        {
            await using var bad = fx.CreateDb();
            bad.EmployeePolicyAcknowledgements.Add(Esign(tenant, 9390, seq, method, tweak));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => bad.SaveChangesAsync());
            return (ex.InnerException as PostgresException)?.ConstraintName;
        }
        (await Violation(a => a.RecordHash = null)).Should().Be(OnboardingP4EsignModel.EvidenceCheck);
        (await Violation(a => a.TypedName = null)).Should().Be(OnboardingP4EsignModel.EvidenceCheck);
        (await Violation(a => a.StepUpJti = null)).Should().Be(OnboardingP4EsignModel.EvidenceCheck);
        (await Violation(a => a.WitnessUserId = null, "esign_hr_device")).Should().Be(OnboardingP4EsignModel.WitnessCheck);
        (await Violation(a => a.WitnessUserId = Guid.NewGuid())).Should().Be(OnboardingP4EsignModel.WitnessCheck);
        (await Violation(a => a.Method = "typed_in_crayon")).Should().Be(OnboardingP4EsignModel.MethodCheck);
        (await Violation(a => { }, seq: 1)).Should().Be(OnboardingP4EsignModel.ChainIndex, "a second record cannot take a sequence number that exists");
        var jti = Guid.NewGuid();
        await using (var one = fx.CreateDb()) { one.EmployeePolicyAcknowledgements.Add(Esign(tenant, 9391, 60, tweak: a => a.StepUpJti = jti)); await one.SaveChangesAsync(); }
        (await Violation(a => a.StepUpJti = jti, seq: 61)).Should().Be(OnboardingP4EsignModel.StepUpJtiIndex);
        // the legacy path is not subject to the new rules, and the indexes are partial
        (await fx.GetIndexDefinitionAsync(OnboardingP4EsignModel.ChainIndex)).Should().Contain("WHERE").And.Contain("legacy_click");
        (await fx.GetIndexDefinitionAsync(OnboardingP4EsignModel.SignPendingIndex)).Should().Contain("WHERE");
    }

    [Fact]
    public async Task Chain_ConcurrentSigns_NoForkNoGap()
    {
        await using var seed = fx.CreateDb();
        var (tenant, employees) = await SeedEmployeesAsync(seed, 8);
        var storage = NewStorage();
        var jobs = new List<(SignCommand Cmd, byte[] Bytes)>();
        foreach (var e in employees)
        {
            var bytes = Pdf(Guid.NewGuid().ToByteArray());
            var doc = await NewDocAsync(seed, storage, tenant, e, bytes);
            jobs.Add((Cmd(tenant, e, doc, bytes), bytes));
        }
        var gate = new TaskCompletionSource();
        var tasks = jobs.Select(async j =>
        {
            await gate.Task;
            await using var db = fx.CreateDb();
            return await new PolicySigningService(db, storage, new FakeStepUp()).SignAsync(j.Cmd, default);
        }).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        var outcomes = results.ToList();
        outcomes.Should().HaveCount(8);
        outcomes.Should().AllSatisfy(o => o.Status.Should().Be(201, o.Code));

        await using var verify = fx.CreateDb();
        var rows = await verify.EmployeePolicyAcknowledgements.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId == tenant && a.Method != "legacy_click").OrderBy(a => a.ChainSeq).ToListAsync();
        rows.Select(r => r.ChainSeq).Should().Equal(Enumerable.Range(1, 8).Select(i => (long?)i), "no gap and no fork");
        rows[0].PrevHash.Should().Be(EsignConstants.ZeroHash);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) rows[i].PrevHash.Should().Be(rows[i - 1].RecordHash);
            SignatureChain.IsRecordValid(rows[i].PrevHash!, rows[i].RecordHash!, new AckEvidence(rows[i].TenantId, rows[i].EmployeeId, rows[i].PolicyId, rows[i].DocumentSha256!,
                rows[i].TypedName!, rows[i].AcknowledgedAtUtc, rows[i].IpAddress!, rows[i].UserAgent, rows[i].UserId!.Value, rows[i].WitnessUserId, rows[i].ConsentTextSha256!, rows[i].Method)).Should().BeTrue($"row {i + 1} verifies after a database round trip");
        }
    }

    [Fact]
    public async Task Sign_Replay_ReturnsConflictWithExistingAck()
    {
        await using var seed = fx.CreateDb();
        var (tenant, employees) = await SeedEmployeesAsync(seed, 1);
        var storage = NewStorage();
        var bytes = Pdf((byte)7);
        var doc = await NewDocAsync(seed, storage, tenant, employees[0], bytes);
        var user = Guid.NewGuid();
        // two simultaneous attempts on the same document: exactly one record
        var gate = new TaskCompletionSource();
        async Task<SignOutcome> Go()
        {
            await gate.Task;
            await using var db = fx.CreateDb();
            return await new PolicySigningService(db, storage, new FakeStepUp()).SignAsync(Cmd(tenant, employees[0], doc, bytes, user), default);
        }
        var a = Go(); var b = Go();
        gate.SetResult();
        var both = await Task.WhenAll(a, b);
        both.Count(o => o.Status == 201).Should().Be(1);
        var loser = both.Single(o => o.Status != 201);
        loser.Status.Should().Be(409); loser.Code.Should().Be("already_signed");
        await using var db2 = fx.CreateDb();
        (await db2.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && x.PolicyId == doc)).Should().Be(1);
        // a later replay points at the existing record
        var again = await new PolicySigningService(db2, storage, new FakeStepUp()).SignAsync(Cmd(tenant, employees[0], doc, bytes, user), default);
        again.Status.Should().Be(409); again.AckId.Should().Be(both.Single(o => o.Status == 201).AckId);
    }

    [Fact]
    public async Task StepUpJti_Reuse_Refused()
    {
        await using var seed = fx.CreateDb();
        var (tenant, employees) = await SeedEmployeesAsync(seed, 2);
        var storage = NewStorage();
        var bytes1 = Pdf((byte)1); var bytes2 = Pdf((byte)2);
        var doc1 = await NewDocAsync(seed, storage, tenant, employees[0], bytes1);
        var doc2 = await NewDocAsync(seed, storage, tenant, employees[1], bytes2);
        var token = Guid.NewGuid().ToString("N");
        await using var db = fx.CreateDb();
        var svc = new PolicySigningService(db, storage, new FakeStepUp());
        (await svc.SignAsync(Cmd(tenant, employees[0], doc1, bytes1, token: token), default)).Status.Should().Be(201);
        await using var db2 = fx.CreateDb();
        var second = await new PolicySigningService(db2, storage, new FakeStepUp()).SignAsync(Cmd(tenant, employees[1], doc2, bytes2, token: token), default);
        second.Status.Should().Be(401); second.Code.Should().Be("step_up_required");
        await using var db3 = fx.CreateDb();
        (await db3.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(x => x.PolicyId == doc2)).Should().Be(0);
        storage.Objects.Keys.Count(k => k.Contains("/esign/")).Should().Be(1, "the refused attempt left no orphan certificate in storage");
    }

    [Fact]
    public async Task Chain_TamperedRowDetectedByVerify()
    {
        await using var seed = fx.CreateDb();
        var (tenant, employees) = await SeedEmployeesAsync(seed, 3);
        var storage = NewStorage();
        var acks = new List<Guid>();
        foreach (var e in employees)
        {
            var bytes = Pdf((byte)e);
            var doc = await NewDocAsync(seed, storage, tenant, e, bytes);
            await using var db = fx.CreateDb();
            var o = await new PolicySigningService(db, storage, new FakeStepUp()).SignAsync(Cmd(tenant, e, doc, bytes), default);
            o.Status.Should().Be(201, o.Code); acks.Add(o.AckId!.Value);
        }
        async Task<VerifyOutcome> Verify(Guid ack) { await using var db = fx.CreateDb(); return await new PolicySigningService(db, storage, new FakeStepUp()).VerifyAsync(tenant, ack, null, default); }
        (await Verify(acks[0])).Valid.Should().BeTrue(); (await Verify(acks[1])).Valid.Should().BeTrue(); (await Verify(acks[2])).Valid.Should().BeTrue();
        // someone with database access edits the second record (no trigger installed in this test: this is the case the chain exists for)
        await ExecAsync($"UPDATE employee_policy_acknowledgements SET typed_name = 'Forged Name' WHERE id = '{acks[1]}'");
        (await Verify(acks[0])).ChainIntact.Should().BeTrue();
        var second = await Verify(acks[1]);
        second.Valid.Should().BeFalse(); second.ChainIntact.Should().BeFalse();
        var third = await Verify(acks[2]);
        third.ChainIntact.Should().BeFalse("the next record's link no longer matches the edited one");
        third.DocumentIntact.Should().BeTrue();
    }
}
