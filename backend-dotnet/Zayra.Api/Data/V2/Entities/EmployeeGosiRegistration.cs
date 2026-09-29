using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Tracks the effective-dated GOSI registration of an employee against an establishment, including the contributory wage GOSI itself holds on file, which is what a filing variance is explained against. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class EmployeeGosiRegistration
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid EmployeeId { get; set; }

    public string GosiRegistrationNo { get; set; } = null!;

    public string? GosiEmployeeNo { get; set; }

    public string Status { get; set; } = null!;

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string? OccupationCode { get; set; }

    public DateOnly? RegisteredOn { get; set; }

    public DateOnly? DeregisteredOn { get; set; }

    public decimal? RegisteredContributoryWage { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
