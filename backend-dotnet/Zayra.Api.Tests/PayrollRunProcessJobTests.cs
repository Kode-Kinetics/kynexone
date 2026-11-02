using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Payroll run processing as a background job (financial module: tests first).
///
/// <para>CHARACTERISATION. The computation moved out of <c>PayrollController.Process</c> into
/// <see cref="PayrollRunProcessor"/>. The controller keeps an in-process <c>Process</c> that calls it, and the
/// ~50 existing suites that drive that method (PayrollRunTypeTests, PayrollProrationArrearsTests, …) are the
/// characterisation of the old behaviour: they pass unchanged. This class proves the job's path — a system-scope
/// pass with the host's hooks, for an explicit tenant and acting user — yields the IDENTICAL result, and pins the
/// all-or-nothing behaviour the job relies on. No Docker: SQLite.</para>
/// </summary>
public sealed class PayrollRunProcessJobTests
{
    private static readonly Guid Actor = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001");

    // ── Harness ─────────────────────────────────────────────────────────────────────────────

    private sealed class World : IDisposable
    {
        public required SqliteConnection Conn { get; init; }
        public required ZayraDbContext Db { get; init; }
        public required Guid Tenant { get; init; }
        public required Guid CompanyId { get; init; }
        public required Guid RunId { get; init; }
        public required Guid LoanId { get; init; }
        public Guid OtherCompanyId { get; init; }
        public Guid OtherRunId { get; init; }
        public required List<Employee> Employees { get; init; }
        public ZayraDbContext Fresh() =>
            new(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(Conn).Options);
        public void Dispose() { Db.Dispose(); Conn.Dispose(); }
    }

    private static StubRuleReader KsaRules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m).Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);

    /// <summary>A KSA company, <paramref name="employeeCount"/> salaried employees (E001 earns 10,000, E002 12,000, …),
    /// one active loan on E001 and a Draft June-2026 run.</summary>
    private static async Task<World> SeedAsync(int employeeCount = 2, string? countryCode = "SAU", int unassignedEmployees = 0, bool emptySecondCompany = false)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Job tenant", Slug = $"job-{tenant:N}", IsActive = true });
        var company = new Company
        {
            TenantId = tenant, LegalNameEn = "Job KSA Co", CountryCode = countryCode!, Jurisdiction = "KSA-mainland",
            IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenant, CompanyId = company.Id, Code = "STR", Name = "Base", Currency = "SAR",
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        var employees = Enumerable.Range(1, employeeCount).Select(i => new Employee
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeCode = $"E{i:000}", FullName = $"Employee {i:000}",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1), WorkEmail = $"e{i}@job.test",
            Nationality = "SAU", ContractType = "Indefinite",
        }).ToList();
        db.Employees.AddRange(employees);
        // Employees not yet attributed to any company (the old request path hid these from a company-scoped caller).
        var unassigned = Enumerable.Range(1, unassignedEmployees).Select(i => new Employee
        {
            TenantId = tenant, CompanyId = null, EmployeeCode = $"N{i:000}", FullName = $"Unassigned {i:000}",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1), Nationality = "SAU", ContractType = "Indefinite",
        }).ToList();
        db.Employees.AddRange(unassigned);
        Company? other = null;
        PayrollRun? otherRun = null;
        if (emptySecondCompany)
        {
            other = new Company { TenantId = tenant, LegalNameEn = "New KSA Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" };
            db.Companies.Add(other);
            otherRun = new PayrollRun { TenantId = tenant, CompanyId = other.Id, Year = 2026, Month = 6, Status = "Draft" };
            db.PayrollRuns.Add(otherRun);
        }
        // The person who queues runs: active, holds payroll.write, group-level.
        db.Users.Add(new User
        {
            Id = Actor, TenantId = tenant, Email = $"actor-{tenant:N}@job.test", NormalizedEmail = $"ACTOR-{tenant:N}@JOB.TEST",
            FullName = "Payroll Maker", PasswordHash = "x", Status = "Active", AccessMode = "FullPortal", IsActive = true,
            IsEmailConfirmed = true, IsGroupScope = true,
        });
        db.Set<UserPermissionOverride>().Add(new UserPermissionOverride
        {
            TenantId = tenant, UserId = Actor, PermissionKey = "payroll.write", Effect = "Allow", IsActive = true, Reason = "test",
        });
        await db.SaveChangesAsync();
        employees.AddRange(unassigned);

        for (var i = 0; i < employees.Count; i++)
        {
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenant, EmployeeId = employees[i].Id, SalaryStructureId = structure.Id,
                BasicSalary = 10_000m + 2_000m * i, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
                EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenant, EmployeeId = employees[i].Id, Iban = "SA4420000001234567891234",
                MolId = $"MOL-{employees[i].EmployeeCode}", SalaryCurrency = "SAR",
            });
        }
        var loan = new EmployeeLoan
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = employees[0].Id,
            LoanNumber = "LN-JOB-1", Status = "Active", ApprovedAmount = 5_000m, OutstandingBalance = 5_000m,
            InstallmentAmount = 1_000m, ApprovedInstallments = 5, TotalRepaid = 0m,
        };
        db.EmployeeLoans.Add(loan);
        var run = new PayrollRun { TenantId = tenant, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft" };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new World
        {
            Conn = conn, Db = db, Tenant = tenant, CompanyId = company.Id, RunId = run.Id, LoanId = loan.Id, Employees = employees,
            OtherCompanyId = other?.Id ?? Guid.Empty, OtherRunId = otherRun?.Id ?? Guid.Empty,
        };
    }

    private static PayrollRunProcessor Processor(ZayraDbContext db, ICountryPackResolver? packs = null)
    {
        var rules = KsaRules();
        return new PayrollRunProcessor(db, packs ?? new KsaPacks(rules), rules,
            new Zayra.Api.Infrastructure.Governance.CompanyTaxPolicyResolver(db),
            new Zayra.Api.Infrastructure.Employees.EmployeeActivationGuard(db),
            new ProrationPolicyResolver(new CompanyRatePolicyResolver(db)),
            new Zayra.Api.Infrastructure.WorkWeek.WorkWeekService(db));
    }

    /// <summary>The in-process entry point the existing suites drive, acting as <see cref="Actor"/>.</summary>
    private static PayrollController Controller(ZayraDbContext db, Guid tenant)
    {
        var rules = KsaRules();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Actor.ToString()),
                new Claim("permission", "payroll.write"),
            }, "test")),
        };
        var ctrl = new PayrollController(db, new Unrestricted(), new Accessor(http), new NullNotifications(),
            new KsaPacks(rules), rules, new NullLetters(), new NullStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    private sealed record Snapshot(
        List<string> Slips, List<string> Earnings, List<string> Deductions, List<string> Validation, string Run,
        string Loan, List<string> Audit);

    /// <summary>Everything a pass writes, in a tenant-independent, order-independent shape.</summary>
    private static async Task<Snapshot> TakeAsync(ZayraDbContext db, World w)
    {
        static string M(decimal v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var slips = (await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == w.RunId).ToListAsync())
            .Select(s => $"{s.EmployeeCode}|{M(s.BasicSalary)}|{M(s.HousingAllowance)}|{M(s.TransportAllowance)}|{M(s.OtherAllowances)}|" +
                         $"{M(s.GrossSalary)}|{M(s.Deductions)}|{M(s.NetSalary)}|{M(s.EmployeeStatutoryTotal)}|{M(s.EmployerStatutoryTotal)}|{M(s.LoanDeductions)}|{s.Status}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var earnings = (await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == w.RunId).ToListAsync())
            .Select(e => $"{e.EmployeeId}|{e.ComponentCode}|{M(e.Amount)}|{e.Source}|{e.GlDriverKey}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var deductions = (await db.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == w.RunId).ToListAsync())
            .Select(d => $"{d.EmployeeId}|{d.ComponentCode}|{M(d.Amount)}|{d.Source}|{d.IsEmployerContribution}|{d.GlDriverKey}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var validation = (await db.PayrollValidationResults.AsNoTracking().Where(v => v.PayrollRunId == w.RunId).ToListAsync())
            .Select(v => $"{v.EmployeeId}|{v.Severity}|{v.Code}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var run = await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == w.RunId);
        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == w.LoanId);
        var audit = (await db.PayrollAuditLogs.AsNoTracking().Where(a => a.TenantId == w.Tenant).ToListAsync())
            .Select(a => $"{a.Action}|{a.EntityName}|{a.EntityId == w.RunId.ToString()}|{a.UserId}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new Snapshot(slips, earnings, deductions, validation,
            $"{run.Status}|{run.EmployeeCount}|{M(run.TotalGrossSalary)}|{M(run.TotalDeductions)}|{M(run.TotalNetSalary)}|{M(run.TotalEmployerStatutoryCost)}|{run.ProcessedByUserId}",
            $"{M(loan.OutstandingBalance)}|{M(loan.TotalRepaid)}", audit);
    }

    // ── 1. The job's path gives the identical result ───────────────────────────────────────────

    [Fact]
    public async Task The_job_path_produces_exactly_what_the_in_process_path_produces()
    {
        using var inProcess = await SeedAsync();
        using var viaJob = await SeedAsync();

        (await Controller(inProcess.Db, inProcess.Tenant).Process(inProcess.RunId, default)).Should().BeOfType<OkObjectResult>();

        var hooks = new PayrollProcessHooks { Progress = (_, _, _) => Task.CompletedTask, BeforeCommit = _ => Task.CompletedTask };
        var outcome = await Processor(viaJob.Db).ProcessAsync(
            new PayrollProcessRequest(viaJob.RunId, viaJob.Tenant, Actor, "10.1.2.3"), hooks, default);
        outcome.StatusCode.Should().Be(200);

        var expected = await TakeAsync(inProcess.Fresh(), inProcess);
        var actual = await TakeAsync(viaJob.Fresh(), viaJob);

        expected.Slips.Should().HaveCount(2, "this is a real pass, not an empty comparison");
        expected.Earnings.Should().NotBeEmpty();
        expected.Deductions.Should().NotBeEmpty();
        expected.Loan.Should().Be("4000.00|1000.00", "the loan ledger moved once");
        actual.Slips.Should().Equal(expected.Slips);
        actual.Earnings.Should().Equal(expected.Earnings);
        actual.Deductions.Should().Equal(expected.Deductions);
        actual.Validation.Should().Equal(expected.Validation);
        actual.Run.Should().Be(expected.Run);
        actual.Loan.Should().Be(expected.Loan);
        // Same audit rows, attributed to the acting user (the one who clicked), not to a worker.
        actual.Audit.Should().Equal(expected.Audit);
        actual.Audit.Should().Contain(a => a.StartsWith("payroll.run.processed|PayrollRun|True|" + Actor));
        actual.Run.Should().EndWith("|" + Actor, "the processor of record is the user who processed it (maker-checker)");
    }

    [Fact]
    public async Task The_audit_row_carries_the_callers_address_and_user_as_the_request_did()
    {
        using var w = await SeedAsync();
        await Processor(w.Db).ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, Actor, "10.1.2.3"), null, default);

        var audit = await w.Fresh().PayrollAuditLogs.AsNoTracking().SingleAsync(a => a.Action == "payroll.run.processed");
        audit.UserId.Should().Be(Actor);
        using var meta = JsonDocument.Parse(audit.MetadataJson!);
        meta.RootElement.GetProperty("ip").GetString().Should().Be("10.1.2.3");
        meta.RootElement.GetProperty("userId").GetString().Should().Be(Actor.ToString());
    }

    [Fact]
    public async Task The_pass_pins_every_write_to_the_requested_tenant_when_another_tenant_has_a_run_for_the_same_period()
    {
        using var w = await SeedAsync();
        // A second tenant in the same database with its own Draft run for the same month and its own employees.
        var other = Guid.NewGuid();
        var otherCompany = new Company { TenantId = other, LegalNameEn = "Other Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" };
        w.Db.Companies.Add(otherCompany);
        var otherEmployee = new Employee { TenantId = other, CompanyId = otherCompany.Id, EmployeeCode = "X001", FullName = "Other Person", Status = "Active", JoiningDate = new DateTime(2023, 1, 1), Nationality = "SAU", ContractType = "Indefinite" };
        w.Db.Employees.Add(otherEmployee);
        var otherRun = new PayrollRun { TenantId = other, CompanyId = otherCompany.Id, Year = 2026, Month = 6, Status = "Draft" };
        w.Db.PayrollRuns.Add(otherRun);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        await Processor(w.Db).ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, Actor, null), null, default);

        var check = w.Fresh();
        (await check.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == otherRun.Id)).Status.Should().Be("Draft");
        (await check.PayrollSlips.AsNoTracking().AnyAsync(s => s.TenantId == other)).Should().BeFalse();
        (await check.PayrollSlips.AsNoTracking().Where(s => s.RunId == w.RunId).Select(s => s.EmployeeCode).ToListAsync())
            .Should().BeEquivalentTo(w.Employees.Select(e => e.EmployeeCode));
    }

    // ── 2. Run-state refusals are the same 4xx, and are available synchronously ──────────────

    [Theory]
    [InlineData("Processed")]
    [InlineData("PendingFinanceReview")]
    [InlineData("Locked")]
    [InlineData("Approved")]
    [InlineData("Paid")]
    public void A_completed_run_is_refused_with_the_same_400(string status)
    {
        var refusal = PayrollRunProcessor.CheckRunState(new PayrollRun { Status = status });
        refusal.Should().NotBeNull();
        refusal!.StatusCode.Should().Be(400);
        JsonSerializer.SerializeToElement(refusal.Body).GetProperty("message").GetString()
            .Should().Contain($"A run in '{status}' status cannot be reprocessed");
    }

    [Fact]
    public void A_voided_run_is_refused_with_run_voided_and_a_draft_is_allowed()
    {
        var voided = PayrollRunProcessor.CheckRunState(new PayrollRun { Status = "Voided" });
        voided!.StatusCode.Should().Be(400);
        JsonSerializer.Serialize(voided.Body).Should().Contain("run_voided");
        PayrollRunProcessor.CheckRunState(new PayrollRun { Status = "Draft" }).Should().BeNull();
        PayrollRunProcessor.CheckRunState(new PayrollRun { Status = "Failed" }).Should().BeNull("a failed run may be processed again");
    }

    [Fact]
    public async Task A_pass_over_a_company_with_no_country_is_refused_422_and_writes_nothing()
    {
        using var w = await SeedAsync(countryCode: "");
        var outcome = await Processor(w.Db).ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, Actor, null), null, default);
        outcome.StatusCode.Should().Be(422);
        PayrollProcessFailure.FromOutcome(outcome).Code.Should().Be("country_code_missing");
        (await w.Fresh().PayrollSlips.AnyAsync()).Should().BeFalse();
    }

    // ── 3. Progress and the in-transaction commit hook ───────────────────────────────────────────

    [Fact]
    public async Task Progress_is_reported_at_the_start_and_the_end_and_the_commit_hook_runs_once_inside_the_transaction()
    {
        using var w = await SeedAsync();
        var progress = new List<(int Done, int Total)>();
        var hookCalls = 0;
        bool? insideTransaction = null;
        var hooks = new PayrollProcessHooks
        {
            Progress = (done, total, _) => { progress.Add((done, total)); return Task.CompletedTask; },
            BeforeCommit = _ =>
            {
                hookCalls++;
                insideTransaction = w.Db.Database.CurrentTransaction is not null;
                return Task.CompletedTask;
            },
        };

        var outcome = await Processor(w.Db).ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, Actor, null), hooks, default);

        outcome.Succeeded.Should().BeTrue();
        progress.Should().Equal((0, 2), (2, 2));
        hookCalls.Should().Be(1);
        insideTransaction.Should().BeTrue("the lease fence must share the run's transaction");
    }

    [Fact]
    public async Task Progress_is_coarse_for_a_larger_run_never_one_report_per_employee()
    {
        using var w = await SeedAsync(employeeCount: 60);
        var progress = new List<(int Done, int Total)>();
        var hooks = new PayrollProcessHooks { Progress = (d, t, _) => { progress.Add((d, t)); return Task.CompletedTask; } };

        var outcome = await Processor(w.Db).ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, Actor, null), hooks, default);

        outcome.Succeeded.Should().BeTrue();
        progress.First().Should().Be((0, 60));
        progress.Last().Should().Be((60, 60));
        progress.Select(p => p.Done).Should().BeInAscendingOrder();
        progress.Count.Should().BeLessThanOrEqualTo(5, "reports are throttled by count and by time, not per row");
    }

    // ── 4. All or nothing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_commit_hook_that_refuses_rolls_the_whole_pass_back_and_a_retry_applies_exactly_once()
    {
        using var w = await SeedAsync();
        var lost = new PayrollProcessHooks { BeforeCommit = _ => throw new BackgroundJobLeaseLostException(Guid.NewGuid()) };

        var act = () => Processor(w.Db).ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, Actor, null), lost, default);
        await act.Should().ThrowAsync<BackgroundJobLeaseLostException>();

        var after = await TakeAsync(w.Fresh(), w);
        after.Run.Should().StartWith("Draft|", "a stale worker must not move the run");
        after.Slips.Should().BeEmpty();
        after.Earnings.Should().BeEmpty();
        after.Deductions.Should().BeEmpty();
        after.Audit.Should().BeEmpty();
        after.Loan.Should().Be("5000.00|0.00");

        // A retry (a fresh pass over the same run) is safe and applies once.
        var retry = Processor(w.Fresh());
        (await retry.ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, Actor, null), null, default)).Succeeded.Should().BeTrue();
        var done = await TakeAsync(w.Fresh(), w);
        done.Slips.Should().HaveCount(2);
        done.Loan.Should().Be("4000.00|1000.00", "the loan is decremented exactly once across the failed and the retried pass");
        done.Audit.Count(a => a.StartsWith("payroll.run.processed")).Should().Be(1);
    }

    [Fact]
    public async Task A_failure_inside_one_employees_calculation_names_that_employee_keeps_the_exception_type_and_saves_nothing()
    {
        using var w = await SeedAsync();
        var packs = new ThrowingPacks(KsaRules(), throwWhenBasicIs: 12_000m);

        var act = () => Processor(w.Db, packs).ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, Actor, null), null, default);
        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;

        var second = w.Employees[1];
        ex.Data[PayrollRunProcessor.FailingEmployeeIdKey].Should().Be(second.Id.ToString());
        ex.Data[PayrollRunProcessor.FailingEmployeeCodeKey].Should().Be(second.EmployeeCode);

        var failure = PayrollProcessFailure.FromException(ex);
        failure.Code.Should().Be("employee_calculation_failed");
        failure.FailedEmployees.Should().ContainSingle().Which.Should().Be(new FailedEmployee(second.Id, second.EmployeeCode, "calculation_failed"));
        failure.Message.Should().NotContain("secret-detail", "the raw exception text is never user-facing");

        var after = await TakeAsync(w.Fresh(), w);
        after.Run.Should().StartWith("Draft|");
        after.Slips.Should().BeEmpty();
        after.Loan.Should().Be("5000.00|0.00");
    }

    // ── 5. The handler: tenant, scope, checkpoints, failures ─────────────────────────────────────

    private static PayrollRunProcessJobHandler Handler(World w, ICountryPackResolver? packs = null) =>
        new(w.Db, Processor(w.Db, packs), NullLogger<PayrollRunProcessJobHandler>.Instance);

    private static PayrollProcessJobPayload Payload(World w, bool group = true, params Guid[] companies) =>
        new(w.RunId, w.Tenant, w.CompanyId, Actor, "10.1.2.3", group, companies);

    private static string StoredError(BackgroundJobPermanentFailureException ex) => ex.Message;

    [Fact]
    public async Task The_handler_processes_the_run_fences_inside_the_transaction_and_records_a_result()
    {
        using var w = await SeedAsync();
        var port = new FakePort(w.Tenant) { OnFence = () => w.Db.Database.CurrentTransaction.Should().NotBeNull() };

        await Handler(w).RunAsync(Payload(w), port, default);

        port.Fences.Should().Equal(PayrollRunProcessJobHandler.ItemKey);
        port.Progress.Should().Equal((0, 2), (2, 2));
        port.Result.Should().NotBeNull();
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Processed|2|");
    }

    [Fact]
    public async Task A_checkpointed_job_is_completed_without_processing_again()
    {
        using var w = await SeedAsync();
        var port = new FakePort(w.Tenant) { Checkpointed = true };

        await Handler(w).RunAsync(Payload(w), port, default);

        port.Fences.Should().BeEmpty();
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Draft|", "an earlier attempt already committed; this one must apply nothing");
        port.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task A_payload_for_another_tenant_than_the_job_row_is_refused()
    {
        using var w = await SeedAsync();
        var port = new FakePort(Guid.NewGuid());

        var act = () => Handler(w).RunAsync(Payload(w), port, default);

        await act.Should().ThrowAsync<BackgroundJobPermanentFailureException>();
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Draft|");
    }

    [Fact]
    public async Task A_caller_limited_to_other_companies_cannot_have_this_run_processed()
    {
        using var w = await SeedAsync();
        var port = new FakePort(w.Tenant);

        var act = () => Handler(w).RunAsync(Payload(w, group: false, Guid.NewGuid()), port, default);

        var ex = (await act.Should().ThrowAsync<BackgroundJobPermanentFailureException>()).Which;
        PayrollProcessFailure.Decode(StoredError(ex)).Code.Should().Be("run_not_found");
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Draft|");

        // …and the same job for a caller who does hold the run's company goes through.
        await Handler(w).RunAsync(Payload(w, group: false, w.CompanyId), new FakePort(w.Tenant), default);
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Processed|");
    }

    [Fact]
    public async Task A_refusal_fails_the_job_with_its_code_and_message_and_changes_nothing()
    {
        using var w = await SeedAsync(countryCode: "");
        var port = new FakePort(w.Tenant);

        var act = () => Handler(w).RunAsync(Payload(w), port, default);

        var ex = (await act.Should().ThrowAsync<BackgroundJobPermanentFailureException>()).Which;
        var failure = PayrollProcessFailure.Decode(StoredError(ex));
        failure.Code.Should().Be("country_code_missing");
        failure.Message.Should().Contain("has no country set");
        port.Fences.Should().BeEmpty();
        (await w.Fresh().PayrollSlips.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_unexpected_error_fails_the_job_with_a_safe_message_naming_the_employee_and_no_exception_text()
    {
        using var w = await SeedAsync();
        var port = new FakePort(w.Tenant);

        var act = () => Handler(w, new ThrowingPacks(KsaRules(), throwWhenBasicIs: 12_000m)).RunAsync(Payload(w), port, default);

        var ex = (await act.Should().ThrowAsync<BackgroundJobPermanentFailureException>()).Which;
        StoredError(ex).Should().NotContain("secret-detail").And.NotContain("InvalidOperationException");
        var failure = PayrollProcessFailure.Decode(StoredError(ex));
        failure.Code.Should().Be("employee_calculation_failed");
        failure.FailedEmployees.Should().ContainSingle().Which.EmployeeCode.Should().Be("E002");
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Draft|");
    }

    [Fact]
    public async Task A_lost_lease_at_the_fence_abandons_the_pass_and_the_run_is_untouched()
    {
        using var w = await SeedAsync();
        var port = new FakePort(w.Tenant) { OnFence = () => throw new BackgroundJobLeaseLostException(Guid.NewGuid()) };

        var act = () => Handler(w).RunAsync(Payload(w), port, default);

        await act.Should().ThrowAsync<BackgroundJobLeaseLostException>("the runner must see its own control-flow exception, unwrapped");
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Draft|");
    }

    [Fact]
    public async Task A_commit_whose_acknowledgement_was_lost_is_a_success_not_a_failure()
    {
        using var w = await SeedAsync(countryCode: "");   // the pass itself is refused…
        var port = new FakePort(w.Tenant) { CommittedElsewhere = true };   // …but the checkpoint says it was applied

        await Handler(w).RunAsync(Payload(w), port, default);

        port.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task A_transient_database_fault_is_rethrown_for_the_runner_to_retry_not_recorded_as_a_failure()
    {
        using var w = await SeedAsync();
        var port = new FakePort(w.Tenant);
        var packs = new ThrowingPacks(KsaRules(), throwWhenBasicIs: 12_000m, make: () => new NpgsqlException("connection reset", new TimeoutException()));

        var act = () => Handler(w, packs).RunAsync(Payload(w), port, default);

        await act.Should().ThrowAsync<NpgsqlException>();
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Draft|", "nothing was committed, so retrying is safe");
    }

    // ── 6. Company scope: a limited caller sees what the request path showed them ─────────────────────

    private static PayrollProcessRequest Scoped(World w, Guid runId, Guid company) =>
        new(runId, w.Tenant, Actor, null, GroupScope: false, CompanyIds: [company]);

    [Fact]
    public async Task A_group_caller_still_pays_unassigned_employees_and_a_company_scoped_caller_never_does()
    {
        using var group = await SeedAsync(employeeCount: 1, unassignedEmployees: 1);
        using var scoped = await SeedAsync(employeeCount: 1, unassignedEmployees: 1);

        (await Processor(group.Db).ProcessAsync(new PayrollProcessRequest(group.RunId, group.Tenant, Actor, null), null, default)).Succeeded.Should().BeTrue();
        (await Processor(scoped.Db).ProcessAsync(Scoped(scoped, scoped.RunId, scoped.CompanyId), null, default)).Succeeded.Should().BeTrue();

        (await group.Fresh().PayrollSlips.Select(x => x.EmployeeCode).OrderBy(x => x).ToListAsync()).Should().Equal("E001", "N001");
        (await scoped.Fresh().PayrollSlips.Select(x => x.EmployeeCode).ToListAsync()).Should().Equal("E001");
    }

    [Fact]
    public async Task A_scoped_caller_on_an_empty_new_company_gets_the_old_refusal_and_no_unassigned_names()
    {
        using var group = await SeedAsync(employeeCount: 1, unassignedEmployees: 1, emptySecondCompany: true);
        using var scoped = await SeedAsync(employeeCount: 1, unassignedEmployees: 1, emptySecondCompany: true);

        var refused = await Processor(scoped.Db).ProcessAsync(Scoped(scoped, scoped.OtherRunId, scoped.OtherCompanyId), null, default);

        refused.StatusCode.Should().Be(422);
        var failure = PayrollProcessFailure.FromOutcome(refused);
        failure.Code.Should().Be("no_company_employees");
        failure.FailedEmployees.Should().BeNull();
        (await scoped.Fresh().PayrollSlips.AnyAsync()).Should().BeFalse();

        // Unchanged for a group-level caller: the unassigned employee is pulled into the new company's run.
        (await Processor(group.Db).ProcessAsync(new PayrollProcessRequest(group.OtherRunId, group.Tenant, Actor, null), null, default))
            .Succeeded.Should().BeTrue();
        (await group.Fresh().PayrollSlips.Select(x => x.EmployeeCode).ToListAsync()).Should().Equal("N001");
    }

    [Fact]
    public async Task A_scoped_caller_gets_exactly_the_in_process_result()
    {
        using var inProcess = await SeedAsync();
        using var scoped = await SeedAsync();
        (await Controller(inProcess.Db, inProcess.Tenant).Process(inProcess.RunId, default)).Should().BeOfType<OkObjectResult>();

        (await Processor(scoped.Db).ProcessAsync(Scoped(scoped, scoped.RunId, scoped.CompanyId), null, default)).Succeeded.Should().BeTrue();

        var expected = await TakeAsync(inProcess.Fresh(), inProcess);
        var actual = await TakeAsync(scoped.Fresh(), scoped);
        actual.Slips.Should().Equal(expected.Slips);
        actual.Earnings.Should().Equal(expected.Earnings);
        actual.Deductions.Should().Equal(expected.Deductions);
        actual.Validation.Should().Equal(expected.Validation);
        actual.Run.Should().Be(expected.Run);
        actual.Loan.Should().Be(expected.Loan);
    }

    // ── 7. Authority is re-checked when the job runs ──────────────────────────────────────────────────

    [Fact]
    public async Task A_user_who_lost_payroll_write_or_was_deactivated_cannot_have_the_queued_run_processed()
    {
        using var revoked = await SeedAsync();
        await revoked.Db.Set<UserPermissionOverride>().ExecuteDeleteAsync();
        using var inactive = await SeedAsync();
        await inactive.Db.Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));

        foreach (var w in new[] { revoked, inactive })
        {
            var port = new FakePort(w.Tenant);
            var act = () => Handler(w).RunAsync(Payload(w), port, default);
            var ex = (await act.Should().ThrowAsync<BackgroundJobPermanentFailureException>()).Which;
            PayrollProcessFailure.Decode(StoredError(ex)).Code.Should().Be("actor_no_longer_permitted");
            port.Fences.Should().BeEmpty();
            (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Draft|");
        }
    }

    [Fact]
    public async Task A_company_limited_user_needs_a_current_grant_on_the_runs_company()
    {
        using var w = await SeedAsync();
        await w.Db.Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.IsGroupScope, false));

        var denied = () => Handler(w).RunAsync(Payload(w, group: false, w.CompanyId), new FakePort(w.Tenant), default);
        var ex = (await denied.Should().ThrowAsync<BackgroundJobPermanentFailureException>()).Which;
        PayrollProcessFailure.Decode(StoredError(ex)).Code.Should().Be("actor_no_longer_permitted");

        w.Db.Set<UserEntityAccess>().Add(new UserEntityAccess { TenantId = w.Tenant, UserId = Actor, CompanyId = w.CompanyId, IsActive = true });
        await w.Db.SaveChangesAsync();
        await Handler(w).RunAsync(Payload(w, group: false, w.CompanyId), new FakePort(w.Tenant), default);
        (await TakeAsync(w.Fresh(), w)).Run.Should().StartWith("Processed|");
    }

    // ── test doubles ──────────────────────────────────────────────────────────────────────────────

    private sealed class FakePort(Guid tenant) : IPayrollJobPort
    {
        public Guid TenantId => tenant;
        public bool Checkpointed { get; init; }
        public bool CommittedElsewhere { get; init; }
        public Action? OnFence { get; init; }
        public List<string> Fences { get; } = [];
        public List<(int, int)> Progress { get; } = [];
        public object? Result { get; private set; }
        public bool IsCheckpointed(string itemKey) => Checkpointed;
        public Task ReportProgressAsync(int done, int total, CancellationToken ct) { Progress.Add((done, total)); return Task.CompletedTask; }
        public Task CommitFenceAsync(string itemKey, CancellationToken ct) { OnFence?.Invoke(); Fences.Add(itemKey); return Task.CompletedTask; }
        public Task<bool> IsCheckpointCommittedAsync(string itemKey, CancellationToken ct) => Task.FromResult(CommittedElsewhere);
        public void SetResult(object result) => Result = result;
    }

    private sealed class KsaPacks(IStatutoryRuleReader rules) : ICountryPackResolver
    {
        private readonly KsaDeductionCalculator _calc = new(rules);
        public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
            cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
        public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
        public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
        public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
        public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
        public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
    }

    /// <summary>Real KSA arithmetic, except that the employee whose basic matches fails mid-calculation.</summary>
    private sealed class ThrowingPacks(IStatutoryRuleReader rules, decimal throwWhenBasicIs, Func<Exception>? make = null) : ICountryPackResolver
    {
        private readonly KsaDeductionCalculator _real = new(rules);
        public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
            new Calc(_real, throwWhenBasicIs, make ?? (() => new InvalidOperationException("secret-detail: SELECT * FROM salaries")));
        public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
        public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
        public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
        public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
        public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();

        private sealed class Calc(IStatutoryDeductionCalculator real, decimal basic, Func<Exception> make) : IStatutoryDeductionCalculator
        {
            public Task<StatutoryDeductionResult> CalculateAsync(StatutoryDeductionInput input, CancellationToken ct = default) =>
                input.Salary.Basic == basic ? throw make() : real.CalculateAsync(input, ct);
        }
    }

    private sealed class Unrestricted : Zayra.Api.Application.Common.IDataScopeService
    {
        public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new Zayra.Api.Application.Common.DataScope
            {
                Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null,
            });
    }

    private sealed class Accessor(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
    }

    private sealed class NullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class NullStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
    {
        public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public string ResolvePath(string storageUrl) => "/tmp/test";
    }
}
