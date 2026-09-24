using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Setup;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Infrastructure.Setup;

/// <summary>
/// The handful of statutory figures the setup assistant needs, read through
/// <see cref="IStatutoryRuleReader"/> — the same reader the payroll run uses.
///
/// <para>This deliberately does not query <c>statutory_rules</c> itself. The reader already
/// resolves the two scopes correctly (platform defaults under a null tenant, plus this tenant's
/// own overrides ranked above them) and is the one place authorised to bypass the query filter to
/// do it. Going direct would have added a raw bypass the ratchet exists to prevent, and would have
/// quietly ignored a tenant that had already customised its overtime rate — drafting the platform
/// default over a figure that tenant had deliberately changed.</para>
/// </summary>
public sealed class SetupStatutoryDefaults : ISetupStatutoryDefaults
{
    private readonly IStatutoryRuleReader _reader;

    public SetupStatutoryDefaults(IStatutoryRuleReader reader) => _reader = reader;

    /// <summary>The jurisdiction a starter configuration is drafted for. A tenant in a financial
    /// free zone (DIFC/ADGM) is on a different labour law, and that is not a choice this form
    /// collects — so only mainland rules are read, and a free-zone tenant edits the draft.</summary>
    private static string? MainlandFor(string iso3) => iso3 switch
    {
        CountryCodes.Saudi => Jurisdictions.KsaMainland,
        CountryCodes.UAE => Jurisdictions.UAEMainland,
        CountryCodes.Qatar => Jurisdictions.QatarMainland,
        _ => null,
    };

    /// <summary>
    /// Exactly the keys the assistant consults, and no others. A draft is built from a fixed set of
    /// figures, so this is a fixed set of lookups rather than a table scan — and the list doubles
    /// as the record of what a starter configuration is allowed to be influenced by.
    /// </summary>
    private static readonly string[] Keys =
    {
        "leave.annual_base_days",
        "leave.sick_band1_days",
        OvertimeStatutoryContextResolver.RuleStandardMultiplier,
        OvertimeStatutoryContextResolver.RuleRestDayMultiplier,
        OvertimeStatutoryContextResolver.RuleHolidayMultiplier,
        OvertimeStatutoryContextResolver.RuleStandardMonthlyHours,
    };

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(string iso3, Guid tenantId, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var jurisdiction = MainlandFor(iso3);
        // No mainland jurisdiction means no seeded pack for this country, so there is nothing to
        // read. The assistant already knows how to say "no built-in figure" and leave the field at
        // zero, which is what an empty result produces.
        if (jurisdiction is null) return found;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var key in Keys)
        {
            var value = await _reader.GetStringAsync(iso3, jurisdiction, key, today, tenantId, ct);
            if (!string.IsNullOrWhiteSpace(value)) found[key] = value;
        }
        return found;
    }
}
