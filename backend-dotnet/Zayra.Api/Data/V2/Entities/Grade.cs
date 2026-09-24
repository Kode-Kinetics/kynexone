using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Salary grades with their basic-pay band and an optional component pay scale, used to validate and default an employee&apos;s salary structure. @tier:T @owner:HR
/// </summary>
public partial class Grade
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public decimal? MinBasic { get; set; }

    public decimal? MaxBasic { get; set; }

    public string? PayScale { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
