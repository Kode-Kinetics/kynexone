using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The ASP.NET Data Protection key ring whose keys encrypt MFA secrets and single-use tokens at rest. @tier:P @owner:Platform @retention:Keep
/// </summary>
public partial class DataProtectionKey
{
    public Guid Id { get; set; }

    public string FriendlyName { get; set; } = null!;

    /// <summary>
    /// Secret column: REVOKE from kynex_ro by column privilege (§19.2).
    /// </summary>
    public string Xml { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
