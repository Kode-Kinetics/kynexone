using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>The three states a create-company wizard draft can be in. The CHECK on the column is this list.</summary>
public static class CompanySetupDraftStatuses
{
    public const string Open = "Open";
    public const string Submitted = "Submitted";
    public const string Discarded = "Discarded";
    public static readonly string[] All = [Open, Submitted, Discarded];
}

/// <summary>
/// A server-side draft of the create-company wizard (design contract docs/design/company-setup-wizard.md,
/// Decision 9). It holds what the user has typed so far, so a draft survives sign-out and a change of device,
/// and it never writes a business row: the company and its setup are created only by the commit, which reads
/// this row inside the tenant setup lock.
///
/// <para><b>One fact, one place.</b> <see cref="Body"/> is the wizard's own shape (camelCase JSON of
/// <c>CompanySetupDraftBody</c>) and nobody queries, joins or reports on it — what was CREATED lives in the
/// business tables and the audit rows. <see cref="ResultCompanyId"/> plus <see cref="Status"/> =
/// <c>Submitted</c> is the idempotency witness: a retried commit carrying this draft id finds them under the
/// lock and replays the first result instead of creating a second company.</para>
/// </summary>
public class CompanySetupDraftRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Status { get; set; } = CompanySetupDraftStatuses.Open;
    /// <summary>The wizard step the user was on (1-7). Resume opens here.</summary>
    public short Step { get; set; } = 1;
    /// <summary>Optimistic concurrency: every save names the version it read; a stale save is a 409.</summary>
    public int Version { get; set; } = 1;
    /// <summary>The draft body as JSON (<c>jsonb</c>, at most 128 KiB by CHECK).</summary>
    public string Body { get; set; } = "{}";
    /// <summary>The company the commit created. Set with <see cref="Status"/> = Submitted.</summary>
    public Guid? ResultCompanyId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAtUtc { get; set; }
}
