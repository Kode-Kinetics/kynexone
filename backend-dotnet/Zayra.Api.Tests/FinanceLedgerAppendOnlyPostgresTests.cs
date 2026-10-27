using FluentAssertions;
using Npgsql;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-E, migration M2 (<c>*_FinanceGlEntriesAppendOnly</c>), the <c>finance_gl_entries</c> half, on real PostgreSQL.
/// v1-spec 1.8/1.11 as rewritten by AM-11 and AM-12: a posted line is frozen in every economic field and cannot be
/// deleted; <c>is_reversed</c> flips only with a matching contra; a reversal row is validated at insert; the
/// <c>erp_*</c> columns follow the state machine the export code already implies. Cites T-049, T-050/T-051 (already
/// proven in M1's tests), S-017, S-022, S-023, S-025, S-026. Raw SQL: these are database rules, so the application
/// paths that must survive them are swept separately (<see cref="FinanceLedgerWritersPostgresTests"/>).
/// </summary>
[Collection("LedgerDb")]
[Trait("Category", "Integration")]
public sealed class FinanceLedgerAppendOnlyPostgresTests
{
    private const string Immutable = LedgerDbFixture.Cc23001, AtCommit = LedgerDbFixture.Cc23514;
    private readonly LedgerDbFixture _db;
    public FinanceLedgerAppendOnlyPostgresTests(LedgerDbFixture db) => _db = db;

    // ── frozen columns (T-049, S-025, S-026) ─────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> FrozenAssignments() => new (string Column, string Assignment)[]
    {
        ("id", "id = gen_random_uuid()"),
        ("tenant_id", "tenant_id = gen_random_uuid()"),
        ("source_module", "source_module = 'Loan'"),
        ("source_entity_id", "source_entity_id = gen_random_uuid()"),
        ("source_entity_ref", "source_entity_ref = 'RUN-2'"),
        ("event_type", "event_type = 'PayrollVoid'"),
        ("debit_account", "debit_account = '9999 - Elsewhere'"),
        ("credit_account", "credit_account = '9998 - Elsewhere'"),
        ("amount", "amount = amount + 1"),
        ("currency", "currency = 'USD'"),
        ("period", "period = '2026-10'"),
        ("entry_date", "entry_date = entry_date + 1"),
        ("description", "description = 'rewritten'"),
        ("posted_by", "posted_by = gen_random_uuid()"),
        ("posted_by_name", "posted_by_name = 'someone else'"),
        ("created_at_utc", "created_at_utc = created_at_utc + interval '1 day'"),
        ("reversal_of_entry_id", "reversal_of_entry_id = gen_random_uuid()"),
        ("posting_key", "posting_key = 'another-key'"),
        ("cost_center_id", "cost_center_id = NULL, cost_center_code = NULL, cost_center_name = NULL, cost_center_gl_segment = NULL"),
        ("cost_center_code", "cost_center_code = 'CC-X'"),
        ("cost_center_name", "cost_center_name = 'renamed'"),
        ("cost_center_gl_segment", "cost_center_gl_segment = 'S-9'"),
    }.Select(a => new object[] { a.Column, a.Assignment });

    [Theory]
    [MemberData(nameof(FrozenAssignments))]
    public async Task EveryPostedEconomicColumn_IsFrozen_AfterInsert(string column, string assignment)
    {
        var id = await _db.Post(_db.CompanyA, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1", key: $"k-{Guid.NewGuid():N}");

        var ex = await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET {assignment} WHERE id = '{id}'", $"{column} is frozen");
        ex.MessageText.Should().Contain("append-only").And.Contain(column);
        (await _db.Scalar<long>($"SELECT count(*) FROM finance_gl_entries WHERE id = '{id}' AND amount = 100 AND description = 'row'")).Should().Be(1, "nothing was written");
    }

    [Fact]
    public async Task CostCentreId_CannotMoveToAnotherCostCentre_EvenOneOfTheSameCompany()
    {
        var id = await _db.Post(_db.CompanyA, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET cost_center_id = '{_db.CcA2}', cost_center_code = 'CC-A2', cost_center_name = 'CC A2' WHERE id = '{id}'");
    }

    [Fact]
    public async Task CompanyAttribution_HasNoNullToValueException_AndCannotMoveToAnotherCompany()
    {
        var unattributed = await _db.Post(null);
        var ex = await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET company_id = '{_db.CompanyA}' WHERE id = '{unattributed}'", "AM-12 drops the NULL to value exception (S-026)");
        ex.MessageText.Should().Contain("company_id");

        var attributed = await _db.Post(_db.CompanyA);
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET company_id = '{_db.CompanyB}' WHERE id = '{attributed}'");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET company_id = NULL WHERE id = '{attributed}'");
    }

    [Fact]
    public async Task PostingKey_CannotBeSetOnARowThatWasPostedWithout()
    {
        var id = await _db.Post(_db.CompanyA);
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET posting_key = 'late-{Guid.NewGuid():N}' WHERE id = '{id}'");
    }

    [Fact]
    public async Task AnUpdateThatChangesNothingProtected_IsAccepted()
    {
        var id = await _db.Post(_db.CompanyA, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1");
        (await _db.Exec($"UPDATE finance_gl_entries SET amount = amount, description = description, company_id = company_id, cost_center_id = cost_center_id WHERE id = '{id}'"))
            .Should().Be(1, "a statement that rewrites a value with itself is not a change");
        (await _db.Exec($"UPDATE finance_gl_entries SET erp_posting_status = 'ReadyForErp', erp_status_changed_at_utc = now() WHERE id = '{id}'")).Should().Be(1);
    }

    // ── delete / bulk removal (T-049) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_IsRefused_RowByRowAndInBulk()
    {
        var id = await _db.Post(_db.CompanyA);
        var ex = await _db.Expect(Immutable, $"DELETE FROM finance_gl_entries WHERE id = '{id}'");
        ex.MessageText.Should().Contain("append-only").And.Contain("DELETE");
        await _db.Expect(Immutable, $"DELETE FROM finance_gl_entries WHERE tenant_id = '{_db.Tenant}'", "a bulk delete is refused at its first row");
        (await _db.Scalar<long>($"SELECT count(*) FROM finance_gl_entries WHERE id = '{id}'")).Should().Be(1);
    }

    [Fact]
    public async Task Truncate_IsRefused()
    {
        await _db.Post(_db.CompanyA);
        // A plain TRUNCATE already fails (loan tables reference the ledger); CASCADE is the form that would empty it.
        var ex = await _db.Expect(Immutable, "TRUNCATE TABLE finance_gl_entries CASCADE", "row triggers do not fire on TRUNCATE, so a statement trigger must");
        ex.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task Delete_IsPermitted_OnlyWhileTheTenantItselfIsBeingPurged()
    {
        // PlatformController.PurgeTenant / SoftDeletedTenantRule erase every tenant-owned row of a SOFT-DELETED tenant (slug
        // carries "__deleted_", is_active = false): the one legitimate removal. A live tenant's ledger stays undeletable.
        var purged = Guid.NewGuid();
        await _db.Exec(await _db.Insert("tenants", new()
        {
            ["id"] = LedgerDbFixture.Q(purged), ["name"] = "'Gone'", ["slug"] = $"'gone__deleted_{purged.ToString("N")[..8]}'", ["is_active"] = "false",
        }));
        var gone = await _db.Post(null, tenant: purged);
        (await _db.Exec($"DELETE FROM finance_gl_entries WHERE id = '{gone}'")).Should().Be(1);

        var live = Guid.NewGuid();
        await _db.Exec(await _db.Insert("tenants", new()
        {
            ["id"] = LedgerDbFixture.Q(live), ["name"] = "'Live'", ["slug"] = $"'live-{live:N}'", ["is_active"] = "true",
        }));
        var kept = await _db.Post(null, tenant: live);
        await _db.Expect(Immutable, $"DELETE FROM finance_gl_entries WHERE id = '{kept}'");

        var suspended = Guid.NewGuid();
        await _db.Exec(await _db.Insert("tenants", new()
        {
            ["id"] = LedgerDbFixture.Q(suspended), ["name"] = "'Suspended'", ["slug"] = $"'suspended-{suspended:N}'", ["is_active"] = "false",
        }));
        var held = await _db.Post(null, tenant: suspended);
        await _db.Expect(Immutable, $"DELETE FROM finance_gl_entries WHERE id = '{held}'", "an inactive tenant without the deleted marker is merely suspended");
    }

    [Fact]
    public async Task TheTriggers_FireEvenWhenReplicationRoleIsReplica()
    {
        var id = await _db.Post(_db.CompanyA);
        var unpooled = new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Pooling = false }.ConnectionString;
        await _db.Expect(Immutable, $"SET session_replication_role = replica; UPDATE finance_gl_entries SET amount = 1 WHERE id = '{id}'",
            "ENABLE ALWAYS: session_replication_role = replica is the standard way to skip triggers", unpooled);
        (await _db.Scalar<long>("SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relname = 'finance_gl_entries' AND NOT t.tgisinternal AND t.tgenabled <> 'A'"))
            .Should().Be(0, "every ledger trigger is ENABLE ALWAYS");
    }

    // ── is_reversed (S-022, AM-12) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsReversed_CannotBeSet_WithoutAContra()
    {
        var id = await _db.Post(_db.CompanyA);
        var ex = await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{id}'");
        ex.MessageText.Should().Contain("is_reversed");
        (await _db.Scalar<bool>($"SELECT is_reversed FROM finance_gl_entries WHERE id = '{id}'")).Should().BeFalse();
    }

    [Fact]
    public async Task IsReversed_CommitsWithItsContra_InEitherStatementOrder()
    {
        // EF may send the UPDATE of the original before or after the INSERT of its contra: the check cannot depend on it.
        var first = await _db.Post(_db.CompanyA);
        await _db.RunTx(
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: first, eventType: "PayrollVoid"),
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{first}'");
        (await _db.Scalar<bool>($"SELECT is_reversed FROM finance_gl_entries WHERE id = '{first}'")).Should().BeTrue();

        var second = await _db.Post(_db.CompanyA);
        await _db.RunTx(
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{second}'",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: second, eventType: "PayrollVoid"));
        (await _db.Scalar<bool>($"SELECT is_reversed FROM finance_gl_entries WHERE id = '{second}'")).Should().BeTrue();
    }

    [Fact]
    public async Task IsReversed_NeedsAContraThatCoversTheWholeOriginal()
    {
        var id = await _db.Post(_db.CompanyA, amount: 100m);
        await _db.ExpectTx(AtCommit, "a 40 contra does not make a 100 line reversed",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 40m, reversalOf: id, eventType: "BonusAccrualReversal"),
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{id}'");

        // The bonus cancel pattern: a partial contra alone is fine, and the original stays visible as not reversed.
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 40m, reversalOf: id, eventType: "BonusAccrualReversal"));
        (await _db.Scalar<bool>($"SELECT is_reversed FROM finance_gl_entries WHERE id = '{id}'")).Should().BeFalse();
    }

    [Fact]
    public async Task IsReversed_CannotBeUnset_AndCannotBeSetOnAContraRow()
    {
        var original = await _db.Post(_db.CompanyA);
        var contra = Guid.NewGuid();
        await _db.RunTx(
            _db.InsertEntry(contra, _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original, eventType: "PayrollVoid"),
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{original}'");

        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET is_reversed = false WHERE id = '{original}'", "true to false is never legal");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{contra}'", "a contra is not itself reversed (B3: reversing a reversal re-applies the original)");
    }

    [Fact]
    public async Task Void_FundsDisbursed_MayFlagTheRetainedCashLegWithItsReclassRow_AndOnlyThen()
    {
        // PayrollVoidService (c): when the money really left, the cash credit is flagged reversed with NO linked contra; the
        // 1420 receivable (an unlinked PayrollNetSettlementReclass row) is written in the same unit of work instead.
        var run = Guid.NewGuid();
        var cash = await _db.Post(_db.CompanyA, debit: "", credit: "1000 - Cash/Bank", amount: 500m, eventType: "PayrollNetSettlement", source: run);

        await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{cash}'", "no contra and no reclass row");
        await _db.ExpectTx(AtCommit, "a reclass row of ANOTHER run does not excuse it",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "1420 - Employee receivable", credit: "", amount: 500m, eventType: "PayrollNetSettlementReclass", source: Guid.NewGuid()),
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{cash}'");

        await _db.RunTx(
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{cash}'",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "1420 - Employee receivable", credit: "", amount: 500m, eventType: "PayrollNetSettlementReclass", source: run));
        (await _db.Scalar<bool>($"SELECT is_reversed FROM finance_gl_entries WHERE id = '{cash}'")).Should().BeTrue();

        var remitRun = Guid.NewGuid();
        var remit = await _db.Post(_db.CompanyA, debit: "", credit: "1000 - Cash/Bank", amount: 70m, eventType: "PayrollRemit:GOSI", source: remitRun);
        await _db.RunTx(
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{remit}'",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "1430 - Prepaid statutory", credit: "", amount: 70m, eventType: "PayrollRemitReclass:GOSI", source: remitRun));
        (await _db.Scalar<bool>($"SELECT is_reversed FROM finance_gl_entries WHERE id = '{remit}'")).Should().BeTrue();
    }

    // ── reversal rows are validated at insert (S-017, AM-11 DB half) ─────────────────────────────────────────────

    [Fact]
    public async Task Reversal_ToAnOriginalThatDoesNotExist_IsRefused()
    {
        var ex = await _db.ExpectTx(AtCommit, "(a) nonexistent original",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: Guid.NewGuid()));
        ex.MessageText.Should().Contain("reversal");
    }

    [Fact]
    public async Task Reversal_ToAnotherCompanysOrAnotherTenantsEntry_IsRefused()
    {
        var original = await _db.Post(_db.CompanyA);
        await _db.ExpectTx(AtCommit, "(b) another company's entry",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyB, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original));
        await _db.ExpectTx(AtCommit, "(b) an unattributed contra of an attributed entry",
            _db.InsertEntry(Guid.NewGuid(), null, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original));
        await _db.ExpectTx(AtCommit, "(b) another tenant's entry",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, tenant: Guid.NewGuid(), debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original));
    }

    [Fact]
    public async Task Reversal_OfAReversal_IsRefused()
    {
        var original = await _db.Post(_db.CompanyA);
        var contra = Guid.NewGuid();
        await _db.RunTx(_db.InsertEntry(contra, _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original));
        await _db.ExpectTx(AtCommit, "(c) reversing a reversal re-applies the original",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "5001 - Salary", credit: "2100 - Net pay", reversalOf: contra));
    }

    [Fact]
    public async Task Reversal_ThatIsNotAMirror_IsRefused()
    {
        var cc = await _db.Post(_db.CompanyA, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1");
        await _db.ExpectTx(AtCommit, "(d) greater amount",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 101m, reversalOf: cc, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1"));
        await _db.ExpectTx(AtCommit, "(d) same direction, not swapped",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "5001 - Salary", credit: "2100 - Net pay", reversalOf: cc, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1"));
        await _db.ExpectTx(AtCommit, "(d) different accounts",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "6000 - Other", reversalOf: cc, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1"));
        await _db.ExpectTx(AtCommit, "(d) cost centre dropped",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: cc));
        await _db.ExpectTx(AtCommit, "(d) a different cost centre",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: cc, cc: _db.CcA2, ccCode: "CC-A2", ccName: "CC A2", ccSegment: "S-1"));
        await _db.ExpectTx(AtCommit, "(d) the snapshot is copied verbatim, not re-read",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: cc, cc: _db.CcA1, ccCode: "CC-A1", ccName: "RENAMED", ccSegment: "S-1"));
        await _db.ExpectTx(AtCommit, "(d) different currency",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", currency: "USD", reversalOf: cc, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1"));
    }

    [Fact]
    public async Task Reversal_ThatMirrorsTheOriginal_IsAccepted_IncludingTheShapesTheVoidWrites()
    {
        var cc = await _db.Post(_db.CompanyA, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1");
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: cc, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1"));

        // A two-sided original undone on one leg only (PayrollVoidService (a), funds disbursed): credit leg only, full amount.
        var twoSided = await _db.Post(_db.CompanyA);
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "", credit: "5001 - Salary", reversalOf: twoSided));
        // One-sided originals (the payroll ledger's normal shape): a debit line is undone by a credit, a credit line by a debit.
        var debitLine = await _db.Post(_db.CompanyA, debit: "5001 - Salary", credit: "");
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "", credit: "5001 - Salary", reversalOf: debitLine));
        var creditLine = await _db.Post(_db.CompanyA, debit: "", credit: "2100 - Net pay");
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "", reversalOf: creditLine));
        // Unattributed legacy original, unattributed contra.
        var legacy = await _db.Post(null);
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), null, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: legacy));
    }

    [Fact]
    public async Task Reversal_PartialContras_AreAccepted_UntilTheyWouldExceedTheOriginal()
    {
        // BonusesController cancel: a partially consumed accrual is reversed for its REMAINDER only.
        var accrual = await _db.Post(_db.CompanyA, amount: 100m);
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 60m, reversalOf: accrual));
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 40m, reversalOf: accrual));
        var ex = await _db.ExpectTx(AtCommit, "a third contra reverses more than was ever posted (a void twice)",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 1m, reversalOf: accrual));
        ex.MessageText.Should().Contain("exceed");
    }

    [Fact]
    public async Task Reversal_TwoFullContrasInOneTransaction_AreRefused()
    {
        var original = await _db.Post(_db.CompanyA);
        await _db.ExpectTx(AtCommit, "a double void in one unit of work",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original),
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original));
    }

    // ── AM-25: reversal validity (supersedes "mirrored" in AM-11 and AM-12) ──────────────────────────────────────

    [Fact]
    public async Task Am25_APartialBonusReversal_IsAllowed_AndLeavesTheOriginalUnflagged()
    {
        var accrual = await _db.Post(_db.CompanyA, amount: 500m, eventType: "BonusAccrual", module: "Bonus");
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 200m, reversalOf: accrual,
            eventType: "BonusAccrualReversal", module: "Bonus"));
        (await _db.Scalar<bool>($"SELECT is_reversed FROM finance_gl_entries WHERE id = '{accrual}'")).Should().BeFalse("only a reversal that closes the accrual flags it (BonusesController)");
    }

    [Fact]
    public async Task Am25_PartialReversalsWhoseSumExceedsTheOriginal_AreRefused()
    {
        var accrual = await _db.Post(_db.CompanyA, amount: 500m, eventType: "BonusAccrual", module: "Bonus");
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 300m, reversalOf: accrual));
        await _db.ExpectTx(AtCommit, "300 + 201 > 500",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 201m, reversalOf: accrual));
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 200m, reversalOf: accrual));
    }

    [Fact]
    public async Task Am25_APeriodShiftedPayrollVoid_IsAllowed()
    {
        // PayrollVoidService with PriorPeriodAdjustment: the contra lands in an OPEN month, dated today, referencing the original period.
        var original = await _db.Post(_db.CompanyA, debit: "5001 - Salary", credit: "", amount: 5_000m, source: Guid.NewGuid());
        var contra = _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "", credit: "5001 - Salary", amount: 5_000m, reversalOf: original,
            eventType: "PayrollVoid", period: "2026-10", sourceRef: "2026-09", description: "VOID — reversal of \"Salary\" [prior-period adjustment for 2026-09]");
        await _db.RunTx(contra, $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{original}'");
        (await _db.Scalar<string>($"SELECT period FROM finance_gl_entries WHERE reversal_of_entry_id = '{original}'")).Should().Be("2026-10");
    }

    [Fact]
    public async Task Am25_AReversalWithADifferentCostCentre_IsRefused()
    {
        var original = await _db.Post(_db.CompanyA, cc: _db.CcA1, ccCode: "CC-A1", ccName: "CC A1", ccSegment: "S-1");
        await _db.ExpectTx(AtCommit, "a different cost centre",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original, cc: _db.CcA2, ccCode: "CC-A2", ccName: "CC A2", ccSegment: "S-1"));
        await _db.ExpectTx(AtCommit, "a reversal of a costed line that dropped the cost centre",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original));
    }

    [Fact]
    public async Task Am25_AReversalOfAReversal_IsRefused()
    {
        var original = await _db.Post(_db.CompanyA);
        var contra = Guid.NewGuid();
        await _db.RunTx(_db.InsertEntry(contra, _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original, amount: 60m));
        await _db.ExpectTx(AtCommit, "even a partial one, even though 60 < 100",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "5001 - Salary", credit: "2100 - Net pay", reversalOf: contra, amount: 10m));
    }

    [Fact]
    public async Task Am25_AnOriginalAlreadyReversedInAnEarlierTransaction_TakesNoFurtherReversal()
    {
        // The retained cash leg of a funds-disbursed void is flagged with no linked contra, so the amount cap alone (0 of 500) would
        // admit a "reversal" of it: the already-reversed rule is what refuses it.
        var run = Guid.NewGuid();
        var cash = await _db.Post(_db.CompanyA, debit: "", credit: "1000 - Cash/Bank", amount: 500m, eventType: "PayrollNetSettlement", source: run);
        await _db.RunTx(
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{cash}'",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "1420 - Employee receivable", credit: "", amount: 500m, eventType: "PayrollNetSettlementReclass", source: run));

        var ex = await _db.ExpectTx(AtCommit, "already reversed",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "1000 - Cash/Bank", credit: "", amount: 500m, reversalOf: cash, eventType: "PayrollNetSettlementReversal", source: run));
        ex.MessageText.Should().Contain("already reversed");
    }

    [Fact]
    public async Task Am25_AnOriginalFlaggedAndReversedInTheSameTransaction_IsNotAlreadyReversed()
    {
        // The writers flag the original and insert its contra in one unit of work, in either statement order: the flag must not make
        // the contra look like a reversal of something already reversed.
        var original = await _db.Post(_db.CompanyA);
        await _db.RunTx(
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{original}'",
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: original));
        var other = await _db.Post(_db.CompanyA);
        await _db.RunTx(
            _db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", reversalOf: other),
            $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{other}'");
    }

    [Fact]
    public async Task IsReversed_NeedsTheContraInTheSameTransaction_NotOneFromEarlier()
    {
        // AM-12: "a matching contra exists in the same transaction". A contra committed earlier (a partial bonus reversal, say) does not
        // license a later bare flag.
        var original = await _db.Post(_db.CompanyA, amount: 100m);
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "2100 - Net pay", credit: "5001 - Salary", amount: 100m, reversalOf: original));
        var ex = await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{original}'", "the contra was committed by someone else, earlier");
        ex.MessageText.Should().Contain("same transaction");
    }

    [Fact]
    public async Task IsReversed_TheReclassRowMustAlsoBeFromTheSameTransaction()
    {
        var run = Guid.NewGuid();
        var cash = await _db.Post(_db.CompanyA, debit: "", credit: "1000 - Cash/Bank", amount: 500m, eventType: "PayrollNetSettlement", source: run);
        await _db.RunTx(_db.InsertEntry(Guid.NewGuid(), _db.CompanyA, debit: "1420 - Employee receivable", credit: "", amount: 500m, eventType: "PayrollNetSettlementReclass", source: run));
        await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{cash}'", "a reclass row from an earlier void cannot excuse a later flag");
    }

    // ── erp_* state machine (S-023, AM-12) ───────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> LegalErpEdges() => new (string From, string To)[]
    {
        ("NotReady", "ReadyForErp"), ("NotReady", "Rejected"), ("NotReady", "Exported"), ("NotReady", "Posted"),
        ("ReadyForErp", "Exported"), ("ReadyForErp", "Rejected"), ("ReadyForErp", "Posted"),
        ("Exported", "Exported"), ("Exported", "Rejected"), ("Exported", "Posted"),
        ("Rejected", "Rejected"), ("Rejected", "Exported"), ("Rejected", "Posted"),
        ("Posted", "Posted"),
    }.Select(e => new object[] { e.From, e.To });

    public static IEnumerable<object[]> IllegalErpEdges() => new (string From, string To)[]
    {
        ("ReadyForErp", "NotReady"),
        ("Exported", "NotReady"), ("Exported", "ReadyForErp"),
        ("Rejected", "NotReady"), ("Rejected", "ReadyForErp"),
        ("Posted", "NotReady"), ("Posted", "ReadyForErp"), ("Posted", "Exported"), ("Posted", "Rejected"),
        ("NotReady", "Bogus"), ("Exported", "Bogus"),
    }.Select(e => new object[] { e.From, e.To });

    private async Task<Guid> ErpRow(string status)
    {
        var id = await _db.Post(_db.CompanyA, status: status, doc: status == "Posted" ? "DOC-0" : null);
        if (status is "Exported" or "Rejected" or "Posted") await _db.AddExportEvidence(id, "DOC-0");
        return id;
    }

    [Theory]
    [MemberData(nameof(LegalErpEdges))]
    public async Task ErpStatus_LegalEdges_Pass_WhenTheEvidenceTheyAssertIsThere(string from, string to)
    {
        var id = await ErpRow(from);
        var doc = to == "Posted" ? (from == "Posted" ? "DOC-0" : "DOC-1") : null;
        if (to == "Posted" && from != "Posted") await _db.AddExportEvidence(id, "DOC-1");
        if (to == "Exported") await _db.AddExportLineOnly(id);
        var set = $"erp_posting_status = '{to}', erp_status_changed_at_utc = now()" + (doc is null ? "" : $", erp_document_number = '{doc}'")
                + (to == "Rejected" ? ", erp_rejection_reason = 'ERP refused'" : "");
        (await _db.Exec($"UPDATE finance_gl_entries SET {set} WHERE id = '{id}'")).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(IllegalErpEdges))]
    public async Task ErpStatus_IllegalEdges_AreRefused_WhateverTheEvidence(string from, string to)
    {
        var id = await ErpRow(from);
        await _db.AddExportEvidence(id, "DOC-0");
        var ex = await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET erp_posting_status = '{to}' WHERE id = '{id}'", $"{from} -> {to}");
        ex.MessageText.Should().Contain("erp_posting_status");
    }

    [Fact]
    public async Task ErpPosted_WithoutAnyConfirmedExport_IsRefusedAtCommit()
    {
        var id = await _db.Post(_db.CompanyA, status: "Exported");
        await _db.AddExportLineOnly(id);
        var ex = await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET erp_posting_status = 'Posted', erp_document_number = 'DOC-FREE' WHERE id = '{id}'", "no confirmed export carries DOC-FREE");
        ex.MessageText.Should().Contain("erp");
    }

    [Fact]
    public async Task ErpPosted_NeedsADocumentNumber()
    {
        var id = await _db.Post(_db.CompanyA, status: "Exported");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET erp_posting_status = 'Posted' WHERE id = '{id}'");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET erp_posting_status = 'Posted', erp_document_number = '' WHERE id = '{id}'");
    }

    [Fact]
    public async Task ErpPosted_IsBackedByAFullConfirmationOrAPerJournalConfirmation_InEitherStatementOrder()
    {
        // The export, its lines and the stamp are written by one SaveChanges, in no fixed order: evidence is judged at COMMIT.
        var stampFirst = await _db.Post(_db.CompanyA, status: "Exported");
        var export1 = Guid.NewGuid();
        await _db.RunTx(
            $"UPDATE finance_gl_entries SET erp_posting_status = 'Posted', erp_document_number = 'DOC-F' WHERE id = '{stampFirst}'",
            await _db.ExportSql(export1, "DOC-F"),
            await _db.ExportLineSql(export1, stampFirst));
        (await _db.Scalar<string>($"SELECT erp_posting_status FROM finance_gl_entries WHERE id = '{stampFirst}'")).Should().Be("Posted");

        var stampLast = await _db.Post(_db.CompanyA, status: "Exported");
        var export2 = Guid.NewGuid();
        await _db.RunTx(
            await _db.ExportLineSql(export2, stampLast),
            await _db.ExportSql(export2, "DOC-O"),
            $"UPDATE finance_gl_entries SET erp_posting_status = 'Posted', erp_document_number = 'DOC-O' WHERE id = '{stampLast}'");
        (await _db.Scalar<string>($"SELECT erp_posting_status FROM finance_gl_entries WHERE id = '{stampLast}'")).Should().Be("Posted");

        var perJournal = await _db.Post(_db.CompanyA, status: "Exported");
        await _db.AddExportEvidence(perJournal, "n/a", status: "Exported",
            confirmationsJson: """[{"DocumentRef":"J1","CompanyCode":"C","JournalRefs":["J1"],"Status":"Confirmed","ErpDocumentNumber":"DOC-J","DecidedAtUtc":"2026-10-01T00:00:00Z"}]""");
        (await _db.Exec($"UPDATE finance_gl_entries SET erp_posting_status = 'Posted', erp_document_number = 'DOC-J' WHERE id = '{perJournal}'")).Should().Be(1);

        var rejected = await _db.Post(_db.CompanyA, status: "Exported");
        await _db.AddExportEvidence(rejected, "n/a", status: "Exported",
            confirmationsJson: """[{"DocumentRef":"J1","CompanyCode":"C","JournalRefs":["J1"],"Status":"Rejected","Reason":"no","DecidedAtUtc":"2026-10-01T00:00:00Z"}]""");
        await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET erp_posting_status = 'Posted', erp_document_number = 'DOC-J' WHERE id = '{rejected}'", "a rejected journal is not evidence of posting");
    }

    [Fact]
    public async Task ErpPosted_EvidenceFromAnotherTenant_DoesNotCount()
    {
        var id = await _db.Post(_db.CompanyA, status: "Exported");
        await _db.AddExportEvidence(id, "DOC-T", tenant: Guid.NewGuid());
        await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET erp_posting_status = 'Posted', erp_document_number = 'DOC-T' WHERE id = '{id}'");
    }

    [Fact]
    public async Task ErpExported_NeedsTheFileItClaimsExists()
    {
        var id = await _db.Post(_db.CompanyA);
        await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET erp_posting_status = 'Exported' WHERE id = '{id}'", "no export line carries this entry");
        await _db.AddExportLineOnly(id);
        (await _db.Exec($"UPDATE finance_gl_entries SET erp_posting_status = 'Exported', erp_status_changed_at_utc = now() WHERE id = '{id}'")).Should().Be(1);
    }

    [Fact]
    public async Task ErpPosted_DocumentNumber_IsWriteOnce_ExceptThroughAReplacementExport()
    {
        var id = await _db.Post(_db.CompanyA, status: "Posted", doc: "DOC-1");
        await _db.AddExportEvidence(id, "DOC-1");

        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET erp_document_number = NULL WHERE id = '{id}'", "clearing the number");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET erp_document_number = '' WHERE id = '{id}'");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET erp_rejection_reason = 'late' WHERE id = '{id}'", "a posted line carries no rejection");
        await _db.Exec($"UPDATE finance_gl_entries SET erp_status_changed_at_utc = now() WHERE id = '{id}'");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET erp_status_changed_at_utc = NULL WHERE id = '{id}'");

        // Editing it to a number that only an ordinary (Full) export vouches for: the first ERP document must not be overwritten.
        await _db.AddExportEvidence(id, "DOC-2");
        await _db.Expect(AtCommit, $"UPDATE finance_gl_entries SET erp_document_number = 'DOC-2', erp_status_changed_at_utc = now() WHERE id = '{id}'");

        // Re-stamping the same number is a no-op the confirm flow performs on a duplicate confirmation.
        (await _db.Exec($"UPDATE finance_gl_entries SET erp_document_number = 'DOC-1', erp_status_changed_at_utc = now() WHERE id = '{id}'")).Should().Be(1);

        // The documented path: a Replacement export of the same rows, confirmed with the ERP's new number.
        await _db.AddExportEvidence(id, "DOC-3", kind: "Replacement");
        (await _db.Exec($"UPDATE finance_gl_entries SET erp_document_number = 'DOC-3', erp_status_changed_at_utc = now() WHERE id = '{id}'")).Should().Be(1);
        (await _db.Scalar<string>($"SELECT erp_document_number FROM finance_gl_entries WHERE id = '{id}'")).Should().Be("DOC-3");
    }

    [Fact]
    public async Task ErpStatusChangedAt_CannotBeErased_OnceSet()
    {
        var id = await _db.Post(_db.CompanyA);
        await _db.Exec($"UPDATE finance_gl_entries SET erp_posting_status = 'ReadyForErp', erp_status_changed_at_utc = now() WHERE id = '{id}'");
        await _db.Expect(Immutable, $"UPDATE finance_gl_entries SET erp_status_changed_at_utc = NULL WHERE id = '{id}'");
    }

    [Fact]
    public async Task ErpRejection_CarriesItsReason_AndIsClearedWhenTheLineMovesOn()
    {
        var id = await _db.Post(_db.CompanyA, status: "Exported");
        await _db.AddExportLineOnly(id);
        await _db.Exec($"UPDATE finance_gl_entries SET erp_posting_status = 'Rejected', erp_rejection_reason = 'unbalanced', erp_status_changed_at_utc = now() WHERE id = '{id}'");
        // ErpPostingEvidence.StampAsync: a re-export clears the reason.
        (await _db.Exec($"UPDATE finance_gl_entries SET erp_posting_status = 'Exported', erp_rejection_reason = NULL, erp_status_changed_at_utc = now() WHERE id = '{id}'")).Should().Be(1);
    }
}
