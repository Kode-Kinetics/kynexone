using System.Text;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Application.Performance;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Application.Leave;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Application.AI;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Boot;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

var builder = WebApplication.CreateBuilder(args);

// ── DI validation at startup (Wave 0 / G5) ───────────────────────────────────
// ValidateOnBuild resolves every registered service AT BOOT, so a missing or mistyped
// registration becomes a failed deploy instead of a 500 the first time some endpoint is
// hit in production. ValidateScopes catches captive dependencies — a Singleton (e.g. a
// hosted worker) capturing a Scoped DbContext, which produces a pooled context shared
// across threads and is the classic source of "random" concurrency corruption.
//
// This is deliberately ON IN EVERY ENVIRONMENT, not just Development. The failure mode it
// guards is precisely what went unnoticed when NotificationService's constructor changed:
// nothing resolved the graph until a request did.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes  = true;
});

// ── P3: JWT audience prod fail-fast ──────────────────────────────────────────
// Dev defaults are intentionally left in appsettings.json for zero-config local dev.
// In Production they MUST be overridden via environment variables (Jwt__TenantAudience,
// Jwt__PlatformAudience). A forgotten env var in prod becomes a failed deploy, not silent drift.
{
    const string DevTenantAudience   = "kynexone-tenant";
    const string DevPlatformAudience = "kynexone-platform";
    const int    MinSigningKeyLength = 64;

    // Enforce in EVERY non-Development environment (Production, Staging, QA…). A non-prod slot that
    // holds real data must not silently run on the committed placeholder key, which would let anyone
    // with the source forge admin JWTs.
    if (!builder.Environment.IsDevelopment())
    {
        var jwtSection     = builder.Configuration.GetSection("Jwt");
        var prodTenantAud  = jwtSection["TenantAudience"];
        var prodPlatformAud = jwtSection["PlatformAudience"];
        var prodSigningKey = jwtSection["SigningKey"];
        var prodErrors     = new List<string>();

        if (string.IsNullOrWhiteSpace(prodTenantAud) || prodTenantAud == DevTenantAudience)
            prodErrors.Add($"Jwt:TenantAudience is null, empty, or still the dev default ('{DevTenantAudience}'). Set Jwt__TenantAudience env var.");
        if (string.IsNullOrWhiteSpace(prodPlatformAud) || prodPlatformAud == DevPlatformAudience)
            prodErrors.Add($"Jwt:PlatformAudience is null, empty, or still the dev default ('{DevPlatformAudience}'). Set Jwt__PlatformAudience env var.");
        if (string.IsNullOrWhiteSpace(prodSigningKey) || prodSigningKey.StartsWith("CHANGE_ME"))
            prodErrors.Add("Jwt:SigningKey is null, empty, or still the placeholder value. Set Jwt__SigningKey env var to a ≥64-char random secret.");
        else if (prodSigningKey.Length < MinSigningKeyLength)
            prodErrors.Add($"Jwt:SigningKey is too short ({prodSigningKey.Length} chars). Use a ≥{MinSigningKeyLength}-char random secret so the HMAC key has adequate entropy.");
        if (prodTenantAud is not null && prodTenantAud == prodPlatformAud)
            prodErrors.Add("Jwt:TenantAudience and Jwt:PlatformAudience must differ — identical audiences collapse the tenant/platform token boundary.");

        if (prodErrors.Count > 0)
            throw new InvalidOperationException(
                $"[{builder.Environment.EnvironmentName}] JWT configuration fail-fast:\n" + string.Join("\n", prodErrors.Select(e => "  " + e)));
    }
}

// Railway injects PORT; fall back to ASPNETCORE_URLS, then local default.
var port = Environment.GetEnvironmentVariable("PORT");
var listenUrl = !string.IsNullOrEmpty(port)
    ? $"http://0.0.0.0:{port}"
    : builder.Configuration["ASPNETCORE_URLS"] ?? "http://0.0.0.0:5117";
builder.WebHost.UseUrls(listenUrl);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<SeedAdminOptions>(builder.Configuration.GetSection("SeedAdmin"));

// ── Seed-admin bootstrap hardening (password fail-fast + demo-seed lock) ──────
// Every fresh database auto-creates a bootstrap admin (SeedAdmin:Email) on first boot. A blank,
// placeholder, or well-known password would ship every client-hosted / dedicated deployment with a
// known-credential admin. A "dedicated" deployment is Production OR anything flagged with
// DEDICATED_DEPLOYMENT / CLIENT_DEPLOYMENT — the SAME predicate the demo-seed gate below uses — so a
// client slot running under a non-Production ASPNETCORE_ENVIRONMENT (Staging/QA/custom) is protected
// too. On a dedicated deployment a weak bootstrap password is REFUSED outright; elsewhere (local dev,
// docker-compose) a working dev default is substituted with a loud warning so zero-config bring-up
// keeps working. This guard runs at builder time on EVERY invocation — including the
// `dotnet Zayra.Api.dll --migrate` one-off job — so a dedicated deployment must set SeedAdmin__Password
// before that job runs too (mirrors the JWT fail-fast above).
{
    const string WeakSeedAdminPassword = "ChangeMe123!";

    var isDedicatedDeployment =
        builder.Environment.IsProduction()
        || string.Equals(Environment.GetEnvironmentVariable("DEDICATED_DEPLOYMENT"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("CLIENT_DEPLOYMENT"), "true", StringComparison.OrdinalIgnoreCase);

    var seedAdminPassword = builder.Configuration["SeedAdmin:Password"];
    var isWeakOrPlaceholder =
        string.IsNullOrWhiteSpace(seedAdminPassword)
        || seedAdminPassword == WeakSeedAdminPassword
        || seedAdminPassword.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase);

    if (isWeakOrPlaceholder)
    {
        if (isDedicatedDeployment)
            throw new InvalidOperationException(
                $"[{builder.Environment.EnvironmentName}] SeedAdmin bootstrap password fail-fast:\n" +
                $"  SeedAdmin:Password is null, empty, a documented placeholder (CHANGE_ME...), or still the insecure default ('{WeakSeedAdminPassword}').\n" +
                "  Set a unique, strong bootstrap admin password before first boot via env var SeedAdmin__Password (config key SeedAdmin:Password).\n" +
                "  docker-compose maps ${SEED_ADMIN_PASSWORD} -> SeedAdmin__Password; on Render / raw containers set SeedAdmin__Password directly (SEED_ADMIN_PASSWORD is NOT read by the app).");

        // Non-dedicated (local dev / docker-compose): substitute a working dev default so bring-up
        // stays zero-config, but never persist a CHANGE_ME* placeholder as the effective password.
        builder.Configuration["SeedAdmin:Password"] = WeakSeedAdminPassword;
        Console.WriteLine(
            $"[SeedAdmin] WARNING [{builder.Environment.EnvironmentName}]: bootstrap admin is using the INSECURE default " +
            "password because SeedAdmin__Password is unset or a placeholder. Never use this outside local development.");
    }

    // Defense in depth for the AuthSeeder demo path: AuthSeeder (always-on, resolved later) seeds a
    // demo company + 25 fake employees when SeedAdmin:SeedDemoData is "true". That flag is read from
    // config BEFORE the runtime demo gate below is evaluated, so neutralize it here for dedicated
    // deployments — one mis-set SeedAdmin__SeedDemoData must never pollute a client tenant.
    if (isDedicatedDeployment
        && string.Equals(builder.Configuration["SeedAdmin:SeedDemoData"], "true", StringComparison.OrdinalIgnoreCase))
    {
        builder.Configuration["SeedAdmin:SeedDemoData"] = "false";
        Console.WriteLine(
            $"[SeedAdmin] OVERRIDE [{builder.Environment.EnvironmentName}]: SeedAdmin__SeedDemoData was 'true' but this is a " +
            "Production/dedicated deployment — forced to 'false' so AuthSeeder cannot seed demo org/employee data into a client tenant.");
    }
}

builder.Services.Configure<EntityScopeOptions>(builder.Configuration.GetSection("EntityScope"));
builder.Services.PostConfigure<EntityScopeOptions>(options =>
{
    options.StrictMode = EntityScopeOptions.ResolveStrictMode(
        builder.Environment.IsProduction(),
        options.StrictMode);
});

builder.Services.AddControllers(options =>
{
    options.Filters.Add<SubscriptionGuardFilter>();
    options.Filters.Add<FeatureFlagGuardFilter>();
})
.AddJsonOptions(options =>
{
    // Forms submit "" for untouched optional fields; treat as null instead of 400.
    options.JsonSerializerOptions.Converters.Add(new Zayra.Api.Infrastructure.Json.EmptyStringNullableGuidConverter());
    options.JsonSerializerOptions.Converters.Add(new Zayra.Api.Infrastructure.Json.EmptyStringNullableDateTimeConverter());
    options.JsonSerializerOptions.Converters.Add(new Zayra.Api.Infrastructure.Json.EmptyStringNullableDateOnlyConverter());
    // Ensure non-nullable DateTime from JSON bodies always arrives with Kind=Utc.
    // Npgsql 6+ rejects Kind=Unspecified for timestamptz columns; this converter
    // treats timezone-free strings as UTC (AssumeUniversal) and converts offset
    // strings to UTC (AdjustToUniversal), matching the nullable converter above.
    options.JsonSerializerOptions.Converters.Add(new Zayra.Api.Infrastructure.Json.UtcDateTimeConverter());
    options.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString;
});
// CORS: explicit allowlist from config + optional CORS_EXTRA_ORIGINS env var for production deployments
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };
var extraOrigins = (builder.Configuration["CORS_EXTRA_ORIGINS"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
allowedOrigins = allowedOrigins.Concat(extraOrigins).Distinct().ToArray();
builder.Services.AddCors(options => options.AddPolicy("kynexone", policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyMethod()
    .AllowAnyHeader()));

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException("Missing required env var: ConnectionStrings__Default");
    connectionString = "Host=localhost;Port=5432;Database=zayra;Username=postgres;Password=password";
}
builder.Services.AddDbContextPool<ZayraDbContext>(options => options
    .UseNpgsql(connectionString,
        npgsqlOptions => npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));

builder.Services.AddMemoryCache();

// Distributed cache: Redis when REDIS_URL is set, in-memory fallback for local dev without Redis.
var redisUrl = builder.Configuration["REDIS_URL"] ?? Environment.GetEnvironmentVariable("REDIS_URL");
if (!string.IsNullOrEmpty(redisUrl))
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisUrl;
        options.InstanceName = "kynexone:";
    });
else
    builder.Services.AddDistributedMemoryCache();

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudiences = new[] { jwtOptions.TenantAudience, jwtOptions.PlatformAudience },
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization(options =>
{
    // PlatformAdmin: must carry both the platform-admin claim AND the platform audience.
    // This means a tenant-audience token is rejected even if it somehow carried
    // is_platform_admin (defence-in-depth beyond the claim-only check that existed before).
    options.AddPolicy("PlatformAdmin", policy => policy
        .RequireClaim("is_platform_admin", "true")
        .RequireClaim("aud", jwtOptions.PlatformAudience));

    // DEFAULT-DENY: any endpoint that does NOT carry an explicit [Authorize]/[AllowAnonymous] now
    // requires an authenticated user. This makes a forgotten authorization attribute fail CLOSED (401)
    // instead of silently exposing the endpoint. Truly public endpoints are explicitly [AllowAnonymous]
    // (AuthController, MfaController challenge-verify, PricingController, /health, tenant localization),
    // which overrides this fallback.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// [HasPermission] mechanism: a policy provider that synthesizes "perm:<key|key>" policies on
// demand (delegating every other policy — PlatformAdmin, the default-deny fallback — to the
// framework default) plus the handler that enforces them against the JWT `permission` claim.
// This is what lets a client-created CUSTOM role actually be granted access: the attribute
// checks effective permissions, not role names. Server-side only; fail-closed.
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, Zayra.Api.Infrastructure.Authorization.PermissionPolicyProvider>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Zayra.Api.Infrastructure.Authorization.PermissionAuthorizationHandler>();

builder.Services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Auth.TotpService>();
builder.Services.AddScoped<IMfaService, Zayra.Api.Infrastructure.Auth.MfaService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccessManagementService, AccessManagementService>();
builder.Services.AddScoped<IEnterpriseIdentityService, EnterpriseIdentityService>();
builder.Services.AddScoped<IAttendanceService, AttendanceService>();
builder.Services.AddScoped<IEmployeeManagementService, EmployeeManagementService>();
builder.Services.AddScoped<IOrganizationSetupService, OrganizationSetupService>();
// Establishment matrix: the ONE budget guard every assignment path shares, plus the per-tenant
// staffing-level defaults seeder (invoked from AuthSeeder + lazily by EstablishmentController).
builder.Services.AddScoped<IEstablishmentGuard, EstablishmentGuardService>();
builder.Services.AddScoped<EstablishmentSeeder>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Governance.ICompanyTaxPolicyResolver, Zayra.Api.Infrastructure.Governance.CompanyTaxPolicyResolver>();
// Employee readiness / hard activation gate (statutory-floor UNION resolver + evaluator + guard).
builder.Services.AddScoped<Zayra.Api.Infrastructure.Employees.IEmployeeReadinessPolicyResolver, Zayra.Api.Infrastructure.Employees.EmployeeReadinessPolicyResolver>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Employees.IEmployeeReadinessEvaluator, Zayra.Api.Infrastructure.Employees.EmployeeReadinessEvaluator>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Employees.IEmployeeActivationGuard, Zayra.Api.Infrastructure.Employees.EmployeeActivationGuard>();
// Duplicate-person detection — the ONE authoritative, server-side detector shared by the pre-create
// check, the create commit backstop, and (via the preloaded-dictionary matcher) the bulk importer.
builder.Services.AddScoped<Zayra.Api.Infrastructure.Employees.IEmployeeDuplicateDetector, Zayra.Api.Infrastructure.Employees.EmployeeDuplicateDetector>();
// Phase 2 rate resolvers: bounded statutory-override precedence + non-statutory company rate precedence.
builder.Services.AddScoped<Zayra.Api.Infrastructure.Payroll.IStatutoryRateResolver, Zayra.Api.Infrastructure.Payroll.StatutoryRateResolver>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Payroll.ICompanyRatePolicyResolver, Zayra.Api.Infrastructure.Payroll.CompanyRatePolicyResolver>();
builder.Services.AddScoped<Zayra.Api.Application.WorkWeek.IWorkWeekService, Zayra.Api.Infrastructure.WorkWeek.WorkWeekService>();
// POD-C3 — the per-company mid-month proration + retro/arrears policy, resolved through the SAME
// CompanyRatePolicy chain (company row → tenant default → compiled default) registered above.
builder.Services.AddScoped<Zayra.Api.Infrastructure.Payroll.IProrationPolicyResolver, Zayra.Api.Infrastructure.Payroll.ProrationPolicyResolver>();
builder.Services.AddScoped<IHrmHierarchyService, HrmHierarchyService>();
builder.Services.AddScoped<IApprovalWorkflowService, ApprovalWorkflowService>();
builder.Services.AddScoped<IApprovalPolicyService, ApprovalPolicyService>();
builder.Services.AddScoped<IAuthSeeder, AuthSeeder>();
builder.Services.AddScoped<IEmployeeModuleSchemaBootstrapper, EmployeeModuleSchemaBootstrapper>();
// P0-5: config-selected durable storage with a Production fail-fast (Render dyno disk is
// ephemeral on plan:free — LocalDocumentStorage would lose compliance documents on restart).
builder.Services.AddDocumentStorage(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddScoped<IHijriDateService, HijriDateService>();
// P0-6: offline Latin→Arabic transliteration (replaces the third-party MyMemory keystroke call).
builder.Services.AddScoped<Zayra.Api.Application.Localization.ITransliterationService,
    Zayra.Api.Infrastructure.Localization.TransliterationService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
// POD-D5 — notification delivery. Channel dispatchers sit behind one abstraction; the vendor seam
// is the provider port below. NO vendor credential lives in code or in a config default: the Null*
// providers report "not_configured" (a visible delivery row), and a real Twilio / Unifonic / Meta
// WhatsApp / FCM / APNs adapter drops in by replacing exactly one of these three lines.
builder.Services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
builder.Services.AddScoped<INotificationProviderConfigReader, NotificationProviderConfigReader>();
builder.Services.AddScoped<ISmsProvider, NullSmsProvider>();
builder.Services.AddScoped<IWhatsAppProvider, NullWhatsAppProvider>();
builder.Services.AddScoped<IPushProvider, NullPushProvider>();
builder.Services.AddScoped<INotificationChannelDispatcher, EmailChannelDispatcher>();
builder.Services.AddScoped<INotificationChannelDispatcher, SmsChannelDispatcher>();
builder.Services.AddScoped<INotificationChannelDispatcher, WhatsAppChannelDispatcher>();
builder.Services.AddScoped<INotificationChannelDispatcher, PushChannelDispatcher>();
builder.Services.AddScoped<ILetterService, LetterService>();
var pdfCapacity = builder.Configuration.GetValue("Pdf:MaxConcurrentRenders", 3);
builder.Services.AddSingleton(new Zayra.Api.Infrastructure.Documents.PdfRenderGate(pdfCapacity));
builder.Services.AddScoped<IRecruitmentService, RecruitmentService>();
builder.Services.AddScoped<IPerformanceService, PerformanceService>();
builder.Services.AddScoped<ILeaveService, LeaveService>();
builder.Services.AddScoped<IDataScopeService, DataScopeService>();
builder.Services.AddSingleton(AiOptions.Load(builder.Configuration));
builder.Services.AddScoped<AiRedactionService>();
builder.Services.AddScoped<AiTokenBudgetService>();
builder.Services.AddScoped<IAiGovernanceService, AiGovernanceService>();
builder.Services.AddScoped<IAiPromptBuilder, AiPromptBuilder>();
builder.Services.AddScoped<IAiAuditService, AiAuditService>();
builder.Services.AddScoped<IAiResponseCacheService, AiResponseCacheService>();
builder.Services.AddScoped<IAiAdvisoryService, AiAdvisoryService>();
builder.Services.AddScoped<Zayra.Api.Application.Shifts.IRosterPlannerService, Zayra.Api.Infrastructure.Shifts.RosterPlannerService>();
builder.Services.AddScoped<Zayra.Api.Application.Setup.ISetupAssistantService, Zayra.Api.Infrastructure.Setup.SetupAssistantService>();
builder.Services.AddScoped<Zayra.Api.Application.Recruitment.IRecruitmentAiService, Zayra.Api.Infrastructure.Recruitment.RecruitmentAiService>();
builder.Services.AddScoped<IPolicyDocumentService, PolicyDocumentService>();
builder.Services.AddScoped<IQiwaIntegrationService, QiwaIntegrationService>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Compliance.SaudiComplianceDashboardService>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Compliance.GosiReadinessReportService>();
// POD-A1: single GOSI/statutory reconciliation truth (contribution-summary, variance-report,
// compliance dashboard variance count). Scoped so it shares the request's memoized IStatutoryRuleReader.
builder.Services.AddScoped<Zayra.Api.Infrastructure.Payroll.GosiReconciliationService>();

// ── POD-D4 — month-end hand-off (GL/ERP journal artifact + bank/WPS payment confirmation) ────────
// Formatters and parsers are registered as IEnumerable<> and resolved BY KEY at the endpoint, so an
// additional ERP shape or a bank's own response layout is one AddSingleton and no other code change.
builder.Services.AddSingleton<Zayra.Api.Infrastructure.Finance.IJournalExportFormatter,
    Zayra.Api.Infrastructure.Finance.GenericCsvJournalFormatter>();
builder.Services.AddSingleton<Zayra.Api.Infrastructure.Finance.IJournalExportFormatter,
    Zayra.Api.Infrastructure.Finance.QuickBooksIifJournalFormatter>();
builder.Services.AddSingleton<Zayra.Api.Infrastructure.Finance.IJournalExportFormatter,
    Zayra.Api.Infrastructure.Finance.OracleGlInterfaceCsvFormatter>();
builder.Services.AddSingleton<Zayra.Api.Infrastructure.Finance.IBankConfirmationParser,
    Zayra.Api.Infrastructure.Finance.GenericCsvBankConfirmationParser>();
builder.Services.AddSingleton<Zayra.Api.Infrastructure.Finance.IBankConfirmationParser,
    Zayra.Api.Infrastructure.Finance.WpsAckBankConfirmationParser>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Finance.JournalExportService>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Finance.BankConfirmationService>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Finance.PeriodHandoffReconciler>();

// Data protection — encrypts Qiwa client secrets at rest.
builder.Services.AddDataProtection();

// Qiwa API adapter: live HTTP client when QIWA_USE_LIVE_ADAPTER=true, sandbox mock otherwise.
builder.Services.AddSingleton<QiwaOAuthTokenCache>();
builder.Services.AddHttpClient("qiwa", c => c.BaseAddress = new Uri("https://api.qiwa.tech"));
if (string.Equals(Environment.GetEnvironmentVariable("QIWA_USE_LIVE_ADAPTER"), "true", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IQiwaApiAdapter, LiveQiwaApiAdapter>();
else
    builder.Services.AddSingleton<IQiwaApiAdapter, SandboxQiwaApiAdapter>();
builder.Services.AddHostedService<QiwaSyncWorker>();
builder.Services.AddHostedService<AiInsightEngine>();
// POD-D5: the ONLY place a notification provider is called. Keeping every send off the request
// thread is what makes "a notification can never fail OR HANG a payroll operation" true.
builder.Services.AddHostedService<NotificationDeliveryWorker>();

builder.Services.AddHttpClient<ILlmClient, LlmClient>();
builder.Services.AddHttpContextAccessor();

// Country pack framework — scoped per request (strategies depend on scoped IStatutoryRuleReader).
// Default (no-op) pack registered as the non-keyed fallback for each interface.
// Country packs registered as keyed scoped services; the resolver checks keyed registrations
// in order: exact jurisdiction key (e.g. "ARE:UAE-DIFC") → country key ("ARE") → default.

builder.Services.AddScoped<Zayra.Api.Application.CountryPack.IStatutoryRuleReader,
    Zayra.Api.Infrastructure.CountryPack.StatutoryRuleReader>();

// Default pack (fallback — non-keyed)
builder.Services.AddScoped<Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator,
    Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator>();
builder.Services.AddScoped<Zayra.Api.Application.CountryPack.IEndOfServiceCalculator,
    Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator>();
builder.Services.AddScoped<Zayra.Api.Application.CountryPack.IWageProtectionExporter,
    Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter>();
builder.Services.AddScoped<Zayra.Api.Application.CountryPack.INationalizationTracker,
    Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker>();
builder.Services.AddScoped<Zayra.Api.Application.CountryPack.ILocalizationProfile,
    Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile>();

// KSA pack — country-wide key "SAU"
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator,
    Zayra.Api.Infrastructure.CountryPack.Ksa.KsaDeductionCalculator>(Zayra.Api.Application.CountryPack.CountryCodes.Saudi);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IEndOfServiceCalculator,
    Zayra.Api.Infrastructure.CountryPack.Ksa.KsaEndOfServiceCalculator>(Zayra.Api.Application.CountryPack.CountryCodes.Saudi);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IWageProtectionExporter,
    Zayra.Api.Infrastructure.CountryPack.Ksa.KsaWageProtectionExporter>(Zayra.Api.Application.CountryPack.CountryCodes.Saudi);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.INationalizationTracker,
    Zayra.Api.Infrastructure.CountryPack.Ksa.KsaNationalizationTracker>(Zayra.Api.Application.CountryPack.CountryCodes.Saudi);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.ILocalizationProfile,
    Zayra.Api.Infrastructure.CountryPack.Ksa.KsaLocalizationProfile>(Zayra.Api.Application.CountryPack.CountryCodes.Saudi);

// UAE pack — country-wide key "ARE" (mainland + ADGM)
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator,
    Zayra.Api.Infrastructure.CountryPack.Uae.UaeDeductionCalculator>(Zayra.Api.Application.CountryPack.CountryCodes.UAE);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IEndOfServiceCalculator,
    Zayra.Api.Infrastructure.CountryPack.Uae.UaeMainlandEndOfServiceCalculator>(Zayra.Api.Application.CountryPack.CountryCodes.UAE);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IWageProtectionExporter,
    Zayra.Api.Infrastructure.CountryPack.Uae.UaeWageProtectionExporter>(Zayra.Api.Application.CountryPack.CountryCodes.UAE);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.INationalizationTracker,
    Zayra.Api.Infrastructure.CountryPack.Uae.UaeNationalizationTracker>(Zayra.Api.Application.CountryPack.CountryCodes.UAE);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.ILocalizationProfile,
    Zayra.Api.Infrastructure.CountryPack.Uae.UaeLocalizationProfile>(Zayra.Api.Application.CountryPack.CountryCodes.UAE);

// UAE DIFC override — jurisdiction-exact key "ARE:UAE-DIFC" (EOS only; deduction/WPS/locale use mainland)
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IEndOfServiceCalculator,
    Zayra.Api.Infrastructure.CountryPack.Uae.UaeDifcEndOfServiceCalculator>(
    $"{Zayra.Api.Application.CountryPack.CountryCodes.UAE}:{Zayra.Api.Application.CountryPack.Jurisdictions.Difc}");

// UAE DIFC descriptor override (DEWS EOS description differs from mainland)
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.ICountryPackDescriptor,
    Zayra.Api.Infrastructure.CountryPack.Uae.UaeDifcDescriptor>(
    $"{Zayra.Api.Application.CountryPack.CountryCodes.UAE}:{Zayra.Api.Application.CountryPack.Jurisdictions.Difc}");

// Qatar pack — country-wide key "QAT"
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator,
    Zayra.Api.Infrastructure.CountryPack.Qatar.QatarDeductionCalculator>(Zayra.Api.Application.CountryPack.CountryCodes.Qatar);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IEndOfServiceCalculator,
    Zayra.Api.Infrastructure.CountryPack.Qatar.QatarEndOfServiceCalculator>(Zayra.Api.Application.CountryPack.CountryCodes.Qatar);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.IWageProtectionExporter,
    Zayra.Api.Infrastructure.CountryPack.Qatar.QatarWageProtectionExporter>(Zayra.Api.Application.CountryPack.CountryCodes.Qatar);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.INationalizationTracker,
    Zayra.Api.Infrastructure.CountryPack.Qatar.QatarNationalizationTracker>(Zayra.Api.Application.CountryPack.CountryCodes.Qatar);
builder.Services.AddKeyedScoped<Zayra.Api.Application.CountryPack.ILocalizationProfile,
    Zayra.Api.Infrastructure.CountryPack.Qatar.QatarLocalizationProfile>(Zayra.Api.Application.CountryPack.CountryCodes.Qatar);

// Pack descriptors — singletons (no DB dependency; static metadata only)
builder.Services.AddSingleton<Zayra.Api.Application.CountryPack.ICountryPackDescriptor,
    Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor>();
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.ICountryPackDescriptor,
    Zayra.Api.Infrastructure.CountryPack.Ksa.KsaDescriptor>(Zayra.Api.Application.CountryPack.CountryCodes.Saudi);
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.ICountryPackDescriptor,
    Zayra.Api.Infrastructure.CountryPack.Uae.UaeDescriptor>(Zayra.Api.Application.CountryPack.CountryCodes.UAE);
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.ICountryPackDescriptor,
    Zayra.Api.Infrastructure.CountryPack.Qatar.QatarDescriptor>(Zayra.Api.Application.CountryPack.CountryCodes.Qatar);

// Identity-document FORMAT packs — the third leg (catalog=shape, floor=requiredness, pack=format).
// Non-keyed default imposes no constraint; each GCC state keyed by ISO-3 (resolver maps ISO-2→ISO-3).
// Singletons — pure static regex tables, no DB dependency.
builder.Services.AddSingleton<Zayra.Api.Application.CountryPack.IIdentityDocumentFormat,
    Zayra.Api.Infrastructure.CountryPack.DefaultIdentityDocumentFormat>();
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.IIdentityDocumentFormat,
    Zayra.Api.Infrastructure.CountryPack.KsaIdentityDocumentFormat>(Zayra.Api.Application.CountryPack.CountryCodes.Saudi);
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.IIdentityDocumentFormat,
    Zayra.Api.Infrastructure.CountryPack.UaeIdentityDocumentFormat>(Zayra.Api.Application.CountryPack.CountryCodes.UAE);
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.IIdentityDocumentFormat,
    Zayra.Api.Infrastructure.CountryPack.QatarIdentityDocumentFormat>(Zayra.Api.Application.CountryPack.CountryCodes.Qatar);
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.IIdentityDocumentFormat,
    Zayra.Api.Infrastructure.CountryPack.KuwaitIdentityDocumentFormat>(Zayra.Api.Application.CountryPack.CountryCodes.Kuwait);
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.IIdentityDocumentFormat,
    Zayra.Api.Infrastructure.CountryPack.OmanIdentityDocumentFormat>(Zayra.Api.Application.CountryPack.CountryCodes.Oman);
builder.Services.AddKeyedSingleton<Zayra.Api.Application.CountryPack.IIdentityDocumentFormat,
    Zayra.Api.Infrastructure.CountryPack.BahrainIdentityDocumentFormat>(Zayra.Api.Application.CountryPack.CountryCodes.Bahrain);

builder.Services.AddScoped<Zayra.Api.Application.CountryPack.ICountryPackResolver,
    Zayra.Api.Infrastructure.CountryPack.CountryPackResolver>();

// Rate limiting — brute-force protection on auth endpoints.
// Limits are configurable via RateLimit:* in appsettings / env vars.
// Default policy: login 10 req/60s per IP, refresh 30 req/60s per IP, platform login 5 req/60s per IP.
var rl = builder.Configuration.GetSection("RateLimit");
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    o.AddPolicy("auth_login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit              = rl.GetValue("LoginPermitLimit", 10),
                Window                   = TimeSpan.FromSeconds(rl.GetValue("LoginWindowSeconds", 60)),
                QueueProcessingOrder     = QueueProcessingOrder.OldestFirst,
                QueueLimit               = 0,
            }));

    o.AddPolicy("auth_refresh", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit              = rl.GetValue("RefreshPermitLimit", 30),
                Window                   = TimeSpan.FromSeconds(rl.GetValue("RefreshWindowSeconds", 60)),
                QueueProcessingOrder     = QueueProcessingOrder.OldestFirst,
                QueueLimit               = 0,
            }));

    o.AddPolicy("platform_login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit              = rl.GetValue("PlatformLoginPermitLimit", 5),
                Window                   = TimeSpan.FromSeconds(rl.GetValue("PlatformLoginWindowSeconds", 60)),
                QueueProcessingOrder     = QueueProcessingOrder.OldestFirst,
                QueueLimit               = 0,
            }));

    // Public (unauthenticated) marketing writes — quote/estimate submissions. Throttle per-IP to
    // prevent spam / storage-exhaustion since these insert rows without any auth.
    o.AddPolicy("public_write", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit              = rl.GetValue("PublicWritePermitLimit", 5),
                Window                   = TimeSpan.FromSeconds(rl.GetValue("PublicWriteWindowSeconds", 60)),
                QueueProcessingOrder     = QueueProcessingOrder.OldestFirst,
                QueueLimit               = 0,
            }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "KynexOne Workforce API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Global exception handler — must be the outermost middleware.
// Converts unhandled exceptions into structured JSON so clients always get a typed error body
// instead of an empty 500. InvalidOperationException (the service-layer sentinel for bad state)
// maps to 400; authorization failures map to 403; everything else is 500 with a traceId.
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalExceptionHandler");
    var traceId = ctx.TraceIdentifier;
    log.LogError(ex, "Unhandled exception. TraceId={TraceId} Path={Path}", traceId, ctx.Request.Path);

    var (statusCode, code, message) = ex switch
    {
        // POD-B1 — a GL post into a closed period (via the Loans/Advances/Bonus posting paths) surfaces
        // as a typed 422 gl_period_closed, matching the inline guards on the payroll Lock/settle/remit paths.
        Zayra.Api.Infrastructure.Payroll.PeriodClosedException closed => (StatusCodes.Status422UnprocessableEntity, "gl_period_closed", closed.Message),
        UnauthorizedAccessException unauthorized => (StatusCodes.Status403Forbidden, "forbidden", unauthorized.Message),
        InvalidOperationException invalid => (StatusCodes.Status400BadRequest, "bad_request", invalid.Message),
        _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred. Quote your traceId when contacting support.")
    };

    ctx.Response.ContentType = "application/json";
    ctx.Response.StatusCode = statusCode;
    await ctx.Response.WriteAsJsonAsync(new
    {
        traceId,
        code,
        message,
    });
}));

// Security + Cache-Control response headers (CSP/HSTS/Permissions-Policy + path-aware caching).
// Logic lives in SecurityHeaders.Apply so it is unit-testable without booting the host.
app.Use(async (context, next) =>
{
    Zayra.Api.Infrastructure.Http.SecurityHeaders.Apply(context.Response.Headers, context.Request.Path.Value ?? string.Empty);
    await next();
});

app.UseCors("kynexone");
app.UseRateLimiter();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
// Per-route audience segregation (defence-in-depth): reject platform-audience tokens on tenant
// /api/* routes so a platform token can never exercise the cross-tenant read bypass on tenant data.
// Placed AFTER UseAuthentication (User is populated) and BEFORE UseAuthorization (runs first).
// Platform-admin cross-tenant endpoints all live under /api/platform (allowlisted); impersonation
// and break-glass carry the tenant audience and are never matched.
app.Use((context, next) =>
    Zayra.Api.Infrastructure.Http.AudienceRouteGuard.InvokeAsync(context, next, jwtOptions.PlatformAudience));
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new
{
    status = "live",
    utc = DateTime.UtcNow,
    service = "zayra-api",
    // Deployed-commit marker for deploy verification. Render injects RENDER_GIT_COMMIT into
    // the running instance, so this reflects exactly which commit is live (falls back to "local").
    commit = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? "local"
})).AllowAnonymous();

app.MapGet("/health/ready", async (ZayraDbContext db, IConfiguration config, CancellationToken ct) =>
{
    var evidence = await ProductionReadinessEvidence.BuildReadinessAsync(db, config, ct);
    return evidence.Status == "ready"
        ? Results.Ok(evidence)
        : Results.Json(evidence, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.MapGet("/health/telemetry", async (ZayraDbContext db, IConfiguration config, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await ProductionReadinessEvidence.BuildTelemetryAsync(db, config, ct));
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            status = "telemetry_unavailable",
            utc = DateTime.UtcNow,
            error = ex.Message
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization();

app.MapGet("/health", async (ZayraDbContext db) =>
{
    // Use raw ADO.NET to bypass EF Core's retry execution strategy in the health path.
    // Fast single-query ping — avoids EF Core retry amplification on health checks.
    try
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema()";
        var tableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return Results.Ok(new { status = "healthy", utc = DateTime.UtcNow, db = "connected", tables = tableCount });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Database error: {ex.Message}", statusCode: 503);
    }
}).AllowAnonymous(); // health check is public; explicit so the default-deny fallback policy allows it

// NOTE: employee endpoints live exclusively in EmployeesController — the former
// minimal-API duplicates here caused AmbiguousMatchException on /api/employees/reports/*.

// ── Migration mode ────────────────────────────────────────────────────────────
// In Production the web process NEVER runs migrations on startup to avoid crashing
// the web service when TiDB or network is unavailable.
// Migrations run via a one-off command:
//   dotnet Zayra.Api.dll --migrate
// or via Render pre-deploy job. Set Database__RunMigrationsOnStartup=true ONLY
// for local dev convenience (it defaults false in Production).
var isMigrateMode = args.Contains("--migrate");
var isPurgeDemoMode = args.Contains("--purge-demo");
var runMigrationsOnStartup = app.Configuration.GetValue<bool>("Database:RunMigrationsOnStartup");

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var dbContext = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();

    // Boot assertions — model-level only, no DB I/O
    TenantOwnershipBootAssertion.Assert(dbContext);
    ControllerEntityReturnBootAssertion.Assert(dbContext, typeof(Program).Assembly);
    // Company dimension: strict (failed boot) outside Production; Production logs errors
    // until ZAYRA_COMPANY_SCOPE_ASSERT=strict is set after a proven-clean deploy cycle.
    CompanyScopeBootAssertion.Assert(
        dbContext,
        CompanyScopeBootAssertion.ResolveStrictMode(app.Environment.IsProduction()),
        logger);

    if (isMigrateMode || runMigrationsOnStartup)
    {
        logger.LogInformation("Running EF Core migrations...");
        await dbContext.Database.MigrateAsync();
        logger.LogInformation("EF Core migrations complete.");
    }
    else
    {
        logger.LogInformation("Skipping EF Core migrations on startup. Set Database:RunMigrationsOnStartup=true or run --migrate.");
    }

    if (isMigrateMode)
    {
        logger.LogInformation("--migrate mode complete. Exiting.");
        return; // exit 0 — Render one-off job succeeds
    }

    // Phase 1B default-company backfill — idempotent (only touches null CompanyId rows),
    // non-fatal, and disabled via CompanyScope:Backfill=false / CompanyScope__Backfill=false.
    if (!string.Equals(app.Configuration["CompanyScope:Backfill"], "false", StringComparison.OrdinalIgnoreCase))
    {
        try { await CompanyScopeBackfill.RunAsync(dbContext, logger); }
        catch (Exception ex) { logger.LogError(ex, "CompanyScopeBackfill failed — continuing startup."); }
    }

    // POD-A3 payroll audit hash-chain backfill — seals legacy payroll_audit_logs rows into the
    // per-tenant tamper-evident chain. Idempotent (only touches unsealed rows), runs before traffic
    // so the strict verifier never false-positives on legacy rows, and is disabled via
    // PayrollAudit:ChainBackfill=false / PayrollAudit__ChainBackfill=false. Non-fatal: if it is
    // disabled or fails, the strict verifier honestly reports the still-unsealed rows as failures
    // (fail-closed signal) rather than silently passing them.
    if (!string.Equals(app.Configuration["PayrollAudit:ChainBackfill"], "false", StringComparison.OrdinalIgnoreCase))
    {
        try { await PayrollAuditChainBackfill.RunAsync(dbContext, logger); }
        catch (Exception ex) { logger.LogError(ex, "PayrollAuditChainBackfill failed — continuing startup."); }
    }

    // One-off demo cleanup: `dotnet Zayra.Api.dll --purge-demo`. Deactivates all
    // demo tenants (guarding the real SeedAdmin tenant) then exits — never seeds.
    if (isPurgeDemoMode)
    {
        await Zayra.Api.Infrastructure.Seed.DemoPurgeRunner.RunAsync(
            dbContext, app.Configuration["SeedAdmin:TenantSlug"], logger);
        logger.LogInformation("--purge-demo mode complete. Exiting.");
        return; // exit 0 — Render one-off job succeeds
    }

    // Seed data — each step is independently non-fatal so one failure never
    // prevents subsequent seeders from running (GOSI/Statutory rules must run
    // even when DemoDataSeeder fails, for example).
    async Task TrySeedAsync(string name, Func<Task> seed, ILogger log)
    {
        try { await seed(); }
        catch (Exception ex)
        {
            log.LogError(ex, "Seeder '{Name}' failed — continuing startup.", name);
            // Drop any entities the failed seeder left in the Added/Modified state, otherwise the
            // next seeder's SaveChanges re-flushes the bad rows and fails too (cascade poisoning).
            dbContext.ChangeTracker.Clear();
        }
    }

    var authSeeder = scope.ServiceProvider.GetRequiredService<IAuthSeeder>();
    await TrySeedAsync("AuthSeeder", () => authSeeder.SeedAsync(), logger);

    var demoDataRequested =
        string.Equals(Environment.GetEnvironmentVariable("SEED_DEMO_DATA"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(app.Configuration["SeedAdmin:SeedDemoData"], "true", StringComparison.OrdinalIgnoreCase);

    // Defense in depth: a client-hosted / dedicated deployment (or ANY Production env) must NEVER run
    // demo seeders — even if SEED_DEMO_DATA / SeedAdmin__SeedDemoData is accidentally set to "true".
    // One mis-set env var must not be able to pollute a client database with demo tenants and users.
    // (This mirrors the builder-time predicate that also neutralizes SeedAdmin:SeedDemoData before
    // AuthSeeder runs.)
    var dedicatedDeployment =
        app.Environment.IsProduction()
        || string.Equals(Environment.GetEnvironmentVariable("DEDICATED_DEPLOYMENT"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("CLIENT_DEPLOYMENT"), "true", StringComparison.OrdinalIgnoreCase);

    var seedDemoData = demoDataRequested && !dedicatedDeployment;

    if (demoDataRequested && dedicatedDeployment)
        logger.LogWarning(
            "Demo data seeding REQUESTED but REFUSED — this is a Production/dedicated client deployment " +
            "(IsProduction={IsProd}, DEDICATED_DEPLOYMENT/CLIENT_DEPLOYMENT respected). Demo seeders will NOT run; " +
            "only idempotent global config (auth bootstrap, GOSI/statutory rules, pricing) is seeded.",
            app.Environment.IsProduction());

    logger.LogInformation("Demo data seeding: {State} (environment={Env})",
        seedDemoData ? "ENABLED" : "DISABLED", app.Environment.EnvironmentName);

    if (seedDemoData)
        await TrySeedAsync("DemoDataSeeder", () => DemoDataSeeder.SeedAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            authSeeder,
            logger), logger);

    // Enterprise GROUP demo tenants (ALMARAI_TEST/TATA_TEST/EMAAR_TEST) — E2E/demo only, idempotent,
    // and NEVER enabled in production/dedicated deployments (separate flag from SEED_DEMO_DATA).
    var enterpriseTestDataRequested = string.Equals(
        Environment.GetEnvironmentVariable(Zayra.Api.Infrastructure.Seed.EnterpriseGroupSeeder.EnableEnvVar),
        "true", StringComparison.OrdinalIgnoreCase);

    if (enterpriseTestDataRequested && dedicatedDeployment)
        logger.LogWarning(
            "Enterprise GROUP test-data seeding REQUESTED ({Flag}=true) but REFUSED — this is a Production/dedicated " +
            "client deployment. Enterprise demo tenants will NOT be seeded.",
            Zayra.Api.Infrastructure.Seed.EnterpriseGroupSeeder.EnableEnvVar);

    if (enterpriseTestDataRequested && !dedicatedDeployment)
        await TrySeedAsync("EnterpriseGroupSeeder", () => new Zayra.Api.Infrastructure.Seed.EnterpriseGroupSeeder(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            authSeeder,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<Zayra.Api.Infrastructure.Seed.EnterpriseGroupSeeder>()).SeedAsync(), logger);

    await TrySeedAsync("GosiRuleSeeder",      () => GosiRuleSeeder.SeedDefaultsAsync(dbContext, logger), logger);
    await TrySeedAsync("StatutoryRuleSeeder", () => Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.SeedAsync(dbContext, logger), logger);

    // Pricing config + module catalog must exist even in production (demo seeding is off there),
    // otherwise the platform-admin pricing/CPQ console is empty. Idempotent (skips when present).
    await TrySeedAsync("PricingConfigSeeder", () => DemoDataSeeder.SeedPricingConfigAsync(dbContext, logger, CancellationToken.None), logger);

    // ── DEMO-ONLY ZONE ─────────────────────────────────────────────────────────────────────
    // Every operation that can MUTATE tenant data (deactivate, rename, or create tenants) runs
    // ONLY when demo seeding is enabled. In production (SeedAdmin__SeedDemoData=false) NOTHING in
    // this block runs — so a deploy can NEVER wipe, revert, deactivate, or rename a real customer
    // tenant. The only seeders that run in production are idempotent, additive global config above
    // (Auth bootstrap, GOSI/statutory rules, pricing) which never delete or mutate customer records.
    if (seedDemoData)
    {
        // Deactivate leftover/garbage demo tenants and soft-delete renamed fragments — demo envs only.
        await TrySeedAsync("GarbageDemoCleanup", () => CleanDemoKsaSeeder.DeactivateGarbageDemoTenantsAsync(dbContext, logger), logger);
        await TrySeedAsync("IntelliFlowFragmentCleanup", () => IntelliFlowFragmentCleanup.RunAsync(dbContext, logger), logger);

        // Seed one clean KSA tenant. Idempotent: no-op when slug exists.
        await TrySeedAsync("CleanDemoKsaSeeder", () => CleanDemoKsaSeeder.SeedAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            authSeeder,
            logger), logger);

        // Seed one clean IntelliFlow Systems tenant (KSA, 12 employees, locked payroll).
        // Idempotent: skips if active "intelliflow" slug already exists.
        await TrySeedAsync("IntelliFlowDemoSeeder", () => IntelliFlowDemoSeeder.SeedAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            authSeeder,
            logger), logger);
    }

}

app.Run();
