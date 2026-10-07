using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class LoanMigrationHistoryPostgresTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Paid")]
    [InlineData("Reversed")]
    public async Task PaymentEvidenceCheck_RejectsNullPaymentMethod(string status)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(Migration).GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new AddLoanJournalEvidenceAndJawazatPolicy(), new object[] { builder });
        var check = Assert.Single(builder.Operations.OfType<AddCheckConstraintOperation>(), x => x.Name == "ck_loan_payment_line_evidence");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // The CHECK text comes from the migration under test, never caller-supplied SQL.
        await using (var create = new NpgsqlCommand($"CREATE TEMP TABLE payment_evidence_probe (status text, paid_date date, paid_by uuid, payment_reference text, payment_method text, CHECK ({check.Sql})) ON COMMIT DROP", connection, transaction))
            await create.ExecuteNonQueryAsync();
        await using var insert = new NpgsqlCommand("INSERT INTO payment_evidence_probe VALUES (@status, CURRENT_DATE, gen_random_uuid(), 'BANK-EVIDENCE', NULL)", connection, transaction);
        insert.Parameters.AddWithValue("status", status);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(typeof(AddStandaloneLoanPayments), "loan_disbursement_lines")]
    [InlineData(typeof(AddStandaloneLoanPayments), "loan_repayments")]
    [InlineData(typeof(AddStandaloneLoanPayments), "loan_disbursement_batches")]
    [InlineData(typeof(AddLoanPolicyLifecycleControls), "loan_disbursement_lines")]
    [InlineData(typeof(AddLoanPolicyLifecycleControls), "loan_repayments")]
    [InlineData(typeof(AddLoanPolicyLifecycleControls), "loan_disbursement_batches")]
    public async Task DownGuard_ActuallyRefusesPaidHistory_AndLeavesRowsIntact(Type migrationType, string table)
    {
        Assert.Contains(table, new[] { "loan_disbursement_lines", "loan_repayments", "loan_disbursement_batches" });
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // Session-local tables shadow public ones; no shared fixture rows are changed.
        await using (var setup = new NpgsqlCommand("""
            CREATE TEMP TABLE loan_disbursement_lines (status text) ON COMMIT DROP;
            CREATE TEMP TABLE loan_repayments (status text) ON COMMIT DROP;
            CREATE TEMP TABLE loan_disbursement_batches (status text) ON COMMIT DROP;
            CREATE TEMP TABLE loan_change_requests (status text) ON COMMIT DROP;
            """, connection, transaction)) await setup.ExecuteNonQueryAsync();
        await using (var insert = new NpgsqlCommand($"INSERT INTO {table} VALUES ('Paid')", connection, transaction))
            await insert.ExecuteNonQueryAsync();
        await transaction.SaveAsync("before_guard");
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(Migration).GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Activator.CreateInstance(migrationType), new object[] { builder });
        var guard = Assert.IsType<SqlOperation>(builder.Operations[0]).Sql;
        await using (var execute = new NpgsqlCommand(guard, connection, transaction))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => execute.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
            Assert.Contains("Preserve financial", error.MessageText);
        }
        await transaction.RollbackAsync("before_guard");
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection, transaction);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }
}
