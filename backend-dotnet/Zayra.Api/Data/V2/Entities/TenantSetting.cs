using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The single settings row per tenant, holding every configuration section as versioned JSON so two admins editing different sections never clobber each other. @tier:T @owner:Platform
/// </summary>
public partial class TenantSetting
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// Keys: general, hr, payroll, localization, branding, security, lookups, leave, loans, overtime, notification_templates, document_requirements, help_texts. Written with jsonb_set on one key, guarded by that key&apos;s section_versions entry (§A).
    /// </summary>
    public string Sections { get; set; } = null!;

    public string SectionVersions { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
