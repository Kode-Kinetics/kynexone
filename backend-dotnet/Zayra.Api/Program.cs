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
using Zayra.Api.Infrastructure.Qiwa;

var builder = WebApplication.CreateBuilder(args);

// ── P3: JWT audience prod fail-fast ──────────────────────────────────────────
// Dev defaults are intentionally left in appsettings.json for zero-config local dev.
// In Production they MUST be overridden via environment variables (Jwt__TenantAudience,
// Jwt__PlatformAudience). A forgotten env var in prod becomes a failed deploy, not silent drift.
{
    const string DevTenantAudience   = "kynexone-tenant";
    const string DevPlatformAudience = "kynexone-platform";

    if (builder.Environment.IsProduction())
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

        if (prodErrors.Count > 0)
            throw new InvalidOperationException(
                "Production JWT configuration fail-fast:\n" + string.Join("\n", prodErrors.Select(e => "  " + e)));
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
});

builder.Services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Auth.TotpService>();
builder.Services.AddScoped<IMfaService, Zayra.Api.Infrastructure.Auth.MfaService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccessManagementService, AccessManagementService>();
builder.Services.AddScoped<IAttendanceService, AttendanceService>();
builder.Services.AddScoped<IEmployeeManagementService, EmployeeManagementService>();
builder.Services.AddScoped<IOrganizationSetupService, OrganizationSetupService>();
builder.Services.AddScoped<IHrmHierarchyService, HrmHierarchyService>();
builder.Services.AddScoped<IApprovalWorkflowService, ApprovalWorkflowService>();
builder.Services.AddScoped<IApprovalPolicyService, ApprovalPolicyService>();
builder.Services.AddScoped<IAuthSeeder, AuthSeeder>();
builder.Services.AddScoped<IEmployeeModuleSchemaBootstrapper, EmployeeModuleSchemaBootstrapper>();
builder.Services.AddScoped<ILogisticsSeeder, LogisticsSeeder>();
builder.Services.AddScoped<IDocumentStorage, LocalDocumentStorage>();
builder.Services.AddScoped<IHijriDateService, HijriDateService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
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
builder.Services.AddScoped<IPolicyDocumentService, PolicyDocumentService>();
builder.Services.AddScoped<IQiwaIntegrationService, QiwaIntegrationService>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Compliance.SaudiComplianceDashboardService>();
builder.Services.AddScoped<Zayra.Api.Infrastructure.Compliance.GosiReadinessReportService>();

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
// maps to 400; everything else is 500 with a traceId for support correlation.
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalExceptionHandler");
    var traceId = ctx.TraceIdentifier;
    log.LogError(ex, "Unhandled exception. TraceId={TraceId} Path={Path}", traceId, ctx.Request.Path);

    ctx.Response.ContentType = "application/json";
    ctx.Response.StatusCode = ex is InvalidOperationException ? 400 : 500;
    await ctx.Response.WriteAsJsonAsync(new
    {
        traceId,
        code = ex is InvalidOperationException ? "bad_request" : "internal_error",
        message = ex is InvalidOperationException
            ? ex.Message
            : "An unexpected error occurred. Quote your traceId when contacting support.",
    });
}));

// Security + Cache-Control response headers.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Permitted-Cross-Domain-Policies"] = "none";

    var path = context.Request.Path.Value ?? string.Empty;
    // Sensitive: auth, payroll, personal data — must never be cached anywhere.
    if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/payroll", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/employees", StringComparison.OrdinalIgnoreCase))
    {
        headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        headers["Pragma"] = "no-cache";
    }
    // Semi-static reference data: short private cache so browser avoids round trips.
    else if (path.StartsWith("/api/master-data", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/features", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/localization", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/help-text", StringComparison.OrdinalIgnoreCase))
    {
        headers["Cache-Control"] = "private, max-age=300"; // 5 min, per-user
    }
    // Everything else: don't cache by default; controllers can override explicitly.
    else
    {
        headers["Cache-Control"] = "no-store";
    }

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
app.UseAuthorization();
app.MapControllers();
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
});

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

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var dbContext = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();

    // Boot assertions — model-level only, no DB I/O
    TenantOwnershipBootAssertion.Assert(dbContext);
    ControllerEntityReturnBootAssertion.Assert(dbContext, typeof(Program).Assembly);

    // Always run migrations on startup — MigrateAsync is a no-op when schema is current.
    logger.LogInformation("Running EF Core migrations...");
    await dbContext.Database.MigrateAsync();
    logger.LogInformation("EF Core migrations complete.");

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

    var seedDemoData =
        string.Equals(Environment.GetEnvironmentVariable("SEED_DEMO_DATA"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(app.Configuration["SeedAdmin:SeedDemoData"], "true", StringComparison.OrdinalIgnoreCase);

    logger.LogInformation("Demo data seeding: {State} (environment={Env})",
        seedDemoData ? "ENABLED" : "DISABLED", app.Environment.EnvironmentName);

    if (seedDemoData)
        await TrySeedAsync("DemoDataSeeder", () => DemoDataSeeder.SeedAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            authSeeder,
            logger), logger);

    await TrySeedAsync("GosiRuleSeeder",      () => GosiRuleSeeder.SeedDefaultsAsync(dbContext, logger), logger);
    await TrySeedAsync("StatutoryRuleSeeder", () => Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.SeedAsync(dbContext, logger), logger);

    // Pricing config + module catalog must exist even in production (demo seeding is off there),
    // otherwise the platform-admin pricing/CPQ console is empty. Idempotent (skips when present).
    await TrySeedAsync("PricingConfigSeeder", () => DemoDataSeeder.SeedPricingConfigAsync(dbContext, logger, CancellationToken.None), logger);

    // Deactivate garbage demo tenants and seed one clean KSA tenant.
    // Idempotent: cleanup is a no-op when already deactivated; seed is a no-op when slug exists.
    await TrySeedAsync("GarbageDemoCleanup", () => CleanDemoKsaSeeder.DeactivateGarbageDemoTenantsAsync(dbContext, logger), logger);
    await TrySeedAsync("CleanDemoKsaSeeder", () => CleanDemoKsaSeeder.SeedAsync(
        dbContext,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
        authSeeder,
        logger), logger);

    // Soft-delete the 5 split-tenant IntelliFlow fragments (SeedDemoData corruption).
    // Idempotent: already-deactivated fragments are skipped; rasalmanar is explicitly guarded.
    await TrySeedAsync("IntelliFlowFragmentCleanup", () => IntelliFlowFragmentCleanup.RunAsync(dbContext, logger), logger);

    // Seed one clean IntelliFlow Systems tenant (KSA, 12 employees, locked payroll).
    // Idempotent: skips if active "intelliflow" slug already exists.
    await TrySeedAsync("IntelliFlowDemoSeeder", () => IntelliFlowDemoSeeder.SeedAsync(
        dbContext,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
        authSeeder,
        logger), logger);

    await TrySeedAsync("LogisticsSeeder", () => scope.ServiceProvider.GetRequiredService<ILogisticsSeeder>().SeedAsync(), logger);

    if (isMigrateMode)
    {
        logger.LogInformation("--migrate mode complete. Exiting.");
        return; // exit 0 — Render one-off job succeeds
    }
}

app.Run();
