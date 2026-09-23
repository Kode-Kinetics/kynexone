using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Seeds the MHRSD Nitaqat colour bands as Saudization percentage ranges per economic activity, establishment size tier and published grid version. @tier:R @owner:Compliance @retention:indefinite-keep
/// </summary>
public partial class NitaqatGrid
{
    public Guid Id { get; set; }

    public string ActivityCode { get; set; } = null!;

    public string? ActivityNameEn { get; set; }

    public string? ActivityNameAr { get; set; }

    public string SizeTier { get; set; } = null!;

    public string Band { get; set; } = null!;

    public string GridVersion { get; set; } = null!;

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public int HeadcountMin { get; set; }

    public int? HeadcountMax { get; set; }

    public decimal MinSaudizationPct { get; set; }

    public decimal? MaxSaudizationPct { get; set; }

    public string? SourceReference { get; set; }

    public DateTime CreatedAt { get; set; }
}
