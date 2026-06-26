namespace Zayra.Api.Application.Shifts;

// ── Inputs ──────────────────────────────────────────────────────────────────

public sealed record RosterPlanEmployee(int Id, string FullName, string Gender, string Department);

public sealed record RosterPlanShift(Guid Id, string Code, string Name, TimeOnly Start, TimeOnly End, string Color);

/// <summary>A gender → allowed shift-codes rule. Mode "required" restricts the gender to exactly
/// these shifts; "preferred" only biases the planner toward them.</summary>
public sealed record GenderShiftRule(string Gender, IReadOnlyList<string> ShiftCodes, string Mode);

public sealed record DemandTarget(string ShiftCode, int Headcount);

public sealed record RosterPlanPolicy(
    IReadOnlyList<GenderShiftRule> GenderRules,
    IReadOnlyList<string> VoluntaryShiftCodes,
    IReadOnlyList<DemandTarget> WeekendDemand,
    IReadOnlyList<DemandTarget> HolidayDemand,
    int MinRestHours,
    int MaxConsecutiveDays);

public sealed record RosterPlanInput(
    DateOnly DateFrom,
    DateOnly DateTo,
    IReadOnlyList<RosterPlanEmployee> Employees,
    IReadOnlyList<RosterPlanShift> Shifts,
    RosterPlanPolicy Policy,
    IReadOnlySet<DateOnly> Holidays,
    IReadOnlySet<DateOnly> WeekendDays);

// ── Outputs ─────────────────────────────────────────────────────────────────

public sealed record ProposedAssignment(
    int EmployeeId,
    string EmployeeName,
    DateOnly Date,
    Guid ShiftDefinitionId,
    string ShiftCode,
    string ShiftName,
    string ShiftColor,
    string Reason);

public sealed record RosterPlanResult(
    IReadOnlyList<ProposedAssignment> Assignments,
    IReadOnlyList<string> Warnings,
    string Engine,
    string Summary);

public interface IRosterPlannerService
{
    /// <summary>Produces a proposed (un-persisted) roster. Uses the configured LLM (e.g. Ollama)
    /// for intelligent planning, then enforces hard constraints deterministically. Never throws on
    /// LLM failure — it falls back to a fully deterministic plan.</summary>
    Task<RosterPlanResult> PlanAsync(RosterPlanInput input, CancellationToken ct);
}
