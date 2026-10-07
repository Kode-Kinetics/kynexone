using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// An IBAN (or other identity/banking number) never reaches a persisted or user-facing string in full:
/// validation findings, entry-time errors and history values name it by its last 4 characters only.
/// </summary>
public class SensitiveValueInMessagesTests
{
    private const string BadIban = "SA0480000000608010167519"; // fails mod-97 (valid check digits are 03)

    [Theory]
    [InlineData(BadIban, "***7519", "wrong checksum")]
    [InlineData("SA038000000060801016751", "***6751", "a Saudi IBAN has exactly 24")]
    [InlineData("INVALID-IBAN", "***IBAN", "characters other than letters and digits")]
    [InlineData("GB29NWBK601613319268", "***9268", "wrong checksum")]
    public void Describe_NamesTheIbanByItsLastFour_AndSaysWhy(string iban, string mask, string reason)
    {
        var text = IbanValidator.Describe(iban);

        text.Should().StartWith($"IBAN {mask} is invalid: ").And.Contain(reason);
        text.Should().NotContain(iban);
    }

    [Fact]
    public void InvalidReason_IsNullForAValidIban()
    {
        IbanValidator.InvalidReason("SA0380000000608010167519").Should().BeNull();
        IbanValidator.InvalidReason("GB29NWBK60161331926819").Should().BeNull();
    }

    [Fact]
    public void ValidationEngine_InvalidIbanFinding_CarriesOnlyTheLastFour()
    {
        var tid = Guid.NewGuid();
        var run = new PayrollRun { Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6, Status = "Processed" };
        var emp = new Employee { Id = 1, TenantId = tid, EmployeeCode = "EMP001", FullName = "E", Nationality = "Indian", Status = "Active" };
        var slip = new PayrollSlip
        {
            Id = Guid.NewGuid(), TenantId = tid, RunId = run.Id, EmployeeId = 1, EmployeeCode = "EMP001",
            GrossSalary = 10_000m, NetSalary = 10_000m, BasicSalary = 10_000m,
        };
        var profile = new EmployeePayrollProfile { TenantId = tid, EmployeeId = 1, Iban = BadIban, MolId = "MOL1", SalaryCurrency = "SAR" };
        var salary = new EmployeeSalaryStructure { TenantId = tid, EmployeeId = 1, SalaryStructureId = Guid.NewGuid(), BasicSalary = 10_000m, IsActive = true };
        var company = new Company { Id = Guid.NewGuid(), TenantId = tid, LegalNameEn = "Co", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };

        var results = PayrollValidationEngine.Run(new PayrollValidationContext(run, [slip], [emp], [salary], [profile], [], [], company));

        results.Should().ContainSingle(r => r.Code == "INVALID_IBAN")
            .Which.Message.Should().Be("Employee EMP001: IBAN ***7519 is invalid: wrong checksum (ISO 13616 mod-97). " +
                                       "Correct the IBAN before approving this run.");
        results.Should().NotContain(r => r.Message.Contains(BadIban));
    }

    [Theory]
    [InlineData("IBAN")]       // a legacy system's column name, taken verbatim by the migration import
    [InlineData("BankIban")]
    [InlineData("Iban")]
    [InlineData("IqamaNumber")]
    [InlineData("Iqama")]
    public void SanitizeFieldValue_MasksIdentityAndBankingFields_ByAnyCommonName(string field)
    {
        EmployeeSafeSnapshot.SanitizeFieldValue(field, BadIban).Should().Be("***7519");
    }

    [Theory]
    [InlineData("Salary")]
    [InlineData("BasicSalary")]
    public void SanitizeFieldValue_NeverStoresASalaryValue(string field)
    {
        EmployeeSafeSnapshot.SanitizeFieldValue(field, "12500").Should().NotContain("12500");
    }

    [Fact]
    public void SanitizeFieldValue_LeavesOrdinaryFieldsAlone()
    {
        EmployeeSafeSnapshot.SanitizeFieldValue("Designation", "Manager").Should().Be("Manager");
    }

    [Fact]
    public void QiwaHttpClient_DoesNotLogRequestUris()
    {
        // GET /establishments/{id}/employees/{nationalIdOrIqama}: IHttpClientFactory's default logging handler
        // writes that URI to the app log at Information. The named client must opt out.
        var program = File.ReadAllText(Path.Combine(SourceScan.ResolveApiRoot(), "Program.cs"));
        program.Should().MatchRegex(@"AddHttpClient\(""qiwa""[^;]*\.RemoveAllLoggers\(\)\s*;");
    }

    [Fact]
    public async Task MigrationImport_EmployeeHistory_StoresSensitiveValuesMasked()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        db.Employees.Add(new Employee { TenantId = tenantId, EmployeeCode = "EMP-001", FullName = "Imported", Status = EmployeeStatuses.Active });
        await db.SaveChangesAsync();
        var controller = new MigrationImportController(db, new Pbkdf2PasswordHasher(), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Admin"),
                    }, "test")),
                },
            },
        };
        const string oldIban = "SA0380000000608010167519";
        var request = new MigrationPackageRequest("history-mask-001", new Dictionary<string, string>
        {
            ["employeeHistory"] =
                "EmployeeCode,EventType,FieldName,OldValue,NewValue,EffectiveDate,Reason,SourceSystem,SourceRecordId\n" +
                $"EMP-001,BankChange,IBAN,{oldIban},{BadIban},2026-01-01,Legacy bank change,Workday,HIST-1\n" +
                "EMP-001,IdChange,IqamaNumber,2111111111,2222222222,2026-01-02,Legacy iqama renewal,Workday,HIST-2\n" +
                "EMP-001,JobChange,Designation,Associate,Manager,2026-01-03,Legacy promotion,Workday,HIST-3\n",
        }, false);

        var result = await controller.Commit(request, CancellationToken.None);

        Assert.Equal("Completed", Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Status);
        var rows = await db.EmployeeHistories.Where(h => h.TenantId == tenantId).ToDictionaryAsync(h => h.FieldName);
        rows["IBAN"].OldValue.Should().Be("***7519");
        rows["IBAN"].NewValue.Should().Be("***7519");
        rows["IqamaNumber"].OldValue.Should().Be("***1111");
        rows["IqamaNumber"].NewValue.Should().Be("***2222");
        rows["Designation"].NewValue.Should().Be("Manager", "non-sensitive history keeps its value");
        rows.Values.Should().NotContain(h => h.OldValue.Contains(oldIban) || h.NewValue.Contains(BadIban)
                                             || h.OldValue.Contains("2111111111") || h.NewValue.Contains("2222222222"));
    }
}
