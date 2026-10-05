using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
public sealed class LoanUpgradeRehearsalPostgresTests
{
    [Fact]
    public async Task Upgrade_FromPreStandalone_PreservesLegacyDebtAndPaidEvidence_AndRefusesDestructiveRollback()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync("20260929065334_AddGosiEntrantCohortFacts");
        // Literal synthetic identifiers only. Deliberately seed the OLD shape, not today's EF model.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO loan_types (id,tenant_id,code,name_en,name_ar,max_amount,max_installments,repayment_frequency,is_interest_free,interest_rate,min_service_months,requires_approval,is_active,is_deleted,created_at_utc)
            VALUES ('30000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000001','MIGRATION','Migration fixture','',5000,12,'Monthly',true,0,6,true,true,false,CURRENT_TIMESTAMP);
            INSERT INTO loan_policies (id,tenant_id,loan_type_id,policy_name,max_concurrent_loans,max_multiplier_of_salary,cooldown_months_after_repayment,allow_early_settlement,allow_rescheduling,is_active,created_at_utc)
            VALUES ('40000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000001','Legacy policy',2,3,2,true,true,true,CURRENT_TIMESTAMP);
            INSERT INTO employee_loans (id,tenant_id,company_id,employee_id,employee_name,loan_type_id,loan_type_name,loan_number,requested_amount,approved_amount,requested_installments,approved_installments,installment_amount,repayment_frequency,total_repaid,outstanding_balance,status,notes,is_locked_by_payroll,is_deleted,created_at_utc,disbursement_date)
            SELECT ('00000000-0000-0000-0000-00000000000' || n)::uuid,'10000000-0000-0000-0000-000000000001','50000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','Migration fixture','30000000-0000-0000-0000-000000000001','Fixture','LEGACY-' || n,100,CASE WHEN n=1 THEN 0 ELSE 100 END,3,3,33.33,'Monthly',CASE WHEN n=3 THEN 33.33 ELSE 0 END,CASE WHEN n=3 THEN 66.67 ELSE 0 END,CASE WHEN n=1 THEN 'Pending' WHEN n=2 THEN 'Approved' ELSE 'Active' END,'',false,false,CURRENT_TIMESTAMP,CASE WHEN n=3 THEN CURRENT_DATE ELSE NULL END FROM generate_series(1,3) AS n;
            INSERT INTO loan_approvals (id,tenant_id,loan_id,step_order,approver_role,approved_by_name,status,comments,created_at_utc)
            SELECT gen_random_uuid(),tenant_id,id,1,'Finance','',CASE WHEN status='Pending' THEN 'Pending' ELSE 'Approved' END,'',CURRENT_TIMESTAMP FROM employee_loans;
            """);
        await migrator.MigrateAsync("20261004142030_AddLoanPaymentReferenceUniqueness");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO loan_disbursement_batches (id,tenant_id,company_id,batch_number,currency,total_amount,status,created_at_utc,paid_by,payment_reference,paid_date,payment_method)
            VALUES ('60000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000001','50000000-0000-0000-0000-000000000001','OLD-PAID','SAR',100,'Paid',CURRENT_TIMESTAMP,'70000000-0000-0000-0000-000000000001','ORIGINAL-BANK-REF',CURRENT_DATE,'BankTransfer');
            INSERT INTO loan_disbursement_lines (id,tenant_id,batch_id,loan_id,amount,is_cancelled,employee_name,employee_code,iban,bank_name)
            VALUES (gen_random_uuid(),'10000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000003',100,false,'Migration fixture','E1','SA0380000000608010167519','Test Bank');
            """);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // Retry is a no-op, not a second backfill.
        var active = await db.EmployeeLoans.SingleAsync(l => l.LoanNumber == "LEGACY-3");
        Assert.Equal(100m, active.ApprovedAmount);
        Assert.Equal(33.33m, active.TotalRepaid);
        Assert.Equal(66.67m, active.OutstandingBalance);
        Assert.Equal("PayrollDeduction", active.RepaymentMethod);
        Assert.Null(active.PolicyId); // No inferred agreement or historical reclassification.
        var line = await db.LoanDisbursementLines.SingleAsync();
        Assert.Equal("Paid", line.Status);
        Assert.Equal("ORIGINAL-BANK-REF", line.PaymentReference);
        Assert.Equal("BankTransfer", line.PaymentMethod);
        Assert.NotNull(line.PaidBy);
        Assert.Null(line.GlEntryId); // Ambiguous historical journals are never fabricated.
        Assert.Equal(3, await db.EmployeeLoans.CountAsync());
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync("20260929065334_AddGosiEntrantCohortFacts"));
        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Equal(1, await db.LoanDisbursementLines.CountAsync());
        Assert.Equal(66.67m, await db.EmployeeLoans.Where(l => l.LoanNumber == "LEGACY-3").Select(l => l.OutstandingBalance).SingleAsync());
    }
}
