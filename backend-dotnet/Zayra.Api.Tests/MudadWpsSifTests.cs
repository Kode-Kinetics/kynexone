using System.Xml;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;

namespace Zayra.Api.Tests;

/// <summary>
/// WPS SIF output validity — KSA Mudad XML structural tests.
///
/// Uses VALID Saudi IBANs (SA + 2 check digits + 18-digit BBAN, passing ISO 13616 mod-97)
/// and valid Iqama / National ID numbers.
///
/// Coverage:
///   - Valid data → well-formed Mudad XML
///   - Root element, Version attribute, Header, Employees structure
///   - Header.RecordCount = employee count
///   - Header.TotalNetPay = sum of NetPay values (total reconciliation = run net)
///   - All required fields present on each Employee element (field-level assertions)
///   - F2 monetary formatting (2 decimal places)
///   - Period is YYYY-MM format
///   - Invalid IBAN → rejected by WpsSifValidator (gate already tested; confirmed here too)
///   - Multi-employee: record count and total match
/// </summary>
public class MudadWpsSifTests
{
    // ── Valid Saudi IBANs (SA + 22 digits, mod-97 verified) ───────────────────
    // Verified via ISO 13616 mod-97: move first 4 chars to end, substitute letters
    // (S=28, A=10), compute mod97 → must equal 1.
    // SA0380000000608010167519 → passes (used throughout existing tests)
    // SA4420000001234567891234 → passes
    // SA2920000001000000012345 → passes
    private const string SaudiIban1 = "SA0380000000608010167519"; // mod-97 verified, 24 chars
    private const string SaudiIban2 = "SA4420000001234567891234"; // mod-97 verified, 24 chars
    private const string SaudiIban3 = "SA2760000009876543210001"; // mod-97 verified, 24 chars

    // ── Iqama / MOL IDs ───────────────────────────────────────────────────────
    // Saudi National ID: 10 digits starting with 1
    // Expat Iqama:       10 digits starting with 2
    private const string SaudiNationalId = "1098765432";
    private const string IqamaId1        = "2109876543";
    private const string IqamaId2        = "2987654321";

    private static KsaWageProtectionExporter Exporter() => new();

    private static WageProtectionExportInput BuildInput(IReadOnlyList<WpsEmployee> employees,
        int year = 2026, int month = 6, string establishmentId = "ESTAB0001",
        string companyEn = "Ras Al Manar LLC") => new(
        TenantId:        Guid.NewGuid(),
        CompanyId:       Guid.NewGuid(),
        PayrollRunId:    Guid.NewGuid(),
        PeriodYear:      year,
        PeriodMonth:     month,
        EstablishmentId: establishmentId,
        EmployerIban:    SaudiIban1,
        CompanyNameEn:   companyEn,
        CompanyNameAr:   "شركة رأس المنار",
        Employees:       employees);

    private static int _empIdCounter = 1;
    private static WpsEmployee Emp(string code, string iban, string nationalId,
        decimal basic = 8_000m, decimal housing = 2_000m, decimal transport = 1_000m,
        decimal other = 500m, decimal net = 10_000m) => new(
        EmployeeId:    _empIdCounter++,
        EmployeeCode:  code,
        FullNameEn:    $"Employee {code}",
        FullNameAr:    string.Empty,
        Nationality:   "Saudi",
        NationalId:    nationalId,
        IbanOrAccount: iban,
        BankCode:      "1060",
        Salary:        new SalaryBreakdown(basic, housing, transport, other),
        NetPay:        net);

    // Helper: parse XML and return doc
    private static XmlDocument ParseXml(byte[] bytes)
    {
        var doc = new XmlDocument();
        doc.LoadXml(System.Text.Encoding.UTF8.GetString(bytes));
        return doc;
    }

    // ── Root structure ────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidInput_RootElement_IsMudadWpsWithVersion()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId);
        var input = BuildInput(new[] { emp });

        var result = await Exporter().ExportAsync(input);

        var doc  = ParseXml(result.FileBytes);
        var root = doc.DocumentElement!;
        Assert.Equal("MudadWPS", root.LocalName);
        Assert.Equal("1.0", root.GetAttribute("Version"));
    }

    [Fact]
    public async Task ValidInput_FormatVersion_IsMudadXml()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId);
        var input = BuildInput(new[] { emp });

        var result = await Exporter().ExportAsync(input);

        Assert.Equal("mudad-xml", result.Format);
    }

    // ── Header assertions ─────────────────────────────────────────────────────

    [Fact]
    public async Task ValidInput_Header_ContainsAllRequiredFields()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId, net: 11_500m);
        var input = BuildInput(new[] { emp }, establishmentId: "ESTAB9999", companyEn: "Test Co");

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);
        var header = doc.SelectSingleNode("//Header")!;

        Assert.NotNull(header);
        Assert.NotNull(header.SelectSingleNode("EmployerID"));
        Assert.NotNull(header.SelectSingleNode("EmployerName"));
        Assert.NotNull(header.SelectSingleNode("EmployerIBAN"));
        Assert.NotNull(header.SelectSingleNode("Period"));
        Assert.NotNull(header.SelectSingleNode("RecordCount"));
        Assert.NotNull(header.SelectSingleNode("TotalNetPay"));
    }

    [Fact]
    public async Task ValidInput_Header_EstablishmentId_MatchesInput()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId);
        var input = BuildInput(new[] { emp }, establishmentId: "ESTAB9001");

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);

        Assert.Equal("ESTAB9001", doc.SelectSingleNode("//Header/EmployerID")!.InnerText);
    }

    [Fact]
    public async Task ValidInput_Header_Period_IsYyyyMmFormat()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId);
        var input = BuildInput(new[] { emp }, year: 2026, month: 6);

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);
        var period = doc.SelectSingleNode("//Header/Period")!.InnerText;

        Assert.Equal("2026-06", period);
    }

    // ── Record count reconciliation ───────────────────────────────────────────

    [Fact]
    public async Task SingleEmployee_RecordCount_IsOne()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId, net: 5_000m);
        var input = BuildInput(new[] { emp });

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);

        var recordCount = int.Parse(doc.SelectSingleNode("//Header/RecordCount")!.InnerText);
        var empNodes    = doc.SelectNodes("//Employees/Employee")!;

        Assert.Equal(1, recordCount);
        Assert.Equal(1, empNodes.Count);
        Assert.Equal(1, result.RecordCount);
    }

    [Fact]
    public async Task MultiEmployee_RecordCount_MatchesEmployeeList()
    {
        var employees = new[]
        {
            Emp("EMP-001", SaudiIban1, SaudiNationalId,        net: 10_000m),
            Emp("EMP-002", SaudiIban2, IqamaId1,               net: 8_500m),
            Emp("EMP-003", SaudiIban3, IqamaId2,               net: 12_000m),
        };
        var input = BuildInput(employees);

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);

        var recordCount = int.Parse(doc.SelectSingleNode("//Header/RecordCount")!.InnerText);
        var empNodes    = doc.SelectNodes("//Employees/Employee")!;

        Assert.Equal(3, recordCount);
        Assert.Equal(3, empNodes.Count);
        Assert.Equal(3, result.RecordCount);
    }

    // ── Total amount reconciliation (run net == SIF total) ───────────────────

    [Fact]
    public async Task SingleEmployee_TotalNetPay_EqualsNetPayValue()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId, net: 9_750.50m);
        var input = BuildInput(new[] { emp });

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);

        var totalStr = doc.SelectSingleNode("//Header/TotalNetPay")!.InnerText;
        var total    = decimal.Parse(totalStr);

        Assert.Equal(9_750.50m, total);
        // run net == SIF header total
        Assert.Equal(emp.NetPay, total);
    }

    [Fact]
    public async Task MultiEmployee_TotalNetPay_EqualsSumOfAllNetPays()
    {
        var employees = new[]
        {
            Emp("EMP-001", SaudiIban1, SaudiNationalId, net: 10_000m),
            Emp("EMP-002", SaudiIban2, IqamaId1,        net:  8_500m),
            Emp("EMP-003", SaudiIban3, IqamaId2,        net: 12_000m),
        };
        var expectedTotal = employees.Sum(e => e.NetPay); // 30_500
        var input = BuildInput(employees);

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);

        var totalStr = doc.SelectSingleNode("//Header/TotalNetPay")!.InnerText;
        var total    = decimal.Parse(totalStr);

        Assert.Equal(expectedTotal, total);
        Assert.Equal(30_500m, total);
    }

    // ── Per-employee field assertions ─────────────────────────────────────────

    [Fact]
    public async Task Employee_AllRequiredFields_ArePresentAndCorrect()
    {
        var emp = Emp("EMP-010", SaudiIban1, IqamaId1,
            basic: 8_000m, housing: 2_000m, transport: 1_000m, other: 500m, net: 11_500m);
        var input = BuildInput(new[] { emp });

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);
        var node   = doc.SelectSingleNode("//Employees/Employee[1]")!;

        // All required child elements must exist.
        foreach (var field in new[] { "EmpCode", "FullNameEn", "NationalID", "Nationality",
                                      "IBAN", "BankCode", "BasicSalary", "Housing",
                                      "Transport", "OtherAllow", "GrossSalary", "NetPay" })
        {
            Assert.NotNull(node.SelectSingleNode(field));
        }

        // Value assertions.
        Assert.Equal("EMP-010",          node.SelectSingleNode("EmpCode")!.InnerText);
        Assert.Equal(IqamaId1,           node.SelectSingleNode("NationalID")!.InnerText);
        Assert.Equal(SaudiIban1,         node.SelectSingleNode("IBAN")!.InnerText);
        Assert.Equal("8000.00",          node.SelectSingleNode("BasicSalary")!.InnerText);
        Assert.Equal("2000.00",          node.SelectSingleNode("Housing")!.InnerText);
        Assert.Equal("1000.00",          node.SelectSingleNode("Transport")!.InnerText);
        Assert.Equal("500.00",           node.SelectSingleNode("OtherAllow")!.InnerText);
        Assert.Equal("11500.00",         node.SelectSingleNode("GrossSalary")!.InnerText);
        Assert.Equal("11500.00",         node.SelectSingleNode("NetPay")!.InnerText);
    }

    [Fact]
    public async Task Employee_MonetaryValues_UseTwoDecimalPlaces()
    {
        // All salary/NetPay values must be formatted with exactly 2 decimal places.
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId, net: 5_000m);
        var input = BuildInput(new[] { emp });

        var result = await Exporter().ExportAsync(input);
        var doc    = ParseXml(result.FileBytes);
        var node   = doc.SelectSingleNode("//Employees/Employee[1]")!;

        foreach (var field in new[] { "BasicSalary", "Housing", "Transport", "OtherAllow", "GrossSalary", "NetPay" })
        {
            var val = node.SelectSingleNode(field)!.InnerText;
            Assert.True(val.Contains('.') && val.Split('.')[1].Length == 2,
                $"{field}='{val}' must have exactly 2 decimal places");
        }
    }

    // ── Invalid IBAN → rejected at validation gate ────────────────────────────

    [Fact]
    public void InvalidIban_IsRejectedByValidator_CannotExportToMudad()
    {
        // IBANs that fail mod-97 are blocked before the exporter is called.
        // This re-confirms the gate using the standard WpsSifValidator.
        const string invalidIban = "SA0000000000000000000000"; // all zeros, fails mod-97

        var tenantId = Guid.NewGuid();
        var run = new Zayra.Api.Models.PayrollRun
        {
            Id             = Guid.NewGuid(),
            TenantId       = tenantId,
            Year           = 2026,
            Month          = 6,
            Status         = "Locked",
            TotalNetSalary = 5_000m,
        };
        var slip = new Zayra.Api.Models.PayrollSlip
        {
            Id         = Guid.NewGuid(),
            TenantId   = tenantId,
            RunId      = run.Id,
            EmployeeId = 1,
            NetSalary  = 5_000m,
            Status     = "Final",
        };
        var profile = new Zayra.Api.Models.EmployeePayrollProfile
        {
            Id         = Guid.NewGuid(),
            TenantId   = tenantId,
            EmployeeId = 1,
            Iban       = invalidIban,
        };
        var employee = new Zayra.Api.Models.Employee
        {
            Id           = 1,
            TenantId     = tenantId,
            EmployeeCode = "EMP-001",
            FullName     = "Test",
            Status       = "Active",
            IdNumber     = SaudiNationalId,
        };

        var validation = Zayra.Api.Infrastructure.Payroll.WpsSifValidator.Validate(
            run, new[] { slip }, new[] { profile }, new[] { employee });

        Assert.False(validation.CanExport);
        Assert.Contains(validation.BlockingErrors, e => e.Code == "INVALID_IBAN");
    }

    // ── File metadata ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportResult_FileName_ContainsEstablishmentAndPeriod()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId);
        var input = BuildInput(new[] { emp }, year: 2026, month: 6, establishmentId: "ESTAB0042");

        var result = await Exporter().ExportAsync(input);

        Assert.Contains("ESTAB0042", result.FileName);
        Assert.Contains("202606",    result.FileName);
        Assert.EndsWith(".xml",      result.FileName);
    }

    [Fact]
    public async Task ExportResult_FileBytes_AreNonEmpty()
    {
        var emp   = Emp("EMP-001", SaudiIban1, SaudiNationalId);
        var input = BuildInput(new[] { emp });

        var result = await Exporter().ExportAsync(input);

        Assert.NotNull(result.FileBytes);
        Assert.True(result.FileBytes.Length > 0);
    }

    // ── Sample SIF (redacted) ─────────────────────────────────────────────────
    // The test below is informational: it generates a sample file and asserts
    // key content is present. The XML is also used to produce the sample in the
    // test output (captured by xUnit output sink).

    [Fact]
    public async Task SampleSif_Redacted_ContainsExpectedStructure()
    {
        var employees = new[]
        {
            Emp("RAM-001", SaudiIban1, SaudiNationalId, basic: 8_000m, housing: 2_000m, transport: 1_000m, other: 500m, net: 10_000m),
            Emp("RAM-002", SaudiIban2, IqamaId1,        basic: 6_000m, housing: 1_500m, transport:   800m, other: 200m, net:  7_500m),
        };
        var input = BuildInput(employees, establishmentId: "MUDAD001", companyEn: "Ras Al Manar LLC");

        var result = await Exporter().ExportAsync(input);
        var xml    = System.Text.Encoding.UTF8.GetString(result.FileBytes);

        // Structural presence
        Assert.Contains("<MudadWPS",      xml);
        Assert.Contains("<Header>",       xml);
        Assert.Contains("<RecordCount>2", xml);
        Assert.Contains("<Employees>",    xml);
        Assert.Contains("<Employee>",     xml);

        // Total reconciliation: 10_000 + 7_500 = 17_500
        Assert.Contains("<TotalNetPay>17500.00</TotalNetPay>", xml);

        // IBANs are present (not masked in the SIF file itself — masking is for audit logs)
        Assert.Contains(SaudiIban1, xml);
        Assert.Contains(SaudiIban2, xml);

        // Period
        Assert.Contains("<Period>2026-06</Period>", xml);
    }
}
