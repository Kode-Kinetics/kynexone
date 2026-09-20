using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// W2-A — shared seed and plumbing for the payroll Lock / Process / WPS job tests. Everything runs on the
/// Testcontainers <see cref="PostgresFixture"/> with production's provider configuration (retrying
/// execution strategy), because every property these tests prove — one transaction, a row lock, a
/// partial unique index — only exists on real PostgreSQL.
/// </summary>
internal static class PayrollJobTestKit
{
    internal sealed record Scenario(
        Guid TenantId, Guid CompanyId, Guid RunId, IReadOnlyList<int> EmployeeIds,
        IReadOnlyList<Guid> LoanIds, IReadOnlyList<Guid> AdvanceIds);

    internal sealed record SeedOptions(
        int Employees = 3,
        bool WithDebt = false,
        int Year = 2026,
        int Month = 6,
        bool AboveCeiling = true);

    /// <summary>
    /// A KSA legal entity with <paramref name="o"/>.Employees employees on one Draft Regular run. Employee 0
    /// is Saudi and (by default) paid ABOVE the 45,000 GOSI ceiling; odd employees are expats. With
    /// <c>WithDebt</c> every employee carries a loan (500 × 3, schedule rows) and an advance (250 × 2).
    /// </summary>
    internal static async Task<Scenario> SeedKsaRunAsync(PostgresFixture fx, SeedOptions? o = null)
    {
        o ??= new SeedOptions();
        await using var db = fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = $"W2A Co {Guid.NewGuid():N}",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"W2A-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        var emps = new List<Employee>();
        for (var i = 0; i < o.Employees; i++)
        {
            var saudi = i % 2 == 0;
            emps.Add(new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"W2A-{i + 1:D3}", FullName = $"W2A Employee {i + 1}",
                Nationality = saudi ? "Saudi" : "Indian", ContractType = "Indefinite", Status = "Active",
                JoiningDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                WorkEmail = $"w2a-{i}-{Guid.NewGuid():N}@example.test",
            });
        }
        db.Employees.AddRange(emps);
        await db.SaveChangesAsync();

        var loanIds = new List<Guid>();
        var advanceIds = new List<Guid>();
        for (var i = 0; i < emps.Count; i++)
        {
            var e = emps[i];
            var (basic, housing) = i == 0 && o.AboveCeiling ? (42_000m, 10_000m) : (8_000m + 1_000m * i, 2_000m + 250m * i);
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = basic, HousingAllowance = housing, TransportAllowance = 500m,
                EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = e.Id, Iban = "SA4420000001234567891234",
                MolId = $"MOL-{Guid.NewGuid():N}"[..20], SalaryCurrency = "SAR", BankRoutingCode = "RJHISARI",
            });
            if (!o.WithDebt) continue;
            var loan = new EmployeeLoan
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = e.Id,
                EmployeeName = e.FullName, LoanTypeName = "Personal", LoanNumber = $"LN-{Guid.NewGuid():N}"[..14],
                RequestedAmount = 1_500m, ApprovedAmount = 1_500m, RequestedInstallments = 3, ApprovedInstallments = 3,
                InstallmentAmount = 500m, RepaymentStartDate = new DateOnly(o.Year, o.Month, 1),
                OutstandingBalance = 1_500m, Status = "Active",
            };
            db.EmployeeLoans.Add(loan);
            loanIds.Add(loan.Id);
            for (var n = 0; n < 3; n++)
                db.LoanInstallments.Add(new LoanInstallment
                {
                    TenantId = tenantId, LoanId = loan.Id, InstallmentNumber = n + 1,
                    DueDate = new DateOnly(o.Year, o.Month, 1).AddMonths(n), AmountDue = 500m, Status = "Pending",
                });
            var adv = new SalaryAdvance
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = e.Id,
                EmployeeName = e.FullName, AdvanceNumber = $"ADV-{Guid.NewGuid():N}"[..14], RequestedAmount = 500m,
                ApprovedAmount = 500m, RepaymentType = "Installments", Installments = 2, InstallmentAmount = 250m,
                RepaymentStartDate = new DateOnly(o.Year, o.Month, 1), OutstandingBalance = 500m, Status = "Active",
            };
            db.SalaryAdvances.Add(adv);
            advanceIds.Add(adv.Id);
            for (var n = 0; n < 2; n++)
                db.AdvanceInstallments.Add(new AdvanceInstallment
                {
                    TenantId = tenantId, AdvanceId = adv.Id, InstallmentNumber = n + 1,
                    DueDate = new DateOnly(o.Year, o.Month, 1).AddMonths(n), AmountDue = 250m, Status = "Pending",
                });
        }
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = o.Year, Month = o.Month, Status = "Draft",
            CreatedAtUtc = new DateTime(o.Year, o.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return new Scenario(tenantId, company.Id, run.Id, emps.Select(e => e.Id).ToList(), loanIds, advanceIds);
    }

    internal static PayrollController Build(ZayraDbContext db, Guid tenantId, params string[] permissions) =>
        PayComponentNetPayDefectTests.Build(db, tenantId, permissions);

    internal static async Task ProcessSyncAsync(PostgresFixture fx, Scenario s)
    {
        await using var db = fx.CreateDb();
        var result = await Build(db, s.TenantId, "payroll.write").Process(s.RunId, CancellationToken.None);
        if (result is ObjectResult { StatusCode: >= 400 } bad)
            Assert.Fail($"Process refused: HTTP {bad.StatusCode} {System.Text.Json.JsonSerializer.Serialize(bad.Value)}");
    }

    /// <summary>Asserts the processed run raised no blocking Error, then moves it to Approved.</summary>
    internal static async Task ApproveDirectAsync(PostgresFixture fx, Scenario s)
    {
        await using var db = fx.CreateDb();
        var errors = await db.PayrollValidationResults.AsNoTracking()
            .Where(v => v.PayrollRunId == s.RunId && v.Severity == "Error").Select(v => v.Code + ": " + v.Message).ToListAsync();
        Assert.Empty(errors);
        await db.PayrollRuns.Where(r => r.Id == s.RunId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, "Approved"));
    }

    internal static async Task GeneratePayslipsAsync(PostgresFixture fx, Scenario s)
    {
        await using var db = fx.CreateDb();
        var result = await Build(db, s.TenantId, "payroll.write").GeneratePayslips(s.RunId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    /// <summary>A fixture-equivalent context (production provider options) with extra interceptors.</summary>
    internal static ZayraDbContext CreateDbWith(PostgresFixture fx, params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(fx.ConnectionString, o => o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .AddInterceptors(interceptors)
            .Options);

    /// <summary>The run's accrual journal lines that are still live.</summary>
    internal static Task<List<FinanceGlEntry>> LiveAccrualAsync(ZayraDbContext db, Scenario s) =>
        db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == s.TenantId && g.SourceModule == "Payroll" && g.SourceEntityId == s.RunId
                     && g.EventType == GlEventTypes.Accrual && !g.IsReversed)
            .ToListAsync();
}

/// <summary>
/// Holds every SaveChanges that is about to INSERT payroll GL lines until <c>participants</c> contexts
/// have arrived (or the timeout elapses). This is what makes a double-submitted Lock deterministic: both
/// requests are parked at the exact point where each has already passed every check and is about to
/// write its journal.
/// </summary>
internal sealed class GlInsertBarrier(int participants, TimeSpan timeout) : SaveChangesInterceptor
{
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;
    public int Arrived => Volatile.Read(ref _arrived);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null
            && eventData.Context.ChangeTracker.Entries<FinanceGlEntry>().Any(e => e.State == EntityState.Added))
        {
            if (Interlocked.Increment(ref _arrived) >= participants) _all.TrySetResult();
            await Task.WhenAny(_all.Task, Task.Delay(timeout, cancellationToken));
        }
        return result;
    }
}

/// <summary>Fails the SaveChanges that would INSERT the payroll journal — a crash "between commits".</summary>
internal sealed class FailGlInsert : SaveChangesInterceptor
{
    public int Fired;
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null
            && eventData.Context.ChangeTracker.Entries<FinanceGlEntry>().Any(e => e.State == EntityState.Added))
        {
            Interlocked.Increment(ref Fired);
            throw new InvalidOperationException("W2A injected fault: the journal write failed.");
        }
        return ValueTask.FromResult(result);
    }
}
