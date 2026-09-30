using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using P = Zayra.Api.Infrastructure.Payroll.PayrollPaymentPrerequisites;

namespace Zayra.Api.Tests;

/// <summary>
/// The pure evaluator behind GET /api/payroll/readiness → paymentPrerequisites. Each code mirrors the
/// run validation's severity: Errors (and bank-file refusals) are blocking, Warnings are recommended.
/// </summary>
public class PayrollPaymentPrerequisitesTests
{
    private const string SaudiIban = "SA0380000000608010167519";
    private const string UaeIban = "AE070331234567890123456";

    private static Company Ksa(string wps = "7001234567", string currency = "SAR") => new()
    {
        LegalNameEn = "KSA Co", CountryCode = "SA", DefaultCurrency = currency, WpsEmployerId = wps, IsActive = true,
    };

    private static Employee Emp(int id, Company? company, string nationality = "Saudi") => new()
    {
        Id = id, EmployeeCode = $"E{id:000}", FullName = $"Employee {id}", Status = "Active",
        CompanyId = company?.Id, Nationality = nationality,
    };

    private static EmployeePayrollProfile Profile(int employeeId, string iban = SaudiIban, string molId = "MOL") => new()
    {
        EmployeeId = employeeId, Iban = iban, MolId = molId,
    };

    private static PayrollPaymentPrerequisitesDto Evaluate(
        IEnumerable<Employee> employees, IEnumerable<Company> companies, IEnumerable<EmployeePayrollProfile> profiles,
        IEnumerable<int>? payBlocked = null, IEnumerable<int>? attended = null, bool attendanceChecked = true,
        IEnumerable<int>? withSalary = null, int limit = P.DefaultEmployeeListLimit)
    {
        var list = employees.ToList();
        return P.Evaluate(new P.Input(
            list,
            companies.ToList(),
            (withSalary ?? list.Select(e => e.Id)).ToHashSet(),
            profiles.ToList(),
            (payBlocked ?? Array.Empty<int>()).ToHashSet(),
            attendanceChecked ? (attended ?? list.Select(e => e.Id)).ToHashSet() : null,
            limit));
    }

    [Fact]
    public void FullyPrepared_Employees_ProduceNothing()
    {
        var co = Ksa();
        var result = Evaluate(new[] { Emp(1, co), Emp(2, co) }, new[] { co }, new[] { Profile(1), Profile(2) });

        result.BlockedEmployees.Should().Be(0);
        result.EmployeesWithRecommendations.Should().Be(0);
        result.Employees.Should().BeEmpty();
        result.CompanyBlocking.Should().BeEmpty();
        result.CompanyRecommended.Should().BeEmpty();
    }

    [Fact]
    public void Iban_Errors_Block_While_A_Foreign_Iban_On_A_Saudi_Run_Is_Only_Recommended()
    {
        var co = Ksa();
        var result = Evaluate(
            new[] { Emp(1, co), Emp(2, co), Emp(3, co) },
            new[] { co },
            new[] { Profile(1, iban: "SA00BROKEN"), Profile(2, iban: UaeIban), Profile(3, iban: " ") });

        result.Employees.Single(e => e.EmployeeId == 1).Blocking.Should().Equal(P.InvalidIban);
        result.Employees.Single(e => e.EmployeeId == 2).Blocking.Should().BeEmpty();
        result.Employees.Single(e => e.EmployeeId == 2).Recommended.Should().Equal(P.NonSaudiIban);
        result.Employees.Single(e => e.EmployeeId == 3).Blocking.Should().Equal(P.MissingIban);
        result.BlockedEmployees.Should().Be(2);
    }

    [Fact]
    public void Saudi_Only_Checks_Do_Not_Apply_To_A_Uae_Company()
    {
        var uae = new Company { LegalNameEn = "UAE Co", CountryCode = "AE", DefaultCurrency = "AED", IsActive = true };
        var result = Evaluate(new[] { Emp(1, uae, "Emirati") }, new[] { uae }, new[] { Profile(1, iban: UaeIban, molId: "") });

        result.Employees.Should().BeEmpty("MOL ID, Saudi IBAN and the WPS employer ID are KSA rules");
        result.CompanyRecommended.Should().BeEmpty();
    }

    [Fact]
    public void Readiness_PayBlock_Nationality_And_Attendance_Land_In_The_Right_List()
    {
        var co = Ksa();
        var result = Evaluate(
            new[] { Emp(1, co), Emp(2, co, nationality: "") },
            new[] { co },
            new[] { Profile(1), Profile(2) },
            payBlocked: new[] { 1 },
            attended: new[] { 1 });

        result.Employees.Single(e => e.EmployeeId == 1).Blocking.Should().Equal(P.ReadinessPayBlocked);
        result.Employees.Single(e => e.EmployeeId == 2).Blocking.Should().BeEmpty();
        result.Employees.Single(e => e.EmployeeId == 2).Recommended.Should().BeEquivalentTo(new[] { P.MissingNationality, P.NoAttendance });
        result.AttendanceChecked.Should().BeTrue();
    }

    [Fact]
    public void A_Period_That_Has_Not_Started_Does_Not_Report_Missing_Attendance()
    {
        var co = Ksa();
        var result = Evaluate(new[] { Emp(1, co) }, new[] { co }, new[] { Profile(1) }, attendanceChecked: false);

        result.AttendanceChecked.Should().BeFalse();
        result.Employees.Should().BeEmpty();
    }

    [Fact]
    public void Company_Gaps_Are_Separate_From_Employee_Gaps()
    {
        var noWps = Ksa(wps: "");
        var noCurrency = new Company { LegalNameEn = "No Currency Co", CountryCode = "SA", DefaultCurrency = "", WpsEmployerId = "1", IsActive = true };
        var result = Evaluate(
            new[] { Emp(1, noWps), Emp(2, noCurrency) },
            new[] { noWps, noCurrency },
            new[] { Profile(1), Profile(2) });

        result.Employees.Should().BeEmpty();
        result.CompanyRecommended.Should().ContainSingle(c => c.Code == P.WpsEmployerIdMissing && c.CompanyId == noWps.Id);
        result.CompanyBlocking.Should().ContainSingle(c => c.Code == P.CompanyCurrencyMissing && c.CompanyId == noCurrency.Id);
    }

    [Fact]
    public void Employees_Without_An_Active_Company_Are_Named_In_A_Multi_Company_Tenant()
    {
        var a = Ksa();
        var b = Ksa();
        var orphan = Emp(3, company: null);
        var inactive = Emp(4, company: null);
        inactive.CompanyId = Guid.NewGuid(); // points at a company that is not active
        var result = Evaluate(new[] { Emp(1, a), Emp(2, b), orphan, inactive }, new[] { a, b }, new[] { Profile(1), Profile(2), Profile(3), Profile(4) });

        var item = result.CompanyBlocking.Should().ContainSingle(c => c.Code == P.CompanyNotResolved).Subject;
        item.Label.Should().StartWith("2 active employee(s)");
    }

    [Fact]
    public void A_Legacy_Employee_Without_A_Company_Belongs_To_The_Only_Company()
    {
        var co = Ksa(wps: "");
        var result = Evaluate(new[] { Emp(1, company: null) }, new[] { co }, new[] { Profile(1, molId: "") });

        result.CompanyBlocking.Should().BeEmpty();
        result.Employees.Single().Recommended.Should().Equal(P.MissingMolId);
        result.CompanyRecommended.Should().ContainSingle(c => c.Code == P.WpsEmployerIdMissing);
    }

    [Fact]
    public void The_List_Is_Capped_But_The_Counts_Are_Not()
    {
        var co = Ksa();
        var employees = Enumerable.Range(1, 5).Select(i => Emp(i, co)).ToList();
        // Employee 5 only has a recommendation; 1-4 are blocked for want of a salary.
        var result = Evaluate(employees, new[] { co }, employees.Select(e => Profile(e.Id, molId: e.Id == 5 ? "" : "MOL")),
            withSalary: new[] { 5 }, limit: 3);

        result.BlockedEmployees.Should().Be(4);
        result.Blocking.Should().ContainSingle(b => b.Code == P.MissingSalary && b.Count == 4);
        result.Employees.Should().HaveCount(3);
        result.Employees.Should().OnlyContain(e => e.Blocking.Count > 0, "blocked employees are listed first");
        result.EmployeesTruncated.Should().BeTrue();
    }

    [Fact]
    public void Every_Code_Has_Plain_Language_And_A_Next_Action()
    {
        foreach (var code in new[]
                 {
                     P.MissingSalary, P.MissingPayrollProfile, P.MissingIban, P.InvalidIban, P.ReadinessPayBlocked,
                     P.CompanyCountryMissing, P.CompanyCurrencyMissing, P.CompanyNotResolved, P.MissingMolId,
                     P.NonSaudiIban, P.MissingNationality, P.NoAttendance, P.WpsEmployerIdMissing,
                 })
        {
            var (label, next) = P.Describe(code);
            label.Should().NotBe(code, $"{code} needs a plain-language label");
            next.Should().NotBeNullOrWhiteSpace();
        }
    }
}
