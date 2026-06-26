using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class ShiftDefinition : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int BreakMinutes { get; set; } = 60;
    public string Color { get; set; } = "#2F6BFF";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class ShiftAssignment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public Guid ShiftDefinitionId { get; set; }
    public string ShiftName { get; set; } = string.Empty;
    public string ShiftCode { get; set; } = string.Empty;
    public string ShiftColor { get; set; } = string.Empty;
    public DateOnly AssignedDate { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-tenant rostering policy that drives the intelligent (Ollama-assisted) roster planner.
/// Complex rule sets are stored as JSON strings so the schema stays stable as rules evolve.
/// Exactly one row per tenant (upserted).
/// </summary>
public class ShiftPolicy : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>JSON: [{ "gender": "Female", "shiftCodes": ["MORNING"], "mode": "required" }, ...]. mode = required|preferred.</summary>
    public string GenderShiftRulesJson { get; set; } = "[]";

    /// <summary>JSON: ["AFTERNOON"] — shifts that are opt-in/voluntary rather than auto-assigned.</summary>
    public string VoluntaryShiftCodesJson { get; set; } = "[]";

    /// <summary>JSON: [{ "shiftCode": "MORNING", "headcount": 2 }, ...] — required coverage on weekends.</summary>
    public string WeekendDemandJson { get; set; } = "[]";

    /// <summary>JSON: [{ "shiftCode": "MORNING", "headcount": 1 }, ...] — required coverage on public holidays.</summary>
    public string HolidayDemandJson { get; set; } = "[]";

    /// <summary>Minimum rest hours between two assigned shifts for the same employee.</summary>
    public int MinRestHours { get; set; } = 8;

    /// <summary>Maximum consecutive days an employee may be rostered before a day off is forced.</summary>
    public int MaxConsecutiveDays { get; set; } = 6;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
