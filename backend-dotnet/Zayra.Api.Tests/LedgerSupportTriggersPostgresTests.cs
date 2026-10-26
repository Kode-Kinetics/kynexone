using FluentAssertions;
using Npgsql;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-E, migration M2, the guards around the ledger (v1-spec 1.11 (b)-(d), AM-12), on real PostgreSQL:
/// the cost-centre in-use guard (T-047, T-048 database halves), the parent-cycle and depth guard (T-054), the frozen
/// export artifact (T-053) and the payroll stamp freeze on a Locked run (S-021). The application layer answers these
/// with a 409 first; these tests prove the same rules hold for anything that reaches the table directly.
/// </summary>
[Collection("LedgerDb")]
[Trait("Category", "Integration")]
public sealed class LedgerSupportTriggersPostgresTests
{
    private const string Immutable = LedgerDbFixture.Cc23001, AtCommit = LedgerDbFixture.Cc23514;
    private static int _seq = 40_000;
    private readonly LedgerDbFixture _db;
    public LedgerSupportTriggersPostgresTests(LedgerDbFixture db) => _db = db;

    private static string Q(Guid g) => LedgerDbFixture.Q(g);
    private static string Q(string s) => LedgerDbFixture.Q(s);

    // ── cost-centre in-use guard (T-047, T-048) ─────────────────────────────────────────────────────────────────

    private async Task<Guid> NewCostCentre(Guid? company, Guid? parent = null)
    {
        var id = Guid.NewGuid();
        await _db.Exec(await _db.Insert("cost_centers", _db.CostCentre(id, company, $"CC-{id.ToString("N")[..10]}", parent)));
        return id;
    }

    /// <summary>Makes a live row of <paramref name="table"/> point at the cost centre; returns the SQL that releases it again.</summary>
    private async Task<string> Reference(string table, Guid cc, Guid company)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        switch (table)
        {
            case "departments":
            {
                var id = Guid.NewGuid();
                await _db.Exec(await _db.Insert("departments", new()
                {
                    ["id"] = Q(id), ["tenant_id"] = Q(_db.Tenant), ["company_id"] = Q(company), ["code"] = Q($"D-{tag}"), ["name_en"] = Q($"D-{tag}"),
                    ["cost_center_id"] = Q(cc), ["is_deleted"] = "false",
                }));
                return $"UPDATE departments SET is_deleted = true WHERE id = '{id}'";
            }
            case "positions":
            {
                var id = Guid.NewGuid();
                await _db.Exec(await _db.Insert("positions", new()
                {
                    ["id"] = Q(id), ["tenant_id"] = Q(_db.Tenant), ["company_id"] = Q(company), ["code"] = Q($"P-{tag}"),
                    ["cost_center_id"] = Q(cc), ["is_deleted"] = "false",
                }));
                return $"UPDATE positions SET is_deleted = true WHERE id = '{id}'";
            }
            case "employees":
            {
                var id = Interlocked.Increment(ref _seq);
                await _db.Exec(await _db.Insert("employees", new()
                {
                    ["id"] = id.ToString(), ["tenant_id"] = Q(_db.Tenant), ["company_id"] = Q(company), ["employee_code"] = Q($"E-{tag}"), ["full_name"] = Q($"E-{tag}"),
                    ["cost_center_id"] = Q(cc), ["public_id"] = Q(Guid.NewGuid()), ["is_deleted"] = "false",
                }));
                return $"UPDATE employees SET is_deleted = true WHERE id = {id}";
            }
            case "gl_account_mappings":
            {
                var account = Guid.NewGuid();
                await _db.Exec(await _db.Insert("gl_accounts", new() { ["id"] = Q(account), ["tenant_id"] = Q(_db.Tenant), ["code"] = Q($"A-{tag}"), ["name"] = Q($"A-{tag}"), ["account_type"] = "'Expense'", ["is_active"] = "true" }));
                var id = Guid.NewGuid();
                await _db.Exec(await _db.Insert("gl_account_mappings", new()
                {
                    ["id"] = Q(id), ["tenant_id"] = Q(_db.Tenant), ["company_id"] = "NULL", ["driver_key"] = Q($"EARN:{tag}"), ["account_id"] = Q(account),
                    ["is_active"] = "true", ["segment_cost_center_id"] = Q(cc),
                }));
                return $"UPDATE gl_account_mappings SET segment_cost_center_id = NULL WHERE id = '{id}'";
            }
            case "companies":
                await _db.Exec($"UPDATE companies SET default_cost_center_id = '{cc}' WHERE id = '{company}'");
                return $"UPDATE companies SET default_cost_center_id = NULL WHERE id = '{company}'";
            case "cost_centers":
            {
                var child = await NewCostCentre(company, parent: cc);
                return $"UPDATE cost_centers SET is_active = false WHERE id = '{child}'";
            }
            default: throw new ArgumentOutOfRangeException(nameof(table));
        }
    }

    private async Task<Guid> NewCompany()
    {
        var id = Guid.NewGuid();
        await _db.Exec(await _db.Insert("companies", new()
        {
            ["id"] = Q(id), ["tenant_id"] = Q(_db.Tenant), ["legal_name_en"] = Q($"Co {id:N}"), ["registration_number"] = Q($"R-{id:N}"),
            ["email_domain"] = Q($"{id:N}.example"), ["is_active"] = "true", ["is_deleted"] = "false",
        }));
        return id;
    }

    public static IEnumerable<object[]> Referrers() =>
        new[] { "departments", "positions", "employees", "gl_account_mappings", "companies", "cost_centers" }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Referrers))]
    public async Task CostCentre_InUse_CannotBeDeactivated_SoftDeleted_OrMovedToAnotherCompany(string table)
    {
        var company = await NewCompany();
        var other = await NewCompany();
        var cc = await NewCostCentre(company);
        var release = await Reference(table, cc, company);

        foreach (var change in new[] { "is_active = false", "is_deleted = true", $"company_id = '{other}'" })
        {
            var ex = await _db.Expect(Immutable, $"UPDATE cost_centers SET {change} WHERE id = '{cc}'", $"{table} still points at it");
            ex.MessageText.Should().Contain($"cost_centre_in_use:").And.Contain($"{table}=1", "machine-readable, with the count (v1-spec 1.11 (b))");
        }
        (await _db.Scalar<long>($"SELECT count(*) FROM cost_centers WHERE id = '{cc}' AND is_active AND NOT is_deleted AND company_id = '{company}'")).Should().Be(1);

        await _db.Exec(release);
        (await _db.Exec($"UPDATE cost_centers SET is_active = false WHERE id = '{cc}'")).Should().Be(1, "nothing live points at it any more");
    }

    [Fact]
    public async Task CostCentre_UsedOnlyByHistoricalPostedLines_MayBeDeactivated_Renamed_AndSoftDeleted_ButNotRecodedOrMoved()
    {
        var company = await NewCompany();
        var cc = await NewCostCentre(company);
        var code = await _db.Scalar<string>($"SELECT code FROM cost_centers WHERE id = '{cc}'");
        await _db.Post(company, cc: cc, ccCode: code, ccName: "snapshot");
        await _db.Post(company, cc: cc, ccCode: code, ccName: "snapshot");

        (await _db.Exec($"UPDATE cost_centers SET name = 'renamed' WHERE id = '{cc}'")).Should().Be(1, "a rename of name is allowed (T-048)");
        var recode = await _db.Expect(Immutable, $"UPDATE cost_centers SET code = 'NEW-CODE' WHERE id = '{cc}'", "code is frozen once a posted line references it (T-048)");
        recode.MessageText.Should().Contain("cost_centre_in_use:").And.Contain("finance_gl_entries=2");
        await _db.Expect(Immutable, $"UPDATE cost_centers SET company_id = '{await NewCompany()}' WHERE id = '{cc}'", "company is frozen too");

        (await _db.Exec($"UPDATE cost_centers SET is_active = false WHERE id = '{cc}'")).Should().Be(1, "history is protected by the snapshots, not by keeping the centre alive");
        (await _db.Exec($"UPDATE cost_centers SET is_deleted = true WHERE id = '{cc}'")).Should().Be(1);
    }

    [Fact]
    public async Task CostCentre_NotReferencedByTheLedger_MayBeRecoded()
    {
        var company = await NewCompany();
        var cc = await NewCostCentre(company);
        (await _db.Exec($"UPDATE cost_centers SET code = 'RECODED-{cc.ToString("N")[..6]}' WHERE id = '{cc}'")).Should().Be(1);
    }

    // ── parent cycle and depth (T-054) ──────────────────────────────────────────────────────────────────────────

    private async Task<List<Guid>> Chain(Guid company, int depth)
    {
        var ids = new List<Guid>();
        Guid? parent = null;
        for (var i = 0; i < depth; i++)
        {
            var id = await NewCostCentre(company, parent);
            ids.Add(id);
            parent = id;
        }
        return ids;
    }

    [Fact]
    public async Task ParentCycle_IsRefused_ForADirectLoop_AndALongOne()
    {
        var company = await NewCompany();
        var chain = await Chain(company, 3); // chain[0] <- chain[1] <- chain[2]
        var two = await _db.Expect(AtCommit, $"UPDATE cost_centers SET parent_id = '{chain[1]}' WHERE id = '{chain[0]}'", "A under its own child");
        two.MessageText.Should().Contain("cost_centre_cycle");
        await _db.Expect(AtCommit, $"UPDATE cost_centers SET parent_id = '{chain[2]}' WHERE id = '{chain[0]}'", "A under its own grandchild");
        (await _db.Scalar<long>($"SELECT count(*) FROM cost_centers WHERE id = '{chain[0]}' AND parent_id IS NULL")).Should().Be(1);
    }

    [Fact]
    public async Task ParentDepth_TenLevelsIsTheLimit_OnInsert()
    {
        var company = await NewCompany();
        var chain = await Chain(company, 10);
        var tooDeep = Guid.NewGuid();
        var ex = await _db.Expect(AtCommit, await _db.Insert("cost_centers", _db.CostCentre(tooDeep, company, $"CC-{tooDeep.ToString("N")[..10]}", chain[^1])), "an eleventh level");
        ex.MessageText.Should().Contain("cost_centre_depth");
    }

    [Fact]
    public async Task ParentDepth_ReparentingASubtreeCannotPushItsLeavesPastTheLimit()
    {
        var company = await NewCompany();
        var deep = await Chain(company, 8);
        var subtree = await Chain(company, 3);                  // three levels of its own
        await _db.Expect(AtCommit, $"UPDATE cost_centers SET parent_id = '{deep[^1]}' WHERE id = '{subtree[0]}'", "8 + 3 = 11 levels");
        (await _db.Exec($"UPDATE cost_centers SET parent_id = '{deep[^2]}' WHERE id = '{subtree[0]}'")).Should().Be(1, "7 + 3 = 10 levels");
    }

    // ── frozen export artifact (T-053) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportLines_CannotBeUpdatedOrDeleted()
    {
        var entry = await _db.Post(_db.CompanyA);
        var export = await _db.AddExportEvidence(entry, "DOC-L");
        var update = await _db.Expect(Immutable, $"UPDATE gl_journal_export_lines SET description = 'rewritten' WHERE gl_journal_export_id = '{export}'");
        update.MessageText.Should().Contain("gl_journal_export_lines").And.Contain("frozen");
        await _db.Expect(Immutable, $"UPDATE gl_journal_export_lines SET amount = amount + 1 WHERE gl_journal_export_id = '{export}'");
        await _db.Expect(Immutable, $"DELETE FROM gl_journal_export_lines WHERE gl_journal_export_id = '{export}'");
        (await _db.Scalar<long>($"SELECT count(*) FROM gl_journal_export_lines WHERE gl_journal_export_id = '{export}' AND description <> 'rewritten'")).Should().Be(1);
    }

    [Fact]
    public async Task ExportLines_AreErasedWithTheirTenant_WhenItIsPurged()
    {
        var purged = Guid.NewGuid();
        await _db.Exec(await _db.Insert("tenants", new() { ["id"] = Q(purged), ["name"] = "'Gone'", ["slug"] = Q($"gone__deleted_{purged.ToString("N")[..8]}"), ["is_active"] = "false" }));
        var entry = await _db.Post(null, tenant: purged);
        var export = await _db.AddExportEvidence(entry, "DOC-P", tenant: purged);
        (await _db.Exec($"DELETE FROM gl_journal_export_lines WHERE gl_journal_export_id = '{export}'")).Should().Be(1);
    }

    // ── payroll stamps on a Locked run (S-021) ──────────────────────────────────────────────────────────────────

    private sealed record SeededRun(Guid Run, Guid Earning, Guid Deduction, Guid Slip);

    private async Task<SeededRun> SeedRun(string status = "Approved", bool stamped = true)
    {
        // Stamped while still Approved (that is when stamps are legal), THEN moved on to the status under test.
        var run = Guid.NewGuid();
        var earning = Guid.NewGuid();
        var deduction = Guid.NewGuid();
        var slip = Guid.NewGuid();
        var n = Interlocked.Increment(ref _seq);
        await _db.Exec(await _db.Insert("payroll_runs", new()
        {
            ["id"] = Q(run), ["tenant_id"] = Q(_db.Tenant), ["company_id"] = Q(_db.CompanyA), ["status"] = "'Approved'",
            ["year"] = (2000 + n % 90).ToString(), ["month"] = (1 + n % 12).ToString(), ["run_type"] = "'OffCycle'",
        }));
        await _db.Exec(await _db.Insert("payroll_earnings", new() { ["id"] = Q(earning), ["tenant_id"] = Q(_db.Tenant), ["payroll_run_id"] = Q(run) }));
        await _db.Exec(await _db.Insert("payroll_deductions", new() { ["id"] = Q(deduction), ["tenant_id"] = Q(_db.Tenant), ["company_id"] = Q(_db.CompanyA), ["payroll_run_id"] = Q(run) }));
        await _db.Exec(await _db.Insert("payroll_slips", new() { ["id"] = Q(slip), ["tenant_id"] = Q(_db.Tenant), ["run_id"] = Q(run), ["status"] = "'Processed'" }));
        if (stamped)
        {
            await _db.Exec($"UPDATE payroll_earnings SET cost_center_id = '{_db.CcA1}', cost_center_source = 'Employee' WHERE id = '{earning}'");
            await _db.Exec($"UPDATE payroll_deductions SET cost_center_id = '{_db.CcA1}', cost_center_source = 'Employee' WHERE id = '{deduction}'");
            await _db.Exec($"UPDATE payroll_slips SET cost_center_id = '{_db.CcA1}' WHERE id = '{slip}'");
            await _db.Exec($"UPDATE payroll_runs SET cost_center_resolution_hash = '{new string('a', 64)}' WHERE id = '{run}'");
        }
        if (status != "Approved") await _db.Exec($"UPDATE payroll_runs SET status = '{status}' WHERE id = '{run}'");
        return new SeededRun(run, earning, deduction, slip);
    }

    private string Stamp(SeededRun r, Guid? cc = null, string source = "Position") =>
        $"UPDATE payroll_earnings SET cost_center_id = '{cc ?? _db.CcA2}', cost_center_source = '{source}' WHERE id = '{r.Earning}'; " +
        $"UPDATE payroll_deductions SET cost_center_id = '{cc ?? _db.CcA2}', cost_center_source = '{source}' WHERE id = '{r.Deduction}'; " +
        $"UPDATE payroll_slips SET cost_center_id = '{cc ?? _db.CcA2}' WHERE id = '{r.Slip}'";

    [Fact]
    public async Task Stamps_AreWritable_WhileTheRunIsNotLocked()
    {
        var r = await SeedRun("Approved");
        await _db.RunTx(Stamp(r), $"UPDATE payroll_runs SET cost_center_resolution_hash = '{new string('b', 64)}', cost_accounting_applied = false WHERE id = '{r.Run}'");
        var draft = await SeedRun("Processed");
        await _db.RunTx(Stamp(draft));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockingTheRun_AndStampingIt_InOneTransaction_Commits_InEitherStatementOrder(bool lockFirst)
    {
        var r = await SeedRun("Approved");
        var lockSql = $"UPDATE payroll_runs SET status = 'Locked', cost_accounting_applied = true WHERE id = '{r.Run}'";
        if (lockFirst) await _db.RunTx(lockSql, Stamp(r));
        else await _db.RunTx(Stamp(r), lockSql);
        (await _db.Scalar<string>($"SELECT status FROM payroll_runs WHERE id = '{r.Run}'")).Should().Be("Locked");
        (await _db.Scalar<string>($"SELECT cost_center_id::text FROM payroll_earnings WHERE id = '{r.Earning}'")).Should().Be(_db.CcA2.ToString());
    }

    [Theory]
    [InlineData("Locked")]
    [InlineData("Paid")]
    [InlineData("Voided")]
    public async Task AfterLock_TheStampColumns_AreFrozen_OnTheRunAndOnAllThreeLineTables(string status)
    {
        var r = await SeedRun(status);

        await _db.ExpectTx(AtCommit, "earning cost centre", $"UPDATE payroll_earnings SET cost_center_id = '{_db.CcA2}' WHERE id = '{r.Earning}'");
        await _db.ExpectTx(AtCommit, "earning source", $"UPDATE payroll_earnings SET cost_center_source = 'Department' WHERE id = '{r.Earning}'");
        await _db.ExpectTx(AtCommit, "deduction cost centre", $"UPDATE payroll_deductions SET cost_center_id = NULL WHERE id = '{r.Deduction}'");
        await _db.ExpectTx(AtCommit, "deduction source", $"UPDATE payroll_deductions SET cost_center_source = 'Department' WHERE id = '{r.Deduction}'");
        var slip = await _db.ExpectTx(AtCommit, "slip cost centre", $"UPDATE payroll_slips SET cost_center_id = '{_db.CcA2}' WHERE id = '{r.Slip}'");
        slip.MessageText.Should().Contain("payroll_slips").And.Contain("frozen");

        var hash = await _db.Expect(Immutable, $"UPDATE payroll_runs SET cost_center_resolution_hash = '{new string('c', 64)}' WHERE id = '{r.Run}'");
        hash.MessageText.Should().Contain("cost_center_resolution_hash");
        await _db.Expect(Immutable, $"UPDATE payroll_runs SET cost_accounting_applied = true WHERE id = '{r.Run}'");
        await _db.Expect(Immutable, $"UPDATE payroll_runs SET cost_center_resolution_hash = NULL WHERE id = '{r.Run}'");
    }

    [Fact]
    public async Task AfterLock_EverythingElseOnTheRunAndItsLinesStillUpdates()
    {
        var r = await SeedRun("Locked");
        (await _db.Exec($"UPDATE payroll_earnings SET amount = 12.5 WHERE id = '{r.Earning}'")).Should().Be(1);
        (await _db.Exec($"UPDATE payroll_slips SET status = 'Final' WHERE id = '{r.Slip}'")).Should().Be(1);
        (await _db.Exec($"UPDATE payroll_runs SET status = 'Paid' WHERE id = '{r.Run}'")).Should().Be(1, "Locked to Paid does not touch a stamp");
        (await _db.Exec($"UPDATE payroll_runs SET status = 'Voided' WHERE id = '{r.Run}'")).Should().Be(1);
        (await _db.Exec($"UPDATE payroll_earnings SET cost_center_id = cost_center_id, cost_center_source = cost_center_source WHERE id = '{r.Earning}'")).Should().Be(1, "rewriting a stamp with itself is not a change");
    }

    [Fact]
    public async Task LockingOneRun_DoesNotUnfreezeAnotherRunsStamps()
    {
        var alreadyLocked = await SeedRun("Locked");
        var locking = await SeedRun("Approved");
        await _db.ExpectTx(AtCommit, "the Lock of run B says nothing about run A",
            $"UPDATE payroll_runs SET status = 'Locked' WHERE id = '{locking.Run}'",
            $"UPDATE payroll_earnings SET cost_center_id = '{_db.CcA2}' WHERE id = '{alreadyLocked.Earning}'");
    }

    [Fact]
    public async Task TheLockWitness_DoesNotOutliveItsTransaction()
    {
        var r = await SeedRun("Approved");
        await _db.RunTx($"UPDATE payroll_runs SET status = 'Locked', cost_accounting_applied = true WHERE id = '{r.Run}'", Stamp(r));
        // A LATER unit of work (very likely on the same pooled connection) gets no excuse from the earlier Lock.
        await _db.ExpectTx(AtCommit, "stamping after the Lock committed", Stamp(r, _db.CcA1, "Department"));
    }
}
