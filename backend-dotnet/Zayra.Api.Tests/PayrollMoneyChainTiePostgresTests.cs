using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// THE MONEY PROOF. One dataset, one chain, tied to the cent at every hand-off:
/// <list type="number">
/// <item>the payslip each employee sees (the Payslip row's "Net" line, and the register slip behind it),</item>
/// <item>the bank file (the batch's WPS payment records, the batch total, and the generated wage file's records and total),</item>
/// <item>the settlement journal (DR 2100 Salaries Payable / CR 1000 Cash at Settle),</item>
/// <item>the ERP export (the journal-export document built for the run: its 2100 credit at the accrual and its 2100 debit / 1000
/// credit at the settlement).</item>
/// </list>
/// Once for a regular month with two employees whose figures carry cents (a Saudi with GOSI, an expatriate), and once for an
/// off-cycle final-settlement run. Real PostgreSQL, the real Process, Approve, Lock, payment batch, wage file, Settle and the
/// real journal-export builder. Its job is to pin the ties so every later payroll change is checked against them.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PayrollMoneyChainTiePostgresTests
{
    private const string Net2100 = "2100";
    private const string Cash1000 = "1000";
    private const string Settle2320 = "2320 - Final Settlement Payable";
    private const string Eosb5110 = "5110 - End of Service Benefit Expense";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PostgresFixture _fx;
    public PayrollMoneyChainTiePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task RegularRun_PayslipNet_TiesToTheCent_ToBankFile_SettlementJournal_AndErpExport()
    {
        var w = await SeedAsync();
        var runId = await NewRunAsync(w, PayrollRunTypes.Regular, includesRecurringPay: true, settlesFinalSettlements: false);

        await ProcessAsync(w, runId);
        await GeneratePayslipsAsync(w, runId);
        await ApproveAsync(w, runId);
        await LockAsync(w, runId);

        var net = await AssertChainAsync(w, runId, expectedEmployees: 2);
        net.Should().BeGreaterThan(0m);
        (net % 1m).Should().NotBe(0m, "the dataset must carry cents, or a tie 'to the cent' proves nothing");
    }

    [Fact]
    public async Task FinalSettlementRun_PayslipNet_TiesToTheCent_ToBankFile_SettlementJournal_AndErpExport()
    {
        var w = await SeedAsync();
        const decimal gross = 12_345.67m;
        // The Saudi national leaves: their statutory record is complete, so the wage file accepts their final payment.
        var runId = await SeedApprovedSettlementRunAsync(w, w.SaudiId, gross);

        await ProcessAsync(w, runId);
        await GeneratePayslipsAsync(w, runId);
        await ApproveAsync(w, runId);
        await LockAsync(w, runId);

        var net = await AssertChainAsync(w, runId, expectedEmployees: 1);
        net.Should().Be(gross, "a gratuity-only settlement with no recoveries pays exactly its gross");

        // The settlement run clears the 2320 accrual into 2100, and that clearing is in the ERP export too.
        await using var db = _fx.CreateDb();
        var build = await ExportAsync(db, w, runId);
        build.Document.Lines.Where(l => l.Side == "DR" && l.AccountCode.StartsWith("2320")).Sum(l => l.Amount)
            .Should().Be(gross, "the settlement payable cleared by the run reaches the ERP");
    }

    /// <summary>
    /// Lock now writes payslip rows, so it meets the company write guard. A user scoped to the run's company locks a month in
    /// which one employee has since moved to a sister company: the payslips belong to the RUN's company (the company that pays
    /// the month), so Lock succeeds and every slip gets one published payslip. Built with an HttpContextAccessor so the guard
    /// actually runs, as it does in production.
    /// </summary>
    [Fact]
    public async Task ScopedLocker_EmployeeMovedToASisterCompany_LockStillPublishesEveryPayslip_InTheRunsCompany()
    {
        var w = await SeedAsync();
        Guid sister;
        await using (var db = _fx.CreateDb())
        {
            var c = new Company { TenantId = w.TenantId, LegalNameEn = $"Sister {Guid.NewGuid():N}"[..14], CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" };
            db.Companies.Add(c);
            await db.SaveChangesAsync();
            sister = c.Id;
        }
        var runId = await NewRunAsync(w, PayrollRunTypes.Regular, includesRecurringPay: true, settlesFinalSettlements: false);
        await ProcessAsync(w, runId);
        await ApproveAsync(w, runId);
        await using (var db = _fx.CreateDb())
        {
            (await db.Employees.SingleAsync(e => e.Id == w.ExpatId)).CompanyId = sister;   // transferred after the pay month
            await db.SaveChangesAsync();
        }

        var scopedUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, w.Locker.ToString()),
            new Claim(ClaimTypes.Name, "Scoped finance"), new Claim(ClaimTypes.Role, "Finance Controller"),
            new Claim("entity_access", JsonSerializer.Serialize(new { c = w.CompanyId, r = "Finance Controller" })),
            new Claim("permission", "payroll.read"), new Claim("permission", "payroll.lock"),
        }, "Test"));
        await using (var scoped = _fx.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = scopedUser } }))
            Ok(await Payroll(scoped, w, w.Locker, "Finance Controller", scopedUser).Lock(runId, Ct), "scoped lock");

        await using var read = _fx.CreateDb();
        var payslips = await read.Payslips.IgnoreQueryFilters().AsNoTracking().Where(p => p.PayrollRunId == runId).ToListAsync();
        payslips.Select(p => p.EmployeeId).Should().BeEquivalentTo(new[] { w.SaudiId, w.ExpatId });
        payslips.Should().OnlyContain(p => p.IsPublishedToEss && p.CompanyId == w.CompanyId,
            "a month's payslips belong to the company that paid it, whatever company the employee is in today");
    }

    // ══ The chain ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Asserts every tie for a Locked run (creating the batch, the wage file and the settlement on the way) and returns
    /// the run's net pay total.</summary>
    private async Task<decimal> AssertChainAsync(World w, Guid runId, int expectedEmployees)
    {
        // 1. Payslips: the "Net" line of each Payslip row equals the register slip behind it.
        Dictionary<int, decimal> slipNet, payslipNet;
        await using (var db = _fx.CreateDb())
        {
            slipNet = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == runId)
                .ToDictionaryAsync(s => s.EmployeeId, s => s.NetSalary);
            var payslips = await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == runId).ToListAsync();
            var ids = payslips.Select(p => p.Id).ToList();
            var netLines = await db.PayslipComponents.AsNoTracking()
                .Where(c => ids.Contains(c.PayslipId) && c.ComponentType == "Net").ToListAsync();
            payslipNet = payslips.ToDictionary(p => p.EmployeeId, p => netLines.Where(c => c.PayslipId == p.Id).Sum(c => c.Amount));
        }
        slipNet.Should().HaveCount(expectedEmployees);
        payslipNet.Should().BeEquivalentTo(slipNet, "every employee's payslip shows exactly the register's net");
        var runNet = slipNet.Values.Sum();

        // 2. Bank file: one payment record per employee at their net, the batch total, and the generated wage file.
        var batchId = await CreateAcceptedBatchAsync(w, runId);
        await using (var db = _fx.CreateDb())
        {
            var batch = await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.Id == batchId);
            var records = await db.PayrollPaymentRecords.AsNoTracking().Where(r => r.PaymentBatchId == batchId).ToListAsync();
            records.ToDictionary(r => r.EmployeeId, r => r.Amount).Should().BeEquivalentTo(payslipNet, "each payment record pays exactly the payslip net");
            batch.TotalAmount.Should().Be(runNet, "the batch total is the sum of the payslips");

            var file = await db.WPSFileBatches.AsNoTracking().SingleAsync(f => f.PaymentBatchId == batchId);
            file.TotalSalaryAmount.Should().Be(runNet, "the wage file's declared total is the payslips' total");
            file.EmployeeCount.Should().Be(expectedEmployees);
            (await db.SIFFileRecords.AsNoTracking().Where(r => r.WPSFileBatchId == file.Id).ToDictionaryAsync(r => r.EmployeeId, r => r.NetPay))
                .Should().BeEquivalentTo(payslipNet, "each wage-file record carries exactly the payslip net");
        }

        // 3. Settlement journal: DR 2100 / CR 1000 for exactly the batch total, and the run's 2100 is then zero.
        await SettleAsync(w, batchId);
        await using (var db = _fx.CreateDb())
        {
            var gl = await db.FinanceGlEntries.AsNoTracking()
                .Where(x => x.SourceModule == "Payroll" && x.SourceEntityId == runId && !x.IsReversed).ToListAsync();
            var settlement = gl.Where(x => x.EventType == GlEventTypes.NetSettlement).ToList();
            settlement.Where(x => x.DebitAccount.StartsWith(Net2100)).Sum(x => x.Amount).Should().Be(runNet, "settlement relieves 2100 by the net paid");
            settlement.Where(x => x.CreditAccount.StartsWith(Cash1000)).Sum(x => x.Amount).Should().Be(runNet, "settlement pays cash of the net paid");
            gl.Where(x => x.CreditAccount.StartsWith(Net2100)).Sum(x => x.Amount)
                .Should().Be(gl.Where(x => x.DebitAccount.StartsWith(Net2100)).Sum(x => x.Amount), "after settlement nothing is left payable on 2100");
            gl.Where(x => x.EventType == GlEventTypes.Accrual || x.EventType == GlEventTypes.SettlementPayrollClearing)
                .Where(x => x.CreditAccount.StartsWith(Net2100)).Sum(x => x.Amount)
                .Should().Be(runNet, "the Lock booked exactly the payslips' net as payable");
        }

        // 4. ERP export: the journal built for this run carries the same figures, line for line.
        await using (var db = _fx.CreateDb())
        {
            var build = await ExportAsync(db, w, runId);
            var lines = build.Document.Lines;
            lines.Where(l => l.EventType == GlEventTypes.NetSettlement && l.Side == "DR" && l.AccountCode.StartsWith(Net2100))
                .Sum(l => l.RoundedDebit).Should().Be(runNet, "the ERP receives the settlement debit to 2100 at the net paid");
            lines.Where(l => l.EventType == GlEventTypes.NetSettlement && l.Side == "CR" && l.AccountCode.StartsWith(Cash1000))
                .Sum(l => l.RoundedCredit).Should().Be(runNet, "the ERP receives the cash credit at the net paid");
            lines.Where(l => l.EventType != GlEventTypes.NetSettlement && l.Side == "CR" && l.AccountCode.StartsWith(Net2100))
                .Sum(l => l.RoundedCredit).Should().Be(runNet, "the ERP receives the payable booked at Lock at the payslips' net");
            lines.Sum(l => l.RoundedDebit).Should().Be(lines.Sum(l => l.RoundedCredit), "the exported journal balances");
        }
        return runNet;
    }

    private static async Task<JournalExportBuild> ExportAsync(ZayraDbContext db, World w, Guid runId)
    {
        var service = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var (build, refusal) = await service.BuildAsync(w.TenantId, new JournalExportFilter(PayrollRunId: runId, CompanyId: w.CompanyId, Currency: "SAR"),
            "generic-csv", Ct);
        build.Should().NotBeNull($"the ERP export must build: {JsonSerializer.Serialize(refusal?.Payload)}");
        return build!;
    }

    // ══ World ════════════════════════════════════════════════════════════════════════════════════════

    private sealed record World(Guid TenantId, Guid CompanyId, int SaudiId, int ExpatId)
    {
        public Guid RunCreator { get; } = Guid.NewGuid();
        public Guid Preparer { get; } = Guid.NewGuid();
        public Guid Approver { get; } = Guid.NewGuid();
        public Guid Locker { get; } = Guid.NewGuid();
        public Guid Payer { get; } = Guid.NewGuid();
        public DateOnly Month { get; } = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
    }

    /// <summary>A SAR company in KSA (chart and drivers seeded, cost accounting off), bank-file settings, and two employees whose
    /// pay carries cents: a Saudi (GOSI employee and employer shares) and an expatriate (employer occupational hazard only).</summary>
    private async Task<World> SeedAsync()
    {
        Guid tid;
        await using (var db = _fx.CreateDb()) tid = await PostgresFixture.SeedMinimalTenant(db);
        Guid companyId, deptId;
        await using (var kit = await CostingLockKit.CreateAsync(costing: false, preset: true, _fx.CreateDb, tid))
        {
            await kit.AddCc("CC-TIE", "G_AND_A");
            deptId = (await kit.AddDept("D-TIE", "CC-TIE")).Id;
            companyId = kit.CompanyId;
        }

        await using var seed = _fx.CreateDb();
        var saudi = NewEmployee(tid, companyId, deptId, "Tie Saudi", "SAU", "NationalId", "1012345678");
        var expat = NewEmployee(tid, companyId, deptId, "Tie Expat", "Egypt", "Iqama", "2012345678");
        seed.Employees.AddRange(saudi, expat);
        await seed.SaveChangesAsync();
        foreach (var (e, basic, housing, transport) in new[] { (saudi, 7_777.77m, 1_944.44m, 333.33m), (expat, 6_543.21m, 1_635.80m, 0m) })
        {
            seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tid, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = basic, HousingAllowance = housing,
                TransportAllowance = transport, Currency = "SAR", EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
            });
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tid, EmployeeId = e.Id, Iban = SaudiBankExportTestData.IbanA, MolId = e.IdNumber!, SalaryCurrency = "SAR",
                BankRoutingCode = "RJHISARI", WpsEligible = true,
            });
        }
        await seed.SaveChangesAsync();
        (await new SaudiBankExportService(seed).SaveSettingsAsync(tid, companyId, Guid.NewGuid(), SaudiBankExportTestData.Settings(), Ct))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return new World(tid, companyId, saudi.Id, expat.Id);
    }

    private static Employee NewEmployee(Guid tid, Guid companyId, Guid deptId, string name, string nationality, string idType, string idNumber) => new()
    {
        TenantId = tid, CompanyId = companyId, EmployeeCode = $"TIE-{Guid.NewGuid():N}"[..12], FullName = name, EnglishName = name,
        Nationality = nationality, Status = EmployeeStatuses.Active, ContractType = "Indefinite", CountryCode = CountryCodes.Saudi,
        Salary = 7_000m, JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), GosiReference = "GOSI-1",
        GosiFirstRegisteredOn = new DateOnly(2015, 1, 1), DepartmentId = deptId, IdType = idType, IdNumber = idNumber, ReadinessState = "Ready",
        // The statutory details the pay interlock asks of a Saudi resident: a leaver (no longer Active) missing them is refused
        // from the wage file outright, which is the control working, not what this class tests.
        QiwaContractNumber = $"QC-{Guid.NewGuid():N}"[..12],
        WorkEmail = $"{Guid.NewGuid():N}@tie.test",
    };

    private async Task<Guid> NewRunAsync(World w, string runType, bool includesRecurringPay, bool settlesFinalSettlements)
    {
        await using var db = _fx.CreateDb();
        var run = new PayrollRun
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, Year = w.Month.Year, Month = w.Month.Month, Status = "Draft",
            RunType = runType, IncludesRecurringPay = includesRecurringPay, SettlesFinalSettlements = settlesFinalSettlements,
            CreatedByUserId = w.RunCreator, CreatedAtUtc = new DateTime(w.Month.Year, w.Month.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>The leaver's Approved final settlement (gratuity only, accrued DR 5110 / CR 2320) and the Draft off-cycle
    /// settlement run that carries them, seeded as <see cref="SalaryAdvanceLifecycleGlPostgresTests"/> seeds it.</summary>
    private async Task<Guid> SeedApprovedSettlementRunAsync(World w, int employeeId, decimal gross)
    {
        await using var db = _fx.CreateDb();
        var employee = await db.Employees.SingleAsync(e => e.Id == employeeId);
        employee.Status = EmployeeStatuses.Offboarded;
        var lastDay = w.Month.AddMonths(1).AddDays(-1);
        var period = DateTime.UtcNow.ToString("yyyy-MM");
        var offboarding = new EmployeeOffboarding
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, SeparationType = "Termination", Status = "InProgress",
            NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, OffboardingId = offboarding.Id, LastWorkingDay = lastDay,
            ServiceStartDate = new DateOnly(2020, 1, 1), SettlementDueDate = lastDay.AddDays(7), TerminationReason = "Termination", Currency = "SAR",
            GratuityAmount = gross, GrossPayable = gross, TotalDeductions = 0m, NetPayable = gross,
            Status = FinalSettlementStatuses.Approved, CreatedByUserId = Guid.NewGuid(), SubmittedByUserId = Guid.NewGuid(),
            ApprovedByUserId = Guid.NewGuid(), ApprovedByName = "Independent Approver", ApprovedAtUtc = DateTime.UtcNow,
            GlPostedAtUtc = DateTime.UtcNow, GlPeriod = period,
        };
        var run = new PayrollRun
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, Year = w.Month.Year, Month = w.Month.Month, Status = "Draft",
            RunType = PayrollRunTypes.OffCycle, IncludesRecurringPay = false, SettlesFinalSettlements = true, CreatedByUserId = w.RunCreator,
            CreatedAtUtc = new DateTime(w.Month.Year, w.Month.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.AddRange(offboarding, settlement, run);
        db.FinalSettlementLines.Add(new FinalSettlementLine
        {
            TenantId = w.TenantId, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.Gratuity,
            ComponentName = "End-of-service gratuity", LineType = FinalSettlementLineTypes.Earning,
            Source = FinalSettlementComponents.SettlementSource, Amount = gross, SortOrder = 0,
        });
        foreach (var (debit, credit) in new[] { (Eosb5110, string.Empty), (string.Empty, Settle2320) })
            db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = w.TenantId, CompanyId = w.CompanyId, SourceModule = FinalSettlementGlDescriptions.SourceModule,
                SourceEntityId = settlement.Id, SourceEntityRef = FinalSettlementGlDescriptions.SettlementRef(settlement.Id),
                EventType = GlEventTypes.SettlementAccrual, DebitAccount = debit, CreditAccount = credit, Amount = gross, Currency = "SAR",
                EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = period,
                Description = FinalSettlementGlDescriptions.AccrualPrefix + settlement.EmployeeCode,
            });
        db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, PayrollRunId = run.Id, EmployeeId = employee.Id, Mode = PayrollRunSelectionModes.Include,
        });
        await db.SaveChangesAsync();
        return run.Id;
    }

    // ══ Actions ══════════════════════════════════════════════════════════════════════════════════════

    private async Task ProcessAsync(World w, Guid runId)
    {
        await using var db = _fx.CreateDb();
        Ok(await Payroll(db, w, w.Preparer, "Payroll Officer").Process(runId, Ct), "process");
    }

    private async Task GeneratePayslipsAsync(World w, Guid runId)
    {
        await using var db = _fx.CreateDb();
        Ok(await Payroll(db, w, w.Preparer, "Payroll Officer").GeneratePayslips(runId, Ct), "generate payslips");
    }

    private async Task ApproveAsync(World w, Guid runId)
    {
        await using (var db = _fx.CreateDb()) await ReconTestSupport.SignOffAsync(db, runId);
        await using var db2 = _fx.CreateDb();
        Ok(await Payroll(db2, w, w.Approver, "Admin").Approve(runId, new PayrollDecisionRequest("reviewed"), Ct), "approve");
    }

    private async Task LockAsync(World w, Guid runId)
    {
        await using var db = _fx.CreateDb();
        Ok(await Payroll(db, w, w.Locker, "Admin").Lock(runId, Ct), "lock");
    }

    /// <summary>Creates the batch, generates its wage file, and records the bank's acceptance.</summary>
    private async Task<Guid> CreateAcceptedBatchAsync(World w, Guid runId)
    {
        Guid batchId;
        await using (var db = _fx.CreateDb())
        {
            var created = await Payroll(db, w, w.Locker, "Admin").CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), Ct);
            created.Should().BeOfType<CreatedResult>(Body(created));
            batchId = (await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.PayrollRunId == runId)).Id;
        }
        await using (var db = _fx.CreateDb())
            Ok(await Payroll(db, w, w.Locker, "Admin").GenerateWps(batchId, true, false, Ct), "generate the wage file");
        await using (var db = _fx.CreateDb())
        {
            var batch = await db.PayrollPaymentBatches.SingleAsync(b => b.Id == batchId);
            batch.WpsStatus = WpsStatuses.Accepted;   // the bank accepted the file (as PayrollRecoveryB3Tests records it)
            await db.SaveChangesAsync();
        }
        return batchId;
    }

    private async Task SettleAsync(World w, Guid batchId)
    {
        await using var db = _fx.CreateDb();
        Ok(await Payroll(db, w, w.Payer, "Admin").SettlePaymentBatch(batchId, new SettlePaymentBatchRequest("BANK-TIE-1"), Ct), "settle");
    }

    private static string Body(IActionResult r) => r is ObjectResult o ? JsonSerializer.Serialize(o.Value) : r.GetType().Name;

    private static void Ok(IActionResult r, string step) => r.Should().BeOfType<OkObjectResult>($"{step} must succeed: {Body(r)}");

    private static PayrollController Payroll(ZayraDbContext db, World w, Guid userId, string role, ClaimsPrincipal? user = null)
    {
        var rules = new StubRuleReader()
            .Set("ot.hourly_base", "wage").Set("ot.standard_multiplier", 1.5m).Set("ot.restday_multiplier", 2.0m)
            .Set("ot.holiday_multiplier", 2.0m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m)
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, role), new Claim(ClaimTypes.Role, role), new Claim("is_group_scope", "true"),
                new Claim("permission", "payroll.read"), new Claim("permission", "payroll.write"),
                new Claim("permission", "payroll.approve"), new Claim("permission", "payroll.lock"),
                new Claim("permission", "payroll.export"),
            }, "Test")),
        };
        if (user is not null) http.User = user;
        return new PayrollController(db, new _TieScope(), new HttpContextAccessor { HttpContext = http }, new _TieNotifications(),
            new _TieKsaResolver(rules), rules, new _TieLetters(), new MemoryDocumentStorage(), new PdfRenderGate(4))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}

file sealed class _TieScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
        Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _TieNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _TieKsaResolver(IStatutoryRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new KsaEndOfServiceCalculator(rules);
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _TieLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
