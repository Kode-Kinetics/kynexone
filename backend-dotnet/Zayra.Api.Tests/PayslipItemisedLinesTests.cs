// PayslipItemisedLinesTests.cs
// ─────────────────────────────────────────────────────────────────────────────
// REGRESSION (demo rehearsal): the admin payslip PDF built its lines from the slip HEADER buckets, so
// overtime printed as "Other Allowances" and the late/early deduction plus loss of pay printed as one
// "Other Deductions". The persisted per-line rows (PayrollEarning / PayrollDeduction, written at Process
// and already the source of the ESS payslip) carry the names. The PDF — single download AND the bulk
// bundle — must itemise from them, lifting each itemised amount out of its bucket so the page still adds
// up to the slip exactly, and keeping "Other …" only for money no persisted row explains.
// Also pinned: the designation/department the PDF prints, and masking of every new line.
// SQLite in-memory: no Docker, no network.
// ─────────────────────────────────────────────────────────────────────────────

using System.IO.Compression;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using UglyToad.PdfPig;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class PayslipItemisedLinesTests
{
    private const int EmployeeId = 11;
    private const string OtName = "Overtime (5.00 h × (62.50 + 62.50 × 0.50)/h)";
    private const string LopName = "Loss of Pay (1.00 d × 1,000.00/d)";

    private static (ZayraDbContext Db, SqliteConnection Conn) CreateDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    /// <summary>The rehearsal slip: basic 10,000, housing 2,500, transport 500, other 200, overtime 1,406.25, arrears 300;
    /// GOSI 900 + 75, late/early 937.50, LOP 1,000, loan 500. Gross 14,906.25, deductions 3,412.50, net 11,493.75.</summary>
    private sealed record Fixture(Guid TenantId, Guid RunId, Guid PayslipId, PayrollSlip Slip);

    private static Fixture Seed(ZayraDbContext db, Guid tenantId, decimal unexplainedAllowance = 0m, decimal unexplainedDeduction = 0m,
        string slipDepartment = "Administration", string employeeDepartment = "Administration", string employeeDesignation = "Site Engineer",
        Guid? designationId = null)
    {
        var run = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 11, Status = "Locked", RunType = "Regular" };
        db.PayrollRuns.Add(run);
        db.Employees.Add(new Employee
        {
            Id = EmployeeId, TenantId = tenantId, EmployeeCode = "DEMO-F11", FullName = "Fahad Demo",
            Department = employeeDepartment, Designation = employeeDesignation, DesignationId = designationId, Status = "Active",
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = EmployeeId, SalaryCurrency = "SAR" });

        decimal gross = 10_000m + 2_500m + 500m + 200m + 1_406.25m + 300m + unexplainedAllowance;
        decimal deductions = 900m + 75m + 937.50m + 1_000m + 500m + unexplainedDeduction;
        var slip = new PayrollSlip
        {
            TenantId = tenantId, RunId = run.Id, EmployeeId = EmployeeId, EmployeeCode = "DEMO-F11", EmployeeName = "Fahad Demo",
            Department = slipDepartment,
            BasicSalary = 10_000m, HousingAllowance = 2_500m, TransportAllowance = 500m,
            OtherAllowances = 200m + 1_406.25m + 300m + unexplainedAllowance,   // the header bucket INCLUDES overtime and arrears
            ArrearsAmount = 300m, GrossSalary = gross,
            Deductions = deductions, NetSalary = gross - deductions, LoanDeductions = 500m,
            EmployeeStatutoryTotal = 975m, Status = "Final",
        };
        db.PayrollSlips.Add(slip);

        void Earn(string code, string name, decimal amount, string source = "Salary") =>
            db.PayrollEarnings.Add(new PayrollEarning { TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = EmployeeId, ComponentCode = code, ComponentName = name, Amount = amount, Source = source });
        void Ded(string code, string name, decimal amount, string source, bool employer = false) =>
            db.PayrollDeductions.Add(new PayrollDeduction { TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = EmployeeId, ComponentCode = code, ComponentName = name, Amount = amount, Source = source, IsEmployerContribution = employer });

        Earn("BASIC", "Basic salary", 10_000m);
        Earn("HOUSING", "Housing allowance", 2_500m);
        Earn("TRANSPORT", "Transport allowance", 500m);
        Earn("OTHER_ALLOWANCES", "Other allowances", 200m);
        Earn("OVERTIME", OtName, 1_406.25m, "Overtime");
        Earn("ARREARS_BASIC", "Arrears — Basic salary (2026-10)", 300m, "Arrears");
        db.PayrollArrearsLines.Add(new PayrollArrearsLine
        {
            TenantId = tenantId, EmployeeId = EmployeeId, EmployeeCode = "DEMO-F11", PayrollRunId = run.Id,
            CoveredYear = 2026, CoveredMonth = 10, ComponentCode = PayrollArrearsComponents.Basic, Amount = 300m,
        });
        Ded("GOSI-ANN-EE", "GOSI Annuities (Employee)", 900m, "Statutory");
        Ded("GOSI-SANED-EE", "SANED (Employee)", 75m, "Statutory");
        Ded("GOSI-ANN-ER", "GOSI Annuities (Employer)", 900m, "Statutory", employer: true);
        Ded("ATTENDANCE", "Late/early attendance deduction", 937.50m, "Attendance");
        Ded("LOP_DEDUCTION", LopName, 1_000m, "Attendance");
        Ded("LOAN_EMI", "Loan instalment", 500m, "Loan");
        var payslip = new Payslip { TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = EmployeeId, PayslipNumber = "PS-DEMO-F11" };
        db.Payslips.Add(payslip);
        db.SaveChanges();
        return new Fixture(tenantId, run.Id, payslip.Id, slip);
    }

    private static async Task<PayslipData> SinglePdf(ZayraDbContext db, Fixture f, bool canSeeSalary = true)
    {
        var letters = new CapturingLetters();
        (await Hr(db, f.TenantId, letters, canSeeSalary).DownloadSlipPdf(f.PayslipId, CancellationToken.None)).Should().BeOfType<FileContentResult>();
        return letters.Last!;
    }

    private static async Task<PayslipData> BulkPdf(ZayraDbContext db, Fixture f, bool canSeeSalary = true)
    {
        var letters = new CapturingLetters();
        var result = await Hr(db, f.TenantId, letters, canSeeSalary).DownloadRunPdfBundle(f.RunId, CancellationToken.None);
        var file = result.Should().BeOfType<FileContentResult>().Subject;
        using var zip = new ZipArchive(new MemoryStream(file.FileContents));
        zip.Entries.Should().HaveCount(1);
        return letters.Last!;
    }

    public static IEnumerable<object[]> BothPaths() => new[] { new object[] { false }, new object[] { true } };

    private static Task<PayslipData> Pdf(ZayraDbContext db, Fixture f, bool bulk, bool canSeeSalary = true) =>
        bulk ? BulkPdf(db, f, canSeeSalary) : SinglePdf(db, f, canSeeSalary);

    private static void AssertTotalsMatchSlip(PayslipData pdf, PayrollSlip slip)
    {
        pdf.Items.Where(i => i.Type == "Earning").Sum(i => i.Amount).Should().Be(slip.GrossSalary, "the earning lines add up to the slip's gross");
        pdf.Items.Where(i => i.Type == "Deduction").Sum(i => i.Amount).Should().Be(slip.Deductions, "the deduction lines add up to the slip's total deductions");
        pdf.Items.Single(i => i.Type == "Net").Amount.Should().Be(slip.NetSalary);
    }

    // ── 1. Itemised from persisted rows ───────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task Overtime_late_early_and_loss_of_pay_are_their_own_lines_and_the_page_still_adds_up(bool bulk)
    {
        var (db, conn) = CreateDb(); using var _ = conn;
        var f = Seed(db, Guid.NewGuid());

        var pdf = await Pdf(db, f, bulk);

        var earnings = pdf.Items.Where(i => i.Type == "Earning").ToDictionary(i => i.Name, i => i.Amount);
        earnings[OtName].Should().Be(1_406.25m, "overtime is its own earning, not part of Other Allowances");
        earnings["Other Allowances"].Should().Be(200m, "the residual is only what the other-allowances row explains");
        earnings.Should().ContainKey("Arrears — Basic salary (2026-10)");

        var deductions = pdf.Items.Where(i => i.Type == "Deduction").ToDictionary(i => i.Name, i => i.Amount);
        deductions["Late/early attendance deduction"].Should().Be(937.50m);
        deductions[LopName].Should().Be(1_000m, "loss of pay keeps its days and rate in the label");
        deductions["Loan instalment"].Should().Be(500m);
        deductions["GOSI Annuities (Employee)"].Should().Be(900m);
        deductions["SANED (Employee)"].Should().Be(75m);
        deductions.Keys.Should().NotContain("Other Deductions", "every deduction is explained by a persisted row");
        deductions.Keys.Should().NotContain(n => n.Contains("Employer"), "an employer contribution is not taken from the employee");

        AssertTotalsMatchSlip(pdf, f.Slip);
    }

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task Other_lines_remain_only_for_amounts_no_persisted_row_explains(bool bulk)
    {
        var (db, conn) = CreateDb(); using var _ = conn;
        var f = Seed(db, Guid.NewGuid(), unexplainedAllowance: 80m, unexplainedDeduction: 50m);

        var pdf = await Pdf(db, f, bulk);

        pdf.Items.Single(i => i.Name == "Other Allowances").Amount.Should().Be(280m, "200 from its row + 80 no row explains");
        pdf.Items.Single(i => i.Name == "Other Deductions").Amount.Should().Be(50m, "only the 50 no row explains");
        pdf.Items.Single(i => i.Name == OtName).Amount.Should().Be(1_406.25m);
        AssertTotalsMatchSlip(pdf, f.Slip);
    }

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task Persisted_rows_that_exceed_a_bucket_never_inflate_the_page(bool bulk)
    {
        var (db, conn) = CreateDb(); using var _ = conn;
        var f = Seed(db, Guid.NewGuid());
        // A corrupt run: rows claim more deductions than the slip header carries.
        db.PayrollDeductions.Add(new PayrollDeduction { TenantId = f.TenantId, PayrollRunId = f.RunId, EmployeeId = EmployeeId, ComponentCode = "LEAVE", ComponentName = "Leave deduction", Amount = 9_999m, Source = "Leave" });
        db.SaveChanges();

        var pdf = await Pdf(db, f, bulk);

        AssertTotalsMatchSlip(pdf, f.Slip);
    }

    // ── 3. Masking ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task A_caller_without_salary_visibility_sees_every_new_line_at_zero(bool bulk)
    {
        var (db, conn) = CreateDb(); using var _ = conn;
        var f = Seed(db, Guid.NewGuid(), unexplainedAllowance: 80m, unexplainedDeduction: 50m);

        var pdf = await Pdf(db, f, bulk, canSeeSalary: false);

        // The rate working in a line's name ("5.00 h × (62.50 + ...)/h", "1.00 d × 1,000.00/d") is pay data too: masked, it goes.
        pdf.Items.Should().Contain(i => i.Name == "Overtime" && i.Type == "Earning");
        pdf.Items.Should().Contain(i => i.Name == "Loss of Pay");
        pdf.Items.Should().Contain(i => i.Name == "Late/early attendance deduction");
        pdf.Items.Should().Contain(i => i.Name == "Loan instalment");
        pdf.Items.Should().Contain(i => i.Name == "GOSI Annuities (Employee)", "a label that carries no figure keeps its brackets");
        // The arrears line keeps its covered month ("(2026-10)"): a period, not a rate.
        pdf.Items.Should().NotContain(i => i.Name.Contains('×') || i.Name.Contains("/d)") || i.Name.Contains("/h)") || i.Name.Contains('%'),
            "no rate or day count survives the mask");
        pdf.Items.Should().OnlyContain(i => i.Amount == 0m, "masking applies to every line, new or old");
    }

    // ── 1b. Arabic labels ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OtName, "العمل الإضافي \u2066(5.00 h × (62.50 + 62.50 × 0.50)/h)\u2069")]
    [InlineData("Late/early attendance deduction", "خصم التأخير والخروج المبكر")]
    [InlineData(LopName, "خصم أيام الغياب \u2066(1.00 d × 1,000.00/d)\u2069")]
    [InlineData("Leave deduction", "خصم الإجازة")]
    [InlineData("Loan instalment", "قسط قرض")]
    [InlineData("Salary advance repayment", "سداد السلفة")]
    [InlineData("Fixed deduction", "استقطاع ثابت")]
    [InlineData("Income tax (5%)", "ضريبة الدخل \u2066(5%)\u2069")]
    [InlineData("Other allowances", "بدلات أخرى")]
    public void New_fixed_lines_have_Arabic_labels(string english, string arabic)
        => PayslipArabic.ItemName(english, null).Should().Be(arabic);

    // ── 2. Designation and department ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task Designation_falls_back_to_the_linked_designation_title_when_the_text_column_is_blank(bool bulk)
    {
        var (db, conn) = CreateDb(); using var _ = conn;
        var tenantId = Guid.NewGuid();
        var designation = new Designation { TenantId = tenantId, Code = "SE", TitleEn = "Site Engineer", TitleAr = "مهندس موقع" };
        db.Designations.Add(designation);
        db.SaveChanges();
        var f = Seed(db, tenantId, employeeDesignation: "", designationId: designation.Id);

        var pdf = await Pdf(db, f, bulk);

        pdf.Designation.Should().Be("Site Engineer");
    }

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task Department_is_the_one_frozen_on_the_slip_not_the_employees_current_one(bool bulk)
    {
        var (db, conn) = CreateDb(); using var _ = conn;
        var f = Seed(db, Guid.NewGuid(), slipDepartment: "Administration", employeeDepartment: "Executive Office");

        var pdf = await Pdf(db, f, bulk);

        pdf.Department.Should().Be("Administration", "a payslip shows the department the employee had in that pay period");
    }

    // ── Rendered page (set PAYSLIP_SAMPLE_DIR to keep the PDFs for a visual check) ─────────────────

    [Theory]
    [InlineData("en")]
    [InlineData("bilingual")]
    [InlineData("ar")]
    public async Task The_rendered_page_carries_the_itemised_lines_in_every_locale(string locale)
    {
        var (db, conn) = CreateDb(); using var _ = conn;
        var f = Seed(db, Guid.NewGuid());
        var data = (await SinglePdf(db, f)) with { Branding = new PayslipBrandingConfig(Locale: locale) };

        var pdf = await new LetterService().GeneratePayslipPdfAsync(data);

        var dir = Environment.GetEnvironmentVariable("PAYSLIP_SAMPLE_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"itemised-{locale}.pdf"), pdf);
        }
        using var document = PdfDocument.Open(pdf);
        var text = string.Concat(document.GetPages().Select(p => p.Text)).Replace(" ", string.Empty);
        if (locale != "ar")
        {
            // The bilingual page prints "English / Arabic", so only the English half is contiguous with its own words.
            text.Should().Contain("Overtime").And.Contain("Late/earlyattendancededuction").And.Contain("LossofPay").And.Contain("Loaninstalment");
            text.Should().NotContain("OtherDeductions");
        }
        text.Should().Contain("1,406.25").And.Contain("1,000.00").And.Contain("937.50");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    private static PayrollController Hr(ZayraDbContext db, Guid tenantId, ILetterService letters, bool canSeeSalary)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Payroll Officer"),
            new(ClaimTypes.Role, "Payroll Officer"),
            new("permission", "payroll.write"),
        };
        if (canSeeSalary) claims.Add(new("permission", "payroll.export"));
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(
            db, new _PilScope(), new _PilHttpAccessor(httpCtx), new _PilNotifications(),
            new _PilPackResolver(), new StubRuleReader(), letters,
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

file sealed class _PilScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _PilHttpAccessor : IHttpContextAccessor
{
    public _PilHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _PilNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _PilPackResolver : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
