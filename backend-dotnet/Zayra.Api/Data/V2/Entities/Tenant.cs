using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Customer account root: identity, plan gating, seat and module limits, timezone anchor and the tenancy soft-delete/purge lifecycle. @tier:P @owner:Platform @retention:3-months-after-SoftDelete-then-Purge
/// </summary>
public partial class Tenant
{
    public Guid Id { get; set; }

    public string Slug { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>
    /// IANA id, validated against the runtime tz database on write. Anchors every business day (§13.4).
    /// </summary>
    public string TimezoneId { get; set; } = null!;

    public string? PlanCode { get; set; }

    public DateTime? PlanExpiresAt { get; set; }

    public List<string> EnabledModules { get; set; } = null!;

    /// <summary>
    /// Authoritative seat/module limits (max_employees, max_users, max_companies). Nothing caches a seat count (§11.6).
    /// </summary>
    public string PlanLimits { get; set; } = null!;

    public DateTime? SoftDeletedAt { get; set; }

    public DateTime? PurgedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
