using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Stores every raw clock event exactly as received from a device, mobile app, import or correction, written once and never edited. @tier:T @owner:HR @retention:24m-purge
/// </summary>
public partial class AttendancePunch
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? DeviceId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string Direction { get; set; } = null!;

    public string Source { get; set; } = null!;

    public DateTime OccurredAt { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public string? ExternalId { get; set; }

    public string IdempotencyKey { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
