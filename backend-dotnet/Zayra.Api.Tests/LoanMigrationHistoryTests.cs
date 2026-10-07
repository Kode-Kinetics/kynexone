using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests;

public class LoanMigrationHistoryTests
{
    [Fact]
    public void EvidenceMigration_AddsNoTables_AndGuardsEvidenceBeforeDowngrade()
    {
        var migration = new AddLoanJournalEvidenceAndJawazatPolicy();
        var up = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(Migration).GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(migration, new object[] { up });
        Assert.Empty(up.Operations.OfType<CreateTableOperation>());
        var down = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(Migration).GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(migration, new object[] { down });
        var guard = Assert.IsType<SqlOperation>(down.Operations[0]).Sql;
        Assert.Contains("RAISE EXCEPTION", guard);
        Assert.Contains("jawazat_data_json IS NOT NULL", guard);
        Assert.Contains("gl_entry_id IS NOT NULL", guard);
        Assert.Contains("requested_repayment_method IS NOT NULL", guard);
    }

    [Theory]
    [InlineData(typeof(AddStandaloneLoanPayments))]
    [InlineData(typeof(AddLoanPolicyLifecycleControls))]
    public void Downgrade_RefusesToDiscardAnyPaymentHistory(Type migrationType)
    {
        var migration = (Migration)Activator.CreateInstance(migrationType)!;
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(Migration).GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, new object[] { builder });
        var guard = Assert.IsType<SqlOperation>(builder.Operations[0]).Sql;
        Assert.Contains("RAISE EXCEPTION", guard);
        Assert.Contains("EXISTS (SELECT 1 FROM loan_disbursement_lines)", guard);
        Assert.Contains("EXISTS (SELECT 1 FROM loan_repayments)", guard);
        Assert.Contains("EXISTS (SELECT 1 FROM loan_disbursement_batches)", guard);
    }
}
