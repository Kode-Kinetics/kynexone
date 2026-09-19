using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests;

public class SundayKsaDemoFixtureSeederTests
{
    private static SundayKsaDemoFixtureSeeder.GuardInput ValidGuard() => new(
        "Staging", false, false, SundayKsaDemoFixtureSeeder.Confirmation, true, "PreviewOnly@2026!");

    private static SundayKsaDemoFixtureSeeder.FixtureShape ValidShape() => new(
        SundayKsaDemoFixtureSeeder.ExpectedEmployees,
        SundayKsaDemoFixtureSeeder.ExpectedBranches,
        SundayKsaDemoFixtureSeeder.ExpectedDepartments,
        SundayKsaDemoFixtureSeeder.ExpectedEmployeeCodes(),
        SundayKsaDemoFixtureSeeder.ExpectedPersonaSignatures(),
        SundayKsaDemoFixtureSeeder.ExpectedAttendanceSignatures(),
        SundayKsaDemoFixtureSeeder.ExpectedLeaveSignatures(),
        SundayKsaDemoFixtureSeeder.ExpectedPayrollSignatures(),
        SundayKsaDemoFixtureSeeder.ExpectedPayrollEmployees,
        SundayKsaDemoFixtureSeeder.ExpectedGross,
        SundayKsaDemoFixtureSeeder.ExpectedDeductions,
        SundayKsaDemoFixtureSeeder.ExpectedNet,
        SundayKsaDemoFixtureSeeder.ExpectedEmployerStatutory);

    [Fact]
    public void Guard_accepts_explicit_disposable_non_client_preview()
    {
        var act = () => SundayKsaDemoFixtureSeeder.ValidateGuard(ValidGuard());
        act.Should().NotThrow();
    }

    [Fact]
    public void Guard_refuses_production()
    {
        var input = ValidGuard() with { EnvironmentName = "Production" };
        var act = () => SundayKsaDemoFixtureSeeder.ValidateGuard(input);
        act.Should().Throw<InvalidOperationException>().WithMessage("*refused in Production*");
    }

    [Fact]
    public void Guard_refuses_dedicated_deployment()
    {
        var act = () => SundayKsaDemoFixtureSeeder.ValidateGuard(ValidGuard() with { DedicatedDeployment = true });
        act.Should().Throw<InvalidOperationException>().WithMessage("*dedicated*");
    }

    [Fact]
    public void Guard_refuses_client_deployment()
    {
        var act = () => SundayKsaDemoFixtureSeeder.ValidateGuard(ValidGuard() with { ClientDeployment = true });
        act.Should().Throw<InvalidOperationException>().WithMessage("*client*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("create-kx-sun-ksa-20260920-v1")]
    [InlineData("CREATE-KX-SUN-KSA-20260920-v2")]
    public void Guard_requires_exact_case_sensitive_confirmation(string? confirmation)
    {
        var act = () => SundayKsaDemoFixtureSeeder.ValidateGuard(ValidGuard() with { ConfirmationValue = confirmation });
        act.Should().Throw<InvalidOperationException>().WithMessage("*exact*");
    }

    [Fact]
    public void Guard_requires_disposable_acknowledgement()
    {
        var act = () => SundayKsaDemoFixtureSeeder.ValidateGuard(ValidGuard() with { DisposableAcknowledged = false });
        act.Should().Throw<InvalidOperationException>().WithMessage("*DISPOSABLE=true*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Welcome@123")]
    [InlineData("12345678901")]
    public void Guard_requires_twelve_character_fixture_password(string? password)
    {
        var act = () => SundayKsaDemoFixtureSeeder.ValidateGuard(ValidGuard() with { Password = password });
        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 12*");
    }

    [Fact]
    public void Canonical_shape_has_no_drift()
    {
        SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape()).Should().BeEmpty();
    }

    [Fact]
    public void Payroll_control_totals_balance_exactly()
    {
        (SundayKsaDemoFixtureSeeder.ExpectedGross - SundayKsaDemoFixtureSeeder.ExpectedDeductions)
            .Should().Be(SundayKsaDemoFixtureSeeder.ExpectedNet);
        SundayKsaDemoFixtureSeeder.ExpectedEmployerStatutory.Should().Be(3_666.25m);
    }

    [Fact]
    public void EveryDeterministicEmployeeIban_IsAValid24CharacterSaudiIban()
    {
        var ibans = Enumerable.Range(0, SundayKsaDemoFixtureSeeder.ExpectedEmployees)
            .Select(SundayKsaDemoFixtureSeeder.BuildSaudiIban)
            .ToList();

        ibans.Should().OnlyHaveUniqueItems();
        ibans.Should().OnlyContain(iban => iban.Length == 24 && IbanValidator.IsSaudiIban(iban));
    }

    [Fact]
    public void Same_count_persona_substitution_is_drift()
    {
        var personas = SundayKsaDemoFixtureSeeder.ExpectedPersonaSignatures();
        personas[0] = "admin@kx-sunday.demo|Auditor";
        SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape() with { PersonaSignatures = personas })
            .Should().ContainSingle(x => x.StartsWith("personas drift"));
    }

    [Fact]
    public void Same_count_attendance_status_change_is_drift()
    {
        var attendance = SundayKsaDemoFixtureSeeder.ExpectedAttendanceSignatures();
        attendance[0] = attendance[0].Replace("|Present", "|Absent", StringComparison.Ordinal);
        SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape() with { AttendanceSignatures = attendance })
            .Should().ContainSingle(x => x.StartsWith("attendance drift"));
    }

    [Fact]
    public void Same_count_leave_status_change_is_drift()
    {
        var leave = SundayKsaDemoFixtureSeeder.ExpectedLeaveSignatures();
        leave[0] = "KXS-006|Approved";
        SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape() with { LeaveSignatures = leave })
            .Should().ContainSingle(x => x.StartsWith("leave drift"));
    }

    [Fact]
    public void Same_count_payroll_amount_change_is_drift()
    {
        var errors = SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape() with
        {
            Gross = SundayKsaDemoFixtureSeeder.ExpectedGross + 1m,
            Net = SundayKsaDemoFixtureSeeder.ExpectedNet + 1m,
        });
        errors.Should().Contain(x => x.StartsWith("payroll gross="));
        errors.Should().Contain(x => x.StartsWith("payroll net="));
    }

    [Fact]
    public void Same_count_payroll_employee_detail_change_is_drift()
    {
        var payroll = SundayKsaDemoFixtureSeeder.ExpectedPayrollSignatures();
        payroll[0] = "KXS-001|14260.00|1047.64|13212.36|Approved";
        SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape() with { PayrollSignatures = payroll })
            .Should().ContainSingle(x => x.StartsWith("payroll drift"));
    }

    [Fact]
    public void Same_count_employee_code_substitution_is_drift()
    {
        var codes = SundayKsaDemoFixtureSeeder.ExpectedEmployeeCodes();
        codes[0] = "KXS-999";
        SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape() with { EmployeeCodes = codes })
            .Should().ContainSingle(x => x.StartsWith("employee codes drift"));
    }

    [Fact]
    public void Payroll_employee_count_is_fail_closed()
    {
        SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape() with { PayrollEmployees = 2 })
            .Should().ContainSingle(x => x.StartsWith("payroll employees="));
    }

    [Fact]
    public void Branch_and_department_counts_are_fail_closed()
    {
        var errors = SundayKsaDemoFixtureSeeder.ValidateShape(ValidShape() with { Branches = 3, Departments = 4 });
        errors.Should().Contain(x => x.StartsWith("branches="));
        errors.Should().Contain(x => x.StartsWith("departments="));
    }
}
