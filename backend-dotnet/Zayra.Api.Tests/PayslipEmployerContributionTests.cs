// PayslipEmployerContributionTests.cs
// ─────────────────────────────────────────────────────────────────────────────
// REGRESSION (demo seed, Asif): a non-Saudi G1 driver on SAR 4,800 with a SAR 400 loan instalment saw
// "Total deductions 496" against "Net pay 4,400" on his own payslip. The extra 96 was the EMPLOYER's 2%
// GOSI occupational-hazard contribution (GOSI-OH-ER): GeneratePayslips stored every PayrollDeduction row
// — employer lines included — as a PayslipComponent of type "Deduction", and the ESS detail and the ESS
// PDF summed that type. The stored payroll figures were right all along (slip.Deductions = 400,
// NetSalary = 4,400); only the payslip's line classification was wrong.
//
// What is pinned here, on every payslip surface (ESS detail, ESS PDF, HR register, HR PDF):
//   • employer contributions are never part of the employee's deductions total;
//   • gross − deductions = net;
//   • a Saudi's own GOSI share IS a deduction, the employer's share is NOT;
//   • payslips generated before the fix (employer lines stored as "Deduction") read correctly too;
//   • the PDF prints employer lines in their own, labelled section.
// SQLite in-memory: no Docker, no network.
// ─────────────────────────────────────────────────────────────────────────────

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class PayslipEmployerContributionTests
{
    private const int EmployeeId = 7;

    private static (ZayraDbContext Db, SqliteConnection Conn) CreateDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private sealed record Line(string Code, string Name, decimal Amount, string Source, bool Employer = false);

    /// <summary>Asif: non-Saudi, SAR 4,800, SAR 400 loan instalment, employer pays 2% occupational hazard.</summary>
    private static readonly Line[] AsifLines =
    {
        new("LOAN_EMI", "Loan instalment", 400m, "Loan"),
        new("GOSI-OH-ER", "Occupational Hazard (Employer)", 96m, "Statutory", Employer: true),
    };

    /// <summary>A Saudi on SAR 10,000 (existing subscriber): 9% + 0.75% from the employee, 9% + 0.75% + 2% from the employer.</summary>
    private static readonly Line[] SaudiLines =
    {
        new("GOSI-ANN-EE", "GOSI Annuities (Employee)", 900m, "Statutory"),
        new("GOSI-ANN-ER", "GOSI Annuities (Employer)", 900m, "Statutory", Employer: true),
        new("GOSI-SANED-EE", "SANED (Employee)", 75m, "Statutory"),
        new("GOSI-SANED-ER", "SANED (Employer)", 75m, "Statutory", Employer: true),
        new("GOSI-OH-ER", "Occupational Hazard (Employer)", 200m, "Statutory", Employer: true),
    };

    /// <summary>
    /// Seeds a Locked run exactly as Process leaves it: the slip aggregates (which exclude employer
    /// lines — PayslipLineInvariant) plus the earning and deduction rows.
    /// </summary>
    private static (Guid RunId, PayrollSlip Slip) SeedRun(ZayraDbContext db, Guid tenantId, decimal gross, Line[] lines)
    {
        var run = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 9, Status = "Locked", RunType = "Regular" };
        db.PayrollRuns.Add(run);
        db.Employees.Add(new Employee
        {
            Id = EmployeeId, TenantId = tenantId, EmployeeCode = "DEMO-E7", FullName = "Asif Khan",
            Department = "Logistics", Designation = "Driver", Status = "Active",
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = EmployeeId, SalaryCurrency = "SAR" });

        var employeeDeductions = lines.Where(l => !l.Employer).Sum(l => l.Amount);
        var slip = new PayrollSlip
        {
            TenantId = tenantId, RunId = run.Id, EmployeeId = EmployeeId,
            EmployeeCode = "DEMO-E7", EmployeeName = "Asif Khan", Department = "Logistics",
            BasicSalary = gross, GrossSalary = gross,
            Deductions = employeeDeductions,
            NetSalary = gross - employeeDeductions,
            LoanDeductions = lines.Where(l => l.Source == "Loan").Sum(l => l.Amount),
            EmployeeStatutoryTotal = lines.Where(l => l.Source == "Statutory" && !l.Employer).Sum(l => l.Amount),
            EmployerStatutoryTotal = lines.Where(l => l.Source == "Statutory" && l.Employer).Sum(l => l.Amount),
            Status = "Final",
        };
        db.PayrollSlips.Add(slip);
        db.PayrollEarnings.Add(new PayrollEarning
        {
            TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = EmployeeId,
            ComponentCode = "BASIC", ComponentName = "Basic Salary", Amount = gross, Source = "Salary",
        });
        foreach (var l in lines)
            db.PayrollDeductions.Add(new PayrollDeduction
            {
                TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = EmployeeId,
                ComponentCode = l.Code, ComponentName = l.Name, Amount = l.Amount, Source = l.Source,
                IsEmployerContribution = l.Employer,
            });
        db.SaveChanges();
        return (run.Id, slip);
    }

    /// <summary>
    /// A payslip generated BEFORE this fix: every deduction row, employer lines included, was stored as
    /// type "Deduction". This is the shape the demo tenant's September payslips are in today.
    /// </summary>
    private static void SeedLegacyPayslip(ZayraDbContext db, Guid tenantId, Guid runId, PayrollSlip slip, Line[] lines)
    {
        var payslip = new Payslip { TenantId = tenantId, PayrollRunId = runId, EmployeeId = EmployeeId, PayslipNumber = "PS-DEMO-E7-LEGACY", IsPublishedToEss = true };
        db.Payslips.Add(payslip);
        db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Earning", ComponentName = "Basic Salary", Amount = slip.GrossSalary });
        foreach (var l in lines)
            db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Deduction", ComponentName = l.Name, Amount = l.Amount });
        db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Net", ComponentName = "Net pay", Amount = slip.NetSalary });
        db.SaveChanges();
    }

    private static async Task PreparePayslip(ZayraDbContext db, Guid tenantId, Guid runId, PayrollSlip slip, Line[] lines, bool legacy)
    {
        if (legacy)
            SeedLegacyPayslip(db, tenantId, runId, slip, lines);
        else
            (await Hr(db, tenantId).GeneratePayslips(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    // ── Asif: the reported defect ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]   // generated after the fix
    [InlineData(true)]    // generated before the fix (employer line stored as "Deduction")
    public async Task Asif_payslip_deducts_only_the_loan_and_lists_the_employer_hazard_separately(bool legacy)
    {
        var (db, conn) = CreateDb();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var (runId, slip) = SeedRun(db, tenantId, 4_800m, AsifLines);
        await PreparePayslip(db, tenantId, runId, slip, AsifLines, legacy);

        var detail = await EssDetail(db, tenantId, slip.Id);

        detail.GrossSalary.Should().Be(4_800m);
        detail.TotalDeductions.Should().Be(400m, "the employer's 2% occupational hazard (96) is not taken from Asif's pay");
        detail.NetSalary.Should().Be(4_400m);
        detail.EmployerContributions.Should().Be(96m);
        detail.Reconciled.Should().BeTrue();
        detail.Lines.Should().ContainSingle(l => l.Type == PayslipLineTypes.Deduction)
            .Which.Name.Should().Be("Loan instalment");
        detail.Lines.Should().ContainSingle(l => l.Type == PayslipLineTypes.EmployerContribution)
            .Which.Should().BeEquivalentTo(new EssPayslipLineDto("Occupational Hazard (Employer)", 96m, PayslipLineTypes.EmployerContribution));
        AssertReconciles(detail);

        var pdf = await EssPdf(db, tenantId, slip.Id);
        AssertReconciles(pdf, gross: 4_800m, deductions: 400m, net: 4_400m);
        pdf.Items.Where(i => i.Type == PayslipLineTypes.EmployerContribution).Sum(i => i.Amount).Should().Be(96m);

        var (hrSlip, hrPdf) = await HrViews(db, tenantId, runId);
        hrSlip.Deductions.Should().Be(400m);
        (hrSlip.GrossSalary - hrSlip.Deductions).Should().Be(hrSlip.NetSalary);
        hrSlip.DeductionLines!.Where(l => !l.IsEmployerContribution).Sum(l => l.Amount).Should().Be(hrSlip.Deductions!.Value);
        hrSlip.DeductionLines!.Should().ContainSingle(l => l.IsEmployerContribution).Which.Code.Should().Be("GOSI-OH-ER");
        AssertReconciles(hrPdf, gross: 4_800m, deductions: 400m, net: 4_400m);
    }

    // ── A Saudi: employee GOSI counted, employer share not ────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saudi_payslip_counts_the_employee_gosi_share_and_never_the_employer_share(bool legacy)
    {
        var (db, conn) = CreateDb();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var (runId, slip) = SeedRun(db, tenantId, 10_000m, SaudiLines);
        await PreparePayslip(db, tenantId, runId, slip, SaudiLines, legacy);

        var detail = await EssDetail(db, tenantId, slip.Id);

        detail.TotalDeductions.Should().Be(975m, "GOSI annuities 900 + SANED 75 are the employee's own share");
        detail.NetSalary.Should().Be(9_025m);
        detail.EmployerContributions.Should().Be(1_175m, "annuities 900 + SANED 75 + occupational hazard 200, paid by the employer");
        detail.Reconciled.Should().BeTrue();
        detail.Lines.Where(l => l.Type == PayslipLineTypes.Deduction).Select(l => l.Name).Should()
            .BeEquivalentTo("GOSI Annuities (Employee)", "SANED (Employee)");
        detail.Lines.Where(l => l.Type == PayslipLineTypes.EmployerContribution).Select(l => l.Name).Should()
            .BeEquivalentTo("GOSI Annuities (Employer)", "SANED (Employer)", "Occupational Hazard (Employer)");
        AssertReconciles(detail);

        var pdf = await EssPdf(db, tenantId, slip.Id);
        AssertReconciles(pdf, gross: 10_000m, deductions: 975m, net: 9_025m);

        var (hrSlip, hrPdf) = await HrViews(db, tenantId, runId);
        (hrSlip.GrossSalary - hrSlip.Deductions).Should().Be(hrSlip.NetSalary);
        hrSlip.DeductionLines!.Where(l => !l.IsEmployerContribution).Sum(l => l.Amount).Should().Be(975m);
        hrSlip.DeductionLines!.Where(l => l.IsEmployerContribution).Sum(l => l.Amount).Should().Be(hrSlip.EmployerStatutoryTotal!.Value);
        AssertReconciles(hrPdf, gross: 10_000m, deductions: 975m, net: 9_025m);
    }

    /// <summary>Generation stores employer lines under their own type, and the stored amounts are unchanged.</summary>
    [Fact]
    public async Task Generate_stores_employer_lines_as_employer_contributions_without_changing_any_amount()
    {
        var (db, conn) = CreateDb();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var (runId, slip) = SeedRun(db, tenantId, 4_800m, AsifLines);

        (await Hr(db, tenantId).GeneratePayslips(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var components = db.PayslipComponents.AsNoTracking().ToList();
        components.Should().ContainSingle(c => c.ComponentType == PayslipLineTypes.EmployerContribution && c.ComponentName == "Occupational Hazard (Employer)" && c.Amount == 96m);
        components.Where(c => c.ComponentType == PayslipLineTypes.Deduction).Sum(c => c.Amount).Should().Be(400m);
        components.Should().ContainSingle(c => c.ComponentType == PayslipLineTypes.Net).Which.Amount.Should().Be(4_400m);

        var stored = db.PayrollSlips.AsNoTracking().Single(s => s.Id == slip.Id);
        (stored.GrossSalary, stored.Deductions, stored.NetSalary).Should().Be((4_800m, 400m, 4_400m),
            "this is a presentation fix — the payroll figures themselves are not recalculated");
    }

    /// <summary>The PDF itself: employer lines print in their own labelled section, the deductions total is 400.</summary>
    [Fact]
    public async Task Pdf_prints_employer_contributions_in_their_own_section_outside_the_deductions_total()
    {
        var pdf = await new LetterService().GeneratePayslipPdfAsync(new PayslipData(
            PayslipNumber: "PS-DEMO-E7", EmployeeCode: "DEMO-E7", EmployeeName: "Asif Khan", Department: "Logistics",
            Designation: "Driver", PayYear: 2026, PayMonth: 9, Currency: "SAR",
            Items:
            [
                new PayslipLineItem("Basic Salary", 4_800m, PayslipLineTypes.Earning),
                new PayslipLineItem("Loan instalment", 400m, PayslipLineTypes.Deduction),
                new PayslipLineItem("Occupational Hazard (Employer)", 96m, PayslipLineTypes.EmployerContribution),
                new PayslipLineItem("Net pay", 4_400m, PayslipLineTypes.Net),
            ],
            CompanyName: "Demo Co"));

        using var document = PdfDocument.Open(pdf);
        // Extracted text loses the "ti" ligature ("Deduc ons"), so assert on section ORDER and content.
        var text = string.Concat(document.GetPages().Select(p => p.Text)).Replace(" ", string.Empty);
        var deductionsAt = text.IndexOf("DEDUCTIONS", StringComparison.Ordinal);
        var netAt = text.IndexOf("NETPAY", StringComparison.Ordinal);
        var employerAt = text.IndexOf("EMPLOYERCONTRIBUTIONS(NOTDEDUCTEDFROMYOURPAY)", StringComparison.Ordinal);
        deductionsAt.Should().BeGreaterThan(-1);
        netAt.Should().BeGreaterThan(deductionsAt);
        employerAt.Should().BeGreaterThan(netAt, "the employer section is its own block, after net pay");
        var deductionsBlock = text[deductionsAt..netAt];
        deductionsBlock.Should().Contain("Loaninstalment400.00").And.EndWith("400.00", "the deductions total is the loan alone");
        deductionsBlock.Should().NotContain("Occupa").And.NotContain("496");
        text[employerAt..].Should().Contain("Hazard(Employer)96.0");
    }

    // ── Reconciliation assertions, one per view model ────────────────────────────────────────────

    private static void AssertReconciles(EssPayslipDetailDto d)
    {
        d.Lines.Where(l => l.Type == PayslipLineTypes.Earning).Sum(l => l.Amount).Should().Be(d.GrossSalary);
        d.Lines.Where(l => l.Type == PayslipLineTypes.Deduction).Sum(l => l.Amount).Should().Be(d.TotalDeductions);
        d.Lines.Where(l => l.Type == PayslipLineTypes.EmployerContribution).Sum(l => l.Amount).Should().Be(d.EmployerContributions);
        (d.GrossSalary - d.TotalDeductions).Should().Be(d.NetSalary, "gross − employee deductions = net on the ESS payslip");
    }

    private static void AssertReconciles(PayslipData pdf, decimal gross, decimal deductions, decimal net)
    {
        var g = pdf.Items.Where(i => i.Type == PayslipLineTypes.Earning).Sum(i => i.Amount);
        var d = pdf.Items.Where(i => i.Type == PayslipLineTypes.Deduction).Sum(i => i.Amount);
        var n = pdf.Items.Single(i => i.Type == PayslipLineTypes.Net).Amount;
        (g, d, n).Should().Be((gross, deductions, net));
        (g - d).Should().Be(n, "gross − employee deductions = net on the payslip PDF");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<EssPayslipDetailDto> EssDetail(ZayraDbContext db, Guid tenantId, Guid slipId)
    {
        var result = await Ess(db, tenantId, new CapturingLetters()).PayslipDetail(slipId, CancellationToken.None);
        return (EssPayslipDetailDto)((OkObjectResult)result.Result!).Value!;
    }

    private static async Task<PayslipData> EssPdf(ZayraDbContext db, Guid tenantId, Guid slipId)
    {
        var letters = new CapturingLetters();
        (await Ess(db, tenantId, letters).DownloadPayslip(slipId, CancellationToken.None)).Should().BeOfType<FileContentResult>();
        return letters.Last!;
    }

    private static async Task<(PayrollSlipDto Slip, PayslipData Pdf)> HrViews(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        var page = (PagedResult<PayrollSlipDto>)((OkObjectResult)await Hr(db, tenantId).Slips(runId, 1, 50, CancellationToken.None)).Value!;
        var payslipId = db.Payslips.AsNoTracking().Single(p => p.PayrollRunId == runId).Id;
        var letters = new CapturingLetters();
        (await Hr(db, tenantId, letters).DownloadSlipPdf(payslipId, CancellationToken.None)).Should().BeOfType<FileContentResult>();
        return (page.Items.Single(), letters.Last!);
    }

    private static EmployeeSelfServiceController Ess(ZayraDbContext db, Guid tenantId, ILetterService letters)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("employee_id", EmployeeId.ToString()),
            new("permission", "ess.read"),
        };
        // The leave/attendance/letter-issuer services are not reached by the payslip endpoints.
        var controller = new EmployeeSelfServiceController(db, letters, new PdfRenderGate(1), null!, null!, null!, new NullDocumentStorage());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    private static PayrollController Hr(ZayraDbContext db, Guid tenantId, ILetterService? letters = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Payroll Officer"),
            new Claim(ClaimTypes.Role, "Payroll Officer"),
            new Claim("permission", "payroll.write"),
            new Claim("permission", "payroll.export"),
        }, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };
        var ctrl = new PayrollController(
            db, new _PecUnrestrictedScope(), new _PecHttpAccessor(httpCtx), new _PecNullNotifications(),
            new _PecNullPackResolver(), new StubRuleReader(), letters ?? new CapturingLetters(),
            new NullDocumentStorage(), new PdfRenderGate(1));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private sealed class CapturingLetters : ILetterService
    {
        public PayslipData? Last { get; private set; }
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) { Last = d; return Task.FromResult(new byte[] { 1 }); }
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _PecUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _PecHttpAccessor : IHttpContextAccessor
{
    public _PecHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _PecNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _PecNullPackResolver : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
