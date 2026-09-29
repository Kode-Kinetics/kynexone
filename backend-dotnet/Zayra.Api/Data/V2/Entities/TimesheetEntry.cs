using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Logs minutes worked on one local day against a cost centre, project and task, and is the only place the project and client dimension of time is captured. @tier:C @owner:HR @retention:24m-purge
/// </summary>
public partial class TimesheetEntry
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TimesheetId { get; set; }

    public Guid? CostCenterId { get; set; }

    public DateOnly WorkDate { get; set; }

    public int Minutes { get; set; }

    public string? ProjectCode { get; set; }

    public string? Task { get; set; }

    public bool Billable { get; set; }

    public string? RateSource { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
