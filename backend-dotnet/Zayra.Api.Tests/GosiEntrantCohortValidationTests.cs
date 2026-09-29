using FluentAssertions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F02 — the GOSI entrant cohort is judged PER EMPLOYEE, not per run.
///
/// <para>Before: one run-wide WARN_GOSI_ENTRANT_COHORT_NOT_MODELLED said "this run assumes EVERY insured
/// person is on the pre-3-July-2024 schedule", named nobody, blocked nothing, and a single tenant flag
/// (<c>gosi.new_entrant_scheme_acknowledged</c>) silenced it for the whole population. A post-July-2024 new
/// entrant was therefore paid and filed on the wrong schedule with nothing stopping the run.</para>
///
/// <para>Now each Saudi national's slip carries the cohort it was computed on
/// (<see cref="PayrollSlip.GosiCohort"/>), and the validator reads it:</para>
/// <list type="bullet">
/// <item>Unknown → a per-employee WARNING naming the person and the next action;</item>
/// <item>NewEntrant → a per-employee, NON-overridable ERROR, because the new-entrant schedule is not
/// modelled and the pre-reform rate on the slip is wrong for that person;</item>
/// <item>PreJuly2024 → nothing.</item>
/// </list>
/// </summary>
[Trait("Category", "PayrollValidation")]
public class GosiEntrantCohortValidationTests
{
    private const string NotRecorded = "GOSI_COHORT_NOT_RECORDED";
    private const string NewEntrantBlocked = "GOSI_NEW_ENTRANT_SCHEDULE_NOT_MODELLED";

    [Fact]
    public void MixedCohortRun_YieldsPerEmployeeFindings_AndTheNewEntrantBlocks()
    {
        var w = new World();
        var unknown = w.Saudi(1, firstRegisteredOn: null);
        var entrant = w.Saudi(2, firstRegisteredOn: new DateOnly(2025, 2, 1));
        var existing = w.Saudi(3, firstRegisteredOn: new DateOnly(2016, 5, 10));

        var results = PayrollValidationEngine.Run(w.Context());

        // Unknown: a warning that names WHO and the NEXT ACTION.
        var warn = results.Should().ContainSingle(r => r.Code == NotRecorded).Subject;
        warn.Severity.Should().Be("Warning");
        warn.EmployeeId.Should().Be(unknown.Id);
        warn.Message.Should().Contain(unknown.EmployeeCode).And.Contain(unknown.FullName)
            .And.Contain("GOSI cohort not recorded, contribution basis unverified")
            .And.Contain("first-registration date");

        // NewEntrant: a blocking error for that employee.
        var block = results.Should().ContainSingle(r => r.Code == NewEntrantBlocked).Subject;
        block.Severity.Should().Be("Error", "an error is what blocks Approve and Lock");
        block.EmployeeId.Should().Be(entrant.Id);
        block.Message.Should().Contain(entrant.EmployeeCode).And.Contain("2025-02-01")
            .And.Contain("not modelled");

        // PreJuly2024: no cohort finding at all.
        results.Should().NotContain(r => r.EmployeeId == existing.Id && (r.Code == NotRecorded || r.Code == NewEntrantBlocked));

        // And the run-wide, nobody-named warning is gone.
        results.Should().NotContain(r => r.Code == "WARN_GOSI_ENTRANT_COHORT_NOT_MODELLED");
        results.Where(r => r.Code is NotRecorded or NewEntrantBlocked).Should().OnlyContain(r => r.EmployeeId != null);
    }

    [Fact]
    public void TheNewEntrantBlock_CannotBeOverridden()
    {
        PayrollValidationOverridePolicy.IsOverridable(NewEntrantBlocked).Should().BeFalse(
            "overriding it would file a person on a schedule known to be wrong for them");
        PayrollValidationOverridePolicy.NonOverridable.Should().Contain(NewEntrantBlocked,
            "the refusal is published, not an accident of the allow list");
    }

    [Fact]
    public void ASlipProcessedWithoutACohort_IsUnverified_NotAssumedPreReform()
    {
        // Every slip processed before F02 carries GosiCohort = null. The validator must not read null as
        // "existing subscriber" — it is exactly as unknown as a missing date.
        var w = new World();
        var e = w.Saudi(1, firstRegisteredOn: new DateOnly(2016, 5, 10), slipCohortOverride: null, overrideSlipCohort: true);

        var results = PayrollValidationEngine.Run(w.Context());

        results.Should().ContainSingle(r => r.Code == NotRecorded && r.EmployeeId == e.Id)
            .Which.Message.Should().Contain("re-process", "the fix for a stale slip is to recompute it, not only to record a date");
    }

    [Fact]
    public void ExpatriatesAndGccNationals_GetNoCohortFinding()
    {
        // The cohort split is a Saudi-national annuities fact. An expatriate pays no annuities, and a GCC
        // national is insured under their home state's scheme — neither has a Saudi cohort.
        var w = new World();
        w.Other(1, "Egyptian");
        w.Other(2, "Bahraini");

        PayrollValidationEngine.Run(w.Context())
            .Should().NotContain(r => r.Code == NotRecorded || r.Code == NewEntrantBlocked);
    }

    [Fact]
    public void ANonKsaRun_GetsNoCohortFinding()
    {
        var w = new World(countryCode: "AE");
        w.Saudi(1, firstRegisteredOn: new DateOnly(2025, 2, 1));

        PayrollValidationEngine.Run(w.Context())
            .Should().NotContain(r => r.Code == NotRecorded || r.Code == NewEntrantBlocked);
    }

    [Fact]
    public void ASupplementalRunWithNoGosi_DoesNotBlockANewEntrant()
    {
        // A bonus-only run whose earnings sit outside the GOSI base computes no contribution, so there is
        // no wrong rate on it to block. The period's regular run still blocks.
        var w = new World(includesRecurringPay: false);
        w.Saudi(1, firstRegisteredOn: new DateOnly(2025, 2, 1), gosiEe: 0m);

        PayrollValidationEngine.Run(w.Context())
            .Should().NotContain(r => r.Code == NewEntrantBlocked);
    }

    [Fact]
    public void ASupplementalRunThatDeductedGosi_StillBlocksANewEntrant()
    {
        var w = new World(includesRecurringPay: false);
        var e = w.Saudi(1, firstRegisteredOn: new DateOnly(2025, 2, 1), gosiEe: 90m);

        PayrollValidationEngine.Run(w.Context())
            .Should().Contain(r => r.Code == NewEntrantBlocked && r.EmployeeId == e.Id && r.Severity == "Error");
    }

    // ── World ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class World
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly PayrollRun _run;
        private readonly Company _company;
        private readonly List<PayrollSlip> _slips = new();
        private readonly List<Employee> _employees = new();
        private readonly List<EmployeeSalaryStructure> _salaries = new();
        private readonly List<EmployeePayrollProfile> _profiles = new();
        private readonly List<PayrollDeduction> _deductions = new();

        public World(string countryCode = "SA", bool includesRecurringPay = true)
        {
            _company = new Company
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, LegalNameEn = "Cohort Co", CountryCode = countryCode,
                DefaultCurrency = "SAR", IsActive = true,
            };
            _run = new PayrollRun
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, Year = 2026, Month = 9, Status = "Processed",
                CompanyId = _company.Id, IncludesRecurringPay = includesRecurringPay,
            };
        }

        public Employee Saudi(int id, DateOnly? firstRegisteredOn, decimal gosiEe = 877.5m,
            string? slipCohortOverride = null, bool overrideSlipCohort = false)
        {
            var e = Add(id, "Saudi", gosiEe);
            e.GosiFirstRegisteredOn = firstRegisteredOn;
            // The slip carries the cohort Process computed it on — the same resolver Process uses.
            _slips[^1].GosiCohort = overrideSlipCohort ? slipCohortOverride : GosiCohorts.Resolve(firstRegisteredOn);
            return e;
        }

        public Employee Other(int id, string nationality) => Add(id, nationality, gosiEe: 0m);

        private Employee Add(int id, string nationality, decimal gosiEe)
        {
            var e = new Employee
            {
                Id = id, TenantId = _tenantId, EmployeeCode = $"EMP{id:D3}", FullName = $"Employee {id}",
                Nationality = nationality, Status = "Active",
            };
            _employees.Add(e);
            _slips.Add(new PayrollSlip
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, RunId = _run.Id, EmployeeId = id,
                EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName,
                BasicSalary = 7_000m, HousingAllowance = 2_000m, GrossSalary = 9_000m,
                Deductions = gosiEe, NetSalary = 9_000m - gosiEe,
            });
            _salaries.Add(new EmployeeSalaryStructure
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, EmployeeId = id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = 7_000m, IsActive = true,
            });
            _profiles.Add(new EmployeePayrollProfile
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, EmployeeId = id,
                Iban = "SA4420000001234567891234", MolId = "MOL123456", SalaryCurrency = "SAR",
            });
            if (gosiEe > 0m)
                _deductions.Add(new PayrollDeduction
                {
                    Id = Guid.NewGuid(), TenantId = _tenantId, PayrollRunId = _run.Id, EmployeeId = id,
                    ComponentCode = "GOSI-ANN-EE", ComponentName = "GOSI Annuities (Employee)",
                    Amount = gosiEe, Source = "Statutory",
                });
            return e;
        }

        public PayrollValidationContext Context()
        {
            _run.TotalGrossSalary = _slips.Sum(s => s.GrossSalary);
            _run.TotalDeductions = _slips.Sum(s => s.Deductions);
            _run.TotalNetSalary = _slips.Sum(s => s.NetSalary);
            return new PayrollValidationContext(_run, _slips, _employees, _salaries, _profiles, _deductions,
                Array.Empty<PayrollEarning>(), _company);
        }
    }
}
