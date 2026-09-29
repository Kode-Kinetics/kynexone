using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Named non-working days per calendar code, with the platform KSA calendar carried as the tenant_id IS NULL rows that every tenant session can read. @tier:R/T @owner:HR
/// </summary>
public partial class PublicHoliday
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public string CalendarCode { get; set; } = null!;

    /// <summary>
    /// A local Gregorian date. Hijri occasions are named in `name` but never stored as Hijri — Hijri is always derived (§13.4).
    /// </summary>
    public DateOnly HolidayDate { get; set; }

    public string Name { get; set; } = null!;

    public bool IsPaid { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
