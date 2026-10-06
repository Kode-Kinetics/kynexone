using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers.Ess;
using Zayra.Api.Controllers.Payroll;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The deductions statement (R3) on real PostgreSQL, through the real payroll run: two loans collected in ONE aggregate
/// LOAN_EMI line are split back per loan from the run's own witnesses and their balances reconcile to the loan ledger;
/// HR company scope holds; an employee never reads a colleague's slip, a draft run or a voided run.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class DeductionStatementPostgresTests(PostgresFixture fixture)
{
    private sealed record World(Guid TenantId, Guid CompanyId, int EmployeeId, int ColleagueId, Guid RunId, Guid LoanA, Guid LoanB);

    [Fact]
    public async Task TwoLoansInOneLoanEmi_SplitPerLoan_AndBalancesReconcileToTheLedger()
    {
        var w = await SeedAndProcessAsync();
        await using var db = fixture.CreateDb();
        var slip = await db.PayrollSlips.SingleAsync(s => s.RunId == w.RunId && s.EmployeeId == w.EmployeeId);
        var emi = await db.PayrollDeductions.Where(d => d.PayrollRunId == w.RunId && d.EmployeeId == w.EmployeeId && d.ComponentCode == "LOAN_EMI").ToListAsync();
        Assert.Single(emi);
        Assert.Equal(1_600m, emi[0].Amount); // 400 + 1,200 in ONE line: the engine cannot attribute it

        var detail = await new DeductionStatementService(db).DetailForSlipAsync(w.TenantId, slip.Id, DeductionAudience.Hr, CancellationToken.None);
        Assert.NotNull(detail);
        var s = detail!.Statement;
        var loanLines = s.Lines.Where(l => l.Category == DeductionCategories.EmployerLoan).OrderBy(l => l.Amount).ToList();
        Assert.Equal(2, loanLines.Count);
        Assert.Equal(emi[0].Amount, loanLines.Sum(l => l.Amount));
        Assert.DoesNotContain(ReleaseABlockReasons.DeductionSplitUnreconciled, s.Flags);

        var loans = await db.EmployeeLoans.AsNoTracking().Where(l => l.Id == w.LoanA || l.Id == w.LoanB).ToDictionaryAsync(l => l.Id);
        var a = loanLines.Single(l => l.LoanId == w.LoanA);
        var b = loanLines.Single(l => l.LoanId == w.LoanB);
        Assert.Equal(400m, a.Amount);
        Assert.Equal(1_200m, b.Amount);
        // The balance after this payslip IS the ledger balance (this is the latest run): no second derivation.
        Assert.Equal(loans[w.LoanA].OutstandingBalance, a.BalanceAfter);
        Assert.Equal(loans[w.LoanB].OutstandingBalance, b.BalanceAfter);
        Assert.Equal(4_400m, a.BalanceAfter);
        Assert.Equal(11, a.InstalmentsRemaining);
        Assert.Equal(4, b.InstalmentsRemaining);
        Assert.Equal(4_800m, b.BalanceAfter);
        Assert.True(b.ConsentOnFile);
        Assert.True(b.PercentOfWage > 10m);
        Assert.DoesNotContain(ReleaseABlockReasons.LoanInstalmentOver10PctNoConsent, s.Flags); // above 10% WITH consent
        Assert.All(loanLines, l => Assert.True(l.CountsTowardCap));
        Assert.All(loanLines, l => Assert.Equal(DeductionLegalBasis.EmployerLoan, l.LegalBasisKey));
        Assert.Equal(Math.Round(400m / slip.GrossSalary * 100m, 2), a.PercentOfWage);
        Assert.False(a.ConsentOnFile);

        // Every number drills to the persisted lines: the statement adds up to the slip's own deductions total.
        Assert.Equal(slip.Deductions, Math.Round(s.Lines.Sum(l => l.Amount), 2));
        Assert.Equal(s.Lines.Where(l => l.CountsTowardCap).Sum(l => l.Amount), s.DebtTotal);
        Assert.Equal(slip.GrossSalary / 2m, s.CapLimit);
        Assert.Contains(s.Lines, l => l.Category == DeductionCategories.Statutory && l.Amount > 0m);
        Assert.DoesNotContain(s.Lines, l => l.Category == DeductionCategories.Statutory && l.CountsTowardCap); // GOSI: #177
        Assert.True(DeductionStatementDto.From(detail).Reconciles);
    }

    [Fact]
    public async Task Employee_SeesOnlyFinalSlipsOfLiveRuns_AndNeverAColleagues()
    {
        var w = await SeedAndProcessAsync();
        await using (var db = fixture.CreateDb())
        {
            var own = await db.PayrollSlips.SingleAsync(s => s.RunId == w.RunId && s.EmployeeId == w.EmployeeId);
            var colleague = await db.PayrollSlips.SingleAsync(s => s.RunId == w.RunId && s.EmployeeId == w.ColleagueId);

            // Processed, not locked: the slip is a draft to the employee.
            Assert.IsType<NotFoundResult>(await Ess(db, w, w.EmployeeId).PayslipDeductions(own.Id, default));
            var list = Assert.IsType<OkObjectResult>(await Ess(db, w, w.EmployeeId).MyDeductions(new FixedClock(), 12, default));
            Assert.Empty(((EmployeeDeductionsDto)list.Value!).Statements);

            await LockAsync(db, w.RunId);
            var ok = Assert.IsType<OkObjectResult>(await Ess(db, w, w.EmployeeId).PayslipDeductions(own.Id, default));
            Assert.Equal(own.Id, ((DeductionStatementDto)ok.Value!).SlipId);
            Assert.True(await db.EmployeePayslipAccessLogs.AnyAsync(l => l.PayslipId == own.Id && l.Action == "ViewDeductions"));

            // A colleague's slip, even final, is indistinguishable from a missing one.
            Assert.IsType<NotFoundResult>(await Ess(db, w, w.EmployeeId).PayslipDeductions(colleague.Id, default));
            // An id from the client never chooses the employee: the token's employee_id does.
            var mine = (EmployeeDeductionsDto)((OkObjectResult)await Ess(db, w, w.EmployeeId).MyDeductions(new FixedClock(), 12, default)).Value!;
            Assert.All(mine.Statements, x => Assert.Equal(w.EmployeeId, x.EmployeeId));
            Assert.Single(mine.Statements);
            Assert.Equal(2, mine.Balances.Loans.Count);

            // A voided run disappears from self-service.
            var run = await db.PayrollRuns.SingleAsync(r => r.Id == w.RunId);
            run.Status = "Voided";
            await db.SaveChangesAsync();
            Assert.IsType<NotFoundResult>(await Ess(db, w, w.EmployeeId).PayslipDeductions(own.Id, default));
            var afterVoid = (EmployeeDeductionsDto)((OkObjectResult)await Ess(db, w, w.EmployeeId).MyDeductions(new FixedClock(), 12, default)).Value!;
            Assert.Empty(afterVoid.Statements);
        }
    }

    [Fact]
    public async Task HrCompanyScope_AnotherCompanysSlipAndRun_Are404()
    {
        var w = await SeedAndProcessAsync();
        Guid slipId;
        await using (var db = fixture.CreateDb())
            slipId = (await db.PayrollSlips.SingleAsync(s => s.RunId == w.RunId && s.EmployeeId == w.EmployeeId)).Id;

        var otherCompany = Guid.NewGuid();
        await using (var db = fixture.CreateDb())
        {
            db.Companies.Add(new Company { Id = otherCompany, TenantId = w.TenantId, LegalNameEn = $"Other {Guid.NewGuid():N}", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true });
            await db.SaveChangesAsync();
        }

        var outsider = Hr(w.TenantId, otherCompany);
        await using (var scoped = fixture.CreateDbWithAccessor(Accessor(outsider)))
        {
            var ctrl = HrController(scoped, outsider);
            Assert.IsType<NotFoundResult>(await ctrl.ForSlip(slipId, default));
            Assert.IsType<NotFoundResult>(await ctrl.ForRun(w.RunId, null, default));
            Assert.IsType<NotFoundResult>(await ctrl.ForEmployee(w.EmployeeId, new FixedClock(), 6, default));
        }

        var insider = Hr(w.TenantId, w.CompanyId);
        await using (var scoped = fixture.CreateDbWithAccessor(Accessor(insider)))
        {
            var ctrl = HrController(scoped, insider);
            var ok = Assert.IsType<OkObjectResult>(await ctrl.ForSlip(slipId, default));
            Assert.Equal(1_600m, ((DeductionStatementDto)ok.Value!).Lines.Where(l => l.LoanId != null).Sum(l => l.Amount));
            var rows = (List<RunDeductionRowDto>)((OkObjectResult)await ctrl.ForRun(w.RunId, null, default)).Value!;
            Assert.Equal(2, rows.Count);
            // The exception-first filter: only the colleague, whose unrecognised 6,000 deduction is over half the wage.
            var attention = (List<RunDeductionRowDto>)((OkObjectResult)await ctrl.ForRun(w.RunId, true, default)).Value!;
            var over = Assert.Single(attention);
            Assert.Equal(w.ColleagueId, over.EmployeeId);
            Assert.Equal(CapStatuses.Over, over.CapStatus);
            Assert.Contains(ReleaseABlockReasons.DeductionUnclassifiedCounted, over.Flags);
            Assert.Equal(ReleaseABlockReasons.DeductionsOverHalfWage, over.Flags[0]);
            var employee = (EmployeeDeductionsDto)((OkObjectResult)await ctrl.ForEmployee(w.EmployeeId, new FixedClock(), 6, default)).Value!;
            Assert.Single(employee.Statements); // HR sees the in-progress run, with its status
            Assert.Equal("Processed", employee.Statements[0].RunStatus);
        }
    }

    /// <summary>
    /// The browser lane (frontend/e2e/release-a-deductions.spec.ts) mocks the API with JSON this read model produced, so
    /// the screens are proven against real output, not a hand-written guess. KYNEX_WRITE_FIXTURES=1 rewrites the file;
    /// otherwise its shape (every property path) must still match what the API returns today.
    /// </summary>
    [Fact]
    public async Task FrontendFixture_IsTheReadModelsOwnOutput()
    {
        var w = await SeedAndProcessAsync();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var payload = new Dictionary<string, object?>();
        await using (var db = fixture.CreateDb())
        {
            var hr = HrController(db, Hr(w.TenantId, w.CompanyId));
            var own = await db.PayrollSlips.SingleAsync(s => s.RunId == w.RunId && s.EmployeeId == w.EmployeeId);
            var colleague = await db.PayrollSlips.SingleAsync(s => s.RunId == w.RunId && s.EmployeeId == w.ColleagueId);
            payload["runRows"] = ((OkObjectResult)await hr.ForRun(w.RunId, null, default)).Value;
            payload["slipWithLoans"] = ((OkObjectResult)await hr.ForSlip(own.Id, default)).Value;
            payload["slipOverLimit"] = ((OkObjectResult)await hr.ForSlip(colleague.Id, default)).Value;
            payload["employeeHr"] = ((OkObjectResult)await hr.ForEmployee(w.EmployeeId, new FixedClock(), 6, default)).Value;
            await LockAsync(db, w.RunId);
            payload["essMine"] = ((OkObjectResult)await Ess(db, w, w.EmployeeId).MyDeductions(new FixedClock(), 6, default)).Value;
            payload["essPayslip"] = ((OkObjectResult)await Ess(db, w, w.EmployeeId).PayslipDeductions(own.Id, default)).Value;
        }
        var actual = JsonSerializer.Serialize(payload, web) + "\n";
        var path = RepoPath("frontend/e2e/fixtures/release-a-deductions.json");
        if (Environment.GetEnvironmentVariable("KYNEX_WRITE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }
        Assert.True(File.Exists(path), $"Missing {path}. Run this test with KYNEX_WRITE_FIXTURES=1 to create it.");
        Assert.Equal(Shape(JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement), Shape(JsonDocument.Parse(actual).RootElement));
    }

    /// <summary>Every property path in a JSON document (array items collapsed), so ids and amounts may differ but fields may not.</summary>
    private static SortedSet<string> Shape(JsonElement e, string at = "$", SortedSet<string>? acc = null)
    {
        acc ??= new SortedSet<string>(StringComparer.Ordinal);
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) { acc.Add($"{at}.{p.Name}"); Shape(p.Value, $"{at}.{p.Name}", acc); }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Shape(item, $"{at}[]", acc);
                break;
        }
        return acc;
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frontend")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    // ── World ───────────────────────────────────────────────────────────────────────────────────────

    private async Task<World> SeedAndProcessAsync()
    {
        Guid tenantId, companyId, runId, loanA, loanB;
        int employeeId, colleagueId;
        await using (var db = fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
            var company = new Company
            {
                TenantId = tenantId, LegalNameEn = $"Masar {Guid.NewGuid():N}", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
                RegistrationNumber = $"MS-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
                CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var emp = Person(tenantId, company.Id, "MS-1", "Mohammed Al-Qahtani", "Saudi"); // pays GOSI: shown, never counted
            var colleague = Person(tenantId, company.Id, "MS-2", "Omar Haddad", "Egyptian");
            db.AddRange(company, emp, colleague);
            await db.SaveChangesAsync();
            foreach (var e in new[] { emp, colleague })
            {
                db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
                {
                    TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 8_000m, HousingAllowance = 2_000m,
                    TransportAllowance = 800m, OtherAllowance = 200m, Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
                });
                db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
                {
                    TenantId = tenantId, EmployeeId = e.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
                });
            }
            var a = Loan(tenantId, company.Id, emp, "LN-R3-A", instalment: 400m, outstanding: 4_800m, count: 12);
            var b = Loan(tenantId, company.Id, emp, "LN-R3-B", instalment: 1_200m, outstanding: 6_000m, count: 5);
            // B's instalment is 10.9% of the 11,000 wage: lawful only with the employee's signed consent (Art. 92).
            var consent = new EmployeeDocument
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeId = emp.Id, DocumentType = RestrictedEmployeeDocumentTypes.LoanDeductionConsent,
                FileName = "loan-consent.pdf", ContentType = "application/pdf",
            };
            db.EmployeeDocuments.Add(consent);
            b.ConsentDocumentId = consent.Id;
            b.CapBaseWage = 11_000m;
            db.EmployeeLoans.AddRange(a, b);
            foreach (var (loan, n) in new[] { (a, 12), (b, 5) })
                for (var i = 1; i <= n; i++)
                    db.LoanInstallments.Add(new LoanInstallment
                    {
                        TenantId = tenantId, LoanId = loan.Id, InstallmentNumber = i, DueDate = new DateOnly(2026, 6, 25).AddMonths(i - 1),
                        AmountDue = loan.InstallmentAmount, Status = "Pending",
                    });
            var run = new PayrollRun
            {
                TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft",
                CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.PayrollRuns.Add(run);
            // An approved deduction of a type payroll does not recognise: counted toward the limit (fail-closed) and flagged.
            db.PayrollAdjustments.Add(new PayrollAdjustment
            {
                TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = colleague.Id, AdjustmentType = "Misc recovery",
                Amount = -6_000m, Reason = "Recovered equipment cost", Status = "Approved",
            });
            await db.SaveChangesAsync();
            (companyId, employeeId, colleagueId, runId, loanA, loanB) = (company.Id, emp.Id, colleague.Id, run.Id, a.Id, b.Id);
        }
        await using (var db = fixture.CreateDb())
        {
            var result = await PayComponentNetPayDefectTests.Build(db, tenantId).Process(runId, CancellationToken.None);
            if (result is ObjectResult { StatusCode: >= 400 } bad)
                Assert.Fail($"Process refused: HTTP {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
        }
        return new World(tenantId, companyId, employeeId, colleagueId, runId, loanA, loanB);
    }

    private static Employee Person(Guid tenantId, Guid companyId, string code, string name, string nationality) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"{code}-{Guid.NewGuid():N}"[..14], FullName = name, Nationality = nationality,
        ContractType = "Fixed", Status = "Active", JoiningDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static EmployeeLoan Loan(Guid tenantId, Guid companyId, Employee e, string number, decimal instalment, decimal outstanding, int count) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeIntId = e.Id, EmployeeId = e.PublicId, EmployeeName = e.FullName,
        LoanTypeId = Guid.NewGuid(), LoanTypeName = "Personal loan", LoanNumber = $"{number}-{Guid.NewGuid():N}"[..16],
        RequestedAmount = instalment * count, ApprovedAmount = instalment * count, RequestedInstallments = count, ApprovedInstallments = count,
        InstallmentAmount = instalment, OutstandingBalance = outstanding, TotalRepaid = instalment * count - outstanding,
        Status = "Active", RepaymentMethod = "PayrollDeduction", Currency = "SAR", RepaymentStartDate = new DateOnly(2026, 1, 1),
    };

    private static async Task LockAsync(Zayra.Api.Data.ZayraDbContext db, Guid runId)
    {
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == runId);
        run.Status = "Locked";
        foreach (var s in await db.PayrollSlips.Where(s => s.RunId == runId).ToListAsync()) s.Status = "Final";
        await db.SaveChangesAsync();
    }

    private static EssDeductionsController Ess(Zayra.Api.Data.ZayraDbContext db, World w, int employeeId) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", w.TenantId.ToString()), new Claim("employee_id", employeeId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("permission", "ess.read"),
                    new Claim(ClaimTypes.Role, "Employee"),
                }, "Test")),
            },
        },
    };

    private static DeductionStatementsController HrController(Zayra.Api.Data.ZayraDbContext db, ClaimsPrincipal user) =>
        new(db, new DataScopeService(db)) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };

    private static ClaimsPrincipal Hr(Guid tenantId, Guid companyId) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Role, "Payroll Manager"), new Claim("permission", "payroll.read"), new Claim("permission", "employees.read"),
        new Claim(Zayra.Api.Application.Common.EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
    }, "Test"));

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new FixedAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FixedClock : Zayra.Api.Application.Common.ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(new DateOnly(2026, 10, 6));
    }
}
