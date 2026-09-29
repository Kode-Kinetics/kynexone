using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Records an overtime claim in minutes for one local working day with the hourly rate, statutory multiplier and amount frozen at the moment it was calculated. @tier:T @owner:HR @retention:84m-keep
/// </summary>
public partial class OvertimeRequest
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? StatutoryRuleId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string OtType { get; set; } = null!;

    public string Payout { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateOnly WorkDate { get; set; }

    public int OvertimeMinutes { get; set; }

    public string? Reason { get; set; }

    public decimal BasicHourlyRate { get; set; }

    public decimal Multiplier { get; set; }

    public decimal Amount { get; set; }

    public string? RulesVersion { get; set; }

    public DateTime? DecidedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
