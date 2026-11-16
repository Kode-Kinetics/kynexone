using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// /validate rebuilds a run's stored findings, and Approve counts the pay warnings among them. On real Postgres:
/// /validate takes the same run row lock (FOR UPDATE) as every status transition, so it can never rebuild a run that
/// a concurrent Approve is moving past Processed; past Processed it rewrites nothing; and the delete of the old
/// findings and the insert of the new ones commit together.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayPopulationValidatePostgresTests
{
    private readonly PostgresFixture _fx;
    public PayPopulationValidatePostgresTests(PostgresFixture fx) => _fx = fx;

    private async Task<GlPostingKit> NewKitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        return await GlPostingKit.CreateAsync(_fx.CreateDb, tenantId);
    }

    private static async Task<PayrollRun> ProcessedRunAsync(GlPostingKit k, int month)
    {
        await using var db = k.NewContext();
        var run = new PayrollRun
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, Year = 2026, Month = month, Status = "Processed",
            RunType = PayrollRunTypes.Regular, IncludesRecurringPay = true, ProcessVersion = 1,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private static async Task<IActionResult> ValidateAsync(GlPostingKit k, Guid runId)
    {
        await using var db = k.NewContext();
        return await k.Payroll(db, Guid.NewGuid(), "Admin", "payroll.read", "payroll.write").Validate(runId, default);
    }

    [Fact]
    public async Task Validate_QueuesBehindTheRunRowLock_ThenRewritesNothingOnceTheRunIsPastProcessed()
    {
        await using var k = await NewKitAsync();
        var run = await ProcessedRunAsync(k, 5);
        Guid warningId;
        await using (var seed = k.NewContext())
        {
            var w = new PayrollValidationResult
            {
                TenantId = k.TenantId, PayrollRunId = run.Id, EmployeeId = 1, Severity = "Warning",
                Code = PayPopulationReasons.WarnNoLastWorkingDay, Message = "acknowledged at level 1",
            };
            seed.PayrollValidationResults.Add(w);
            await seed.SaveChangesAsync();
            warningId = w.Id;
        }

        Task<IActionResult> validate = null!;
        await using (var holder = k.NewContext())
        {
            await holder.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await holder.Database.BeginTransactionAsync();
                var locked = await holder.PayrollRuns.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
                    .SingleAsync(r => r.Id == run.Id);
                validate = Task.Run(() => ValidateAsync(k, run.Id));

                var finishedEarly = await Task.WhenAny(validate, Task.Delay(TimeSpan.FromSeconds(3))) == validate;
                finishedEarly.Should().BeFalse("/validate must queue behind the run row lock an approval holds");

                locked.Status = "PendingFinanceReview";   // level 1 approval commits while /validate waits
                await holder.SaveChangesAsync();
                await tx.CommitAsync();
            });
        }

        (await validate).Should().BeOfType<OkObjectResult>();
        await using var db = k.NewContext();
        (await db.PayrollValidationResults.AsNoTracking().Where(x => x.PayrollRunId == run.Id).Select(x => x.Id).ToListAsync())
            .Should().Equal(new[] { warningId }, "the run is past Processed when /validate gets the lock, so it reads and rewrites nothing");
    }

    [Fact]
    public async Task Validate_OnAProcessedRun_ReplacesTheFindings_AndBlocksASlipWhoseEmployeeLostTheirStanding()
    {
        await using var k = await NewKitAsync();
        var run = await ProcessedRunAsync(k, 6);
        int employeeId;
        await using (var seed = k.NewContext())
        {
            var e = new Employee
            {
                TenantId = k.TenantId, CompanyId = k.CompanyId, EmployeeCode = $"PV{Guid.NewGuid():N}"[..10], FullName = "Suspended after process",
                Status = "Suspended", JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                WorkEmail = $"pv{Guid.NewGuid():N}@pv.test", Nationality = "Saudi", ContractType = "Indefinite",
            };
            seed.Employees.Add(e);
            await seed.SaveChangesAsync();
            employeeId = e.Id;
            seed.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = k.TenantId, CompanyId = k.CompanyId, RunId = run.Id, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode,
                Status = "Processed", BasicSalary = 6_000m, GrossSalary = 6_000m, NetSalary = 6_000m,
                PaidFromDate = new DateOnly(2026, 6, 1), PaidToDate = new DateOnly(2026, 6, 30),
            });
            seed.PayrollValidationResults.Add(new PayrollValidationResult
            {
                TenantId = k.TenantId, PayrollRunId = run.Id, Severity = "Info", Code = "STALE_FROM_PROCESS", Message = "old",
            });
            await seed.SaveChangesAsync();
        }

        (await ValidateAsync(k, run.Id)).Should().BeOfType<OkObjectResult>();

        await using var db = k.NewContext();
        var findings = await db.PayrollValidationResults.AsNoTracking().Where(x => x.PayrollRunId == run.Id).ToListAsync();
        findings.Should().NotContain(f => f.Code == "STALE_FROM_PROCESS", "the old findings are replaced");
        findings.Should().ContainSingle(f => f.Code == PayPopulationReasons.StandingChangedReprocess && f.EmployeeId == employeeId)
            .Which.Severity.Should().Be("Error");
    }
}
