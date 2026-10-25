using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Provisioning;

/// <summary>What the installation's organisation is born with.</summary>
public sealed record OrganisationProvisioningRequest
{
    public string? OrganisationName { get; init; }
    /// <summary>The first company's registered legal name; defaults to the organisation name.</summary>
    public string? LegalName { get; init; }
    public string? HomeCountry { get; init; }
    public string? AdminEmail { get; init; }
    public string? AdminFullName { get; init; }
    public string? AdminPassword { get; init; }
}

public sealed record ProvisionedOrganisation(Guid OrganisationId, string Name, Guid AdminUserId, string AdminEmail);

public sealed record OrganisationProvisioningResult(ProvisionedOrganisation? Organisation, string? Refusal)
{
    public bool Succeeded => Organisation is not null;
    internal static OrganisationProvisioningResult Ok(ProvisionedOrganisation organisation) => new(organisation, null);
    internal static OrganisationProvisioningResult Refused(string reason) => new(null, reason);
}

public interface IOrganisationProvisioningService
{
    /// <summary>
    /// Validates the request and creates the installation's one organisation, its first company and
    /// its administrator, atomically. Refuses when an organisation already exists.
    /// </summary>
    /// <param name="actorName">Who ran the setup, for the audit rows.</param>
    Task<OrganisationProvisioningResult> ProvisionAsync(OrganisationProvisioningRequest req, string actorName, CancellationToken ct);
}

/// <summary>
/// THE one door through which the organisation is created (docs/DATA_ENTRY_PATHS.md,
/// OrganisationCreationDoorTests). Called once per installation by <c>--setup-organisation</c>.
/// </summary>
/// <remarks>
/// The steps and their order are exactly those the retired platform console ran for a new tenant:
/// the organisation row, its localisation (country, operating currency and TIME ZONE from the home
/// country), the first company, roles, GL defaults, pay components, the provisioning bundle, the
/// administrator, the installation id and the two audit rows — in one transaction. Commercial limits come from
/// the licence imported afterwards (<c>--import-licence</c>), never from a row written here.
/// Until the database restructure the organisation is still stored as the single <c>tenants</c> row.
/// </remarks>
public sealed class OrganisationProvisioningService : IOrganisationProvisioningService
{
    /// <summary>Serialises setups on one database; see <see cref="ProvisionCoreAsync"/>.</summary>
    internal const string SetupLockKey = "kynexone:organisation-setup";

    /// <summary>
    /// The slug the single organisation is stored under. Nothing reads it once logins stop asking for
    /// a workspace (WP4); until then it is what a login request names.
    /// </summary>
    public const string OrganisationSlug = "main";

    /// <summary>The audit actor of the setup command when no better identity is known.</summary>
    public const string SetupActor = "installation_setup";

    private readonly ZayraDbContext _db;
    private readonly IAuthSeeder _authSeeder;
    private readonly IPasswordHasher _passwordHasher;

    public OrganisationProvisioningService(ZayraDbContext db, IAuthSeeder authSeeder, IPasswordHasher passwordHasher)
    {
        _db = db;
        _authSeeder = authSeeder;
        _passwordHasher = passwordHasher;
    }

    /// <summary>
    /// The currency an organisation in <paramref name="homeCountry"/> pays its staff in, from the one
    /// country list the product knows. A recognised country missing from that list keeps the previous
    /// entity default (USD), which Setup → Companies and organisation settings can correct.
    /// </summary>
    public static string HomeCurrencyFor(string homeCountry) =>
        IsoReference.Countries.FirstOrDefault(c => string.Equals(c.Code, homeCountry, StringComparison.OrdinalIgnoreCase))?.Currency
        ?? "USD";

    /// <summary>The home countries a new organisation may have: those with a country pack.</summary>
    public static IReadOnlyList<string> SupportedHomeCountries { get; } = CountryPackRegistry.Available
        .Select(p => HomeJurisdiction.Normalize(p.CountryCode))
        .OfType<string>()
        .ToList();

    /// <summary>
    /// True when the password contains one of the defaults that ship in docker-compose, CI and the docs.
    /// </summary>
    public static bool ContainsKnownDefaultPassword(string? password)
    {
        var pw = password ?? string.Empty;
        return KnownDefaultPasswordFragments.Any(f => pw.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] KnownDefaultPasswordFragments = ["ChangeMe", "YourPassword", "PlatformAdmin123"];

    /// <summary>The request's refusal reason, or null when it may proceed. No database access.</summary>
    public static string? Validate(OrganisationProvisioningRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.OrganisationName))
            return "The organisation name is required.";
        // HOME JURISDICTION — required, and never inferred. Provisioning seeds country payroll rules,
        // leave entitlements and readiness profiles, and the first company inherits this value; a
        // tenant created without it produced a company with CountryCode = "" whose employees could not
        // be added at all, with no message naming the cause.
        var homeCountry = HomeJurisdiction.Normalize(req.HomeCountry);
        if (homeCountry is null)
            return string.IsNullOrWhiteSpace(req.HomeCountry)
                ? "A home country is required. Use an ISO 3166-1 code (e.g. SA, AE, QA)."
                : $"Unrecognized home country '{req.HomeCountry}'. Use an ISO 3166-1 code (e.g. SA, AE, QA).";
        if (!SupportedHomeCountries.Contains(homeCountry))
            return $"There is no country pack for '{homeCountry}'. Supported: {string.Join(", ", SupportedHomeCountries)}.";
        if (string.IsNullOrWhiteSpace(req.AdminEmail) || !req.AdminEmail.Contains('@'))
            return "A valid admin email is required.";
        if (string.IsNullOrWhiteSpace(req.AdminPassword) || req.AdminPassword.Length < 10)
            return "The admin password must be at least 10 characters.";
        if (ContainsKnownDefaultPassword(req.AdminPassword))
            return "The admin password is a known default. Choose a password of your own.";
        return null;
    }

    public async Task<OrganisationProvisioningResult> ProvisionAsync(OrganisationProvisioningRequest req, string actorName, CancellationToken ct)
    {
        var refusal = Validate(req);
        if (refusal is not null) return OrganisationProvisioningResult.Refused(refusal);
        var homeCountry = HomeJurisdiction.Normalize(req.HomeCountry)!;

        if (_db.Database.IsRelational())
        {
            // The provisioning bundle performs several SaveChanges calls. Execute all of them inside
            // one retry-aware database transaction so a seeder/config/admin failure cannot leave an
            // organisation shell that cannot be logged into or set up again.
            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                try
                {
                    var result = await ProvisionCoreAsync(req, homeCountry, actorName, ct);
                    if (result.Succeeded) await transaction.CommitAsync(ct);
                    else await transaction.RollbackAsync(ct);
                    return result;
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            });
        }

        return await ProvisionCoreAsync(req, homeCountry, actorName, ct);
    }

    private async Task<OrganisationProvisioningResult> ProvisionCoreAsync(
        OrganisationProvisioningRequest req, string homeCountry, string actorName, CancellationToken ct)
    {
        // ONE ORGANISATION PER INSTALLATION. Checked inside the transaction under a transaction-scoped
        // advisory lock, so two setup runs racing on an empty database serialise here and the second
        // sees the first one's row. Any row counts, active or not: a database that has ever held an
        // organisation is not an empty installation.
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({SetupLockKey}, 0))", ct);
        var existing = await _db.Tenants.AsNoTracking().Select(t => t.Name).ToListAsync(ct);
        if (existing.Count > 0)
            return OrganisationProvisioningResult.Refused(
                $"This installation is already set up (organisation '{string.Join("', '", existing)}').");

        var name = req.OrganisationName!.Trim();
        var legalName = string.IsNullOrWhiteSpace(req.LegalName) ? name : req.LegalName.Trim();

        // The OPERATING currency — the one payroll, bank files, GL and leave encashment are
        // denominated in — follows the home jurisdiction. Left to the entity defaults, the
        // localisation row and the first company were born USD whatever the country, and a Saudi
        // payroll approval then labelled a SAR run as "USD".
        var operatingCurrency = HomeCurrencyFor(homeCountry);

        var organisation = new Tenant
        {
            Name = name,
            Slug = OrganisationSlug,
            AccountType = TenantAccountTypes.SingleCompany,
            CompanyCreationMode = CompanyCreationModes.GroupSelfServiceWithinLimit,
        };
        _db.Tenants.Add(organisation);
        await _db.SaveChangesAsync(ct);

        // The home jurisdiction lives on the localisation row (what TenantModuleService and
        // OvertimeController read as "the country"). The TIMEZONE must be set here too, and this is
        // not optional tidiness: the entity defaults to America/New_York, which put a GCC
        // organisation's "today" 7-11 hours behind — an approved leave starting today was not live on
        // the day it started. Unknown country resolves to UTC rather than a guess.
        _db.TenantLocalizationSettings.Add(new TenantLocalizationSetting
        {
            TenantId = organisation.Id,
            CountryCode = homeCountry,
            CurrencyCode = operatingCurrency,
            DefaultTimezone = HomeJurisdiction.TimeZoneFor(homeCountry),
        });

        // The organisation is born with its first company, inside this transaction (CompanyScopeBackfill
        // only repairs, never creates). It INHERITS the home jurisdiction: a company created with
        // CountryCode = "" left statutory/identity resolution with nothing to key on for every
        // employee. A group can give each further legal entity its own country.
        _db.Companies.Add(new Company
        {
            TenantId = organisation.Id,
            LegalNameEn = legalName,
            TradeName = name,
            CountryCode = homeCountry,
            DefaultCurrency = operatingCurrency,
            IsActive = true,
        });
        await _db.SaveChangesAsync(ct);

        // Full standard RBAC set (Admin → Employee).
        var adminRole = await _authSeeder.EnsureTenantRolesAsync(organisation.Id, ct);
        // GL defaults: chart of accounts, default mappings, the 17 system posting drivers and the
        // client-configurable rate allow-list.
        await GlDriverSeeder.SeedTenantDefaultsAsync(_db, organisation.Id, ct);
        // The data-driven pay-component defaults (the system rows that reproduce the current payslip
        // earning/deduction set), so payroll starts from an editable catalog.
        await PayComponentSeeder.SeedTenantDefaultsAsync(_db, organisation.Id, ct);
        // The idempotent provisioning bundle — country rules, MasterData, HR categories, default
        // attendance/leave/approval policies, bilingual notification templates and reference lists.
        await TenantProvisioningBundle.ProvisionAsync(_db, organisation.Id, homeCountry, ct);
        await _db.SaveChangesAsync(ct);

        var admin = new User
        {
            TenantId = organisation.Id,
            Email = req.AdminEmail!.Trim().ToLowerInvariant(),
            NormalizedEmail = AuthService.Normalize(req.AdminEmail),
            FullName = string.IsNullOrWhiteSpace(req.AdminFullName) ? "Organisation Administrator" : req.AdminFullName.Trim(),
            PasswordHash = _passwordHasher.Hash(req.AdminPassword!),
            AccessMode = "FullPortal",
            Status = "Active",
            IsActive = true,
            IsEmailConfirmed = true,
            IsGroupScope = true
        };
        admin.UserRoles.Add(new UserRole { User = admin, Role = adminRole });
        _db.Users.Add(admin);

        // The installation's identity: a licence may be bound to it, so the client sends it to the vendor.
        // Created here, once, in the same transaction as the organisation.
        if (!await _db.PlatformConfigEntries.AnyAsync(e => e.Key == PlatformConfigKeys.InstallationId, ct))
            _db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = PlatformConfigKeys.InstallationId, Value = Guid.NewGuid().ToString() });

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = organisation.Id,
            EntityType = "Organisation",
            EntityId = organisation.Id.ToString(),
            Action = "OrganisationCreated",
            NewValuesJson = JsonSerializer.Serialize(new
            {
                name = organisation.Name,
                legalName,
                adminEmail = admin.Email,
                // The home jurisdiction decided which statutory defaults were seeded, so it is part of
                // the creation record.
                homeCountryCode = homeCountry,
                operatingCurrency,
            }),
            PerformedByName = actorName,
            IpAddress = ""
        });

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = organisation.Id,
            EntityType = "User",
            EntityId = admin.Id.ToString(),
            Action = "AdminCreated",
            NewValuesJson = JsonSerializer.Serialize(new { email = admin.Email, role = "Admin" }),
            PerformedByName = actorName,
            IpAddress = ""
        });

        await _db.SaveChangesAsync(ct);
        return OrganisationProvisioningResult.Ok(new ProvisionedOrganisation(organisation.Id, organisation.Name, admin.Id, admin.Email));
    }
}
