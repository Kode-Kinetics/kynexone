using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// Real ASP.NET pipeline (isolated SQLite host, JWT validation, default-deny policy, filters) for the custom
/// bank template routes. Users are real rows holding exactly the permissions named; tokens come from the
/// application's own issuer. Nothing here talks to a bank.
/// </summary>
public sealed class BankTemplateHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid CompanyA { get; private set; }
    public Guid CompanyA2 { get; private set; }
    public Guid CompanyB { get; private set; }
    public Guid BatchA { get; private set; }
    public string MakerToken { get; private set; } = "";        // payroll.export + payroll.structure_manage
    public string CheckerToken { get; private set; } = "";      // payroll.approve only
    public string ExporterToken { get; private set; } = "";     // payroll.export only
    public string NoPermissionToken { get; private set; } = ""; // employees.read only
    public string ForeignToken { get; private set; } = "";      // another tenant, all three permissions

    private static readonly string[] Emails = { "maker", "checker", "exporter", "nobody", "foreign" };

    public async Task InitializeAsync()
    {
        var name = $"bank-template-http-{Guid.NewGuid():N}";
        var cs = $"Data Source=file:{name}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(cs);
        await _anchor.OpenAsync();

        CompanyA = Guid.NewGuid(); CompanyA2 = Guid.NewGuid(); CompanyB = Guid.NewGuid();
        string Key(string section, int i, string v) => $"{SaudiBankExportActivation.Section}:{section}:{i}:{v}";
        var cfg = new Dictionary<string, string?>
        {
            [$"{SaudiBankExportActivation.Section}:Enabled"] = "true",
            [Key("Companies", 0, "TenantId")] = TenantA.ToString(), [Key("Companies", 0, "CompanyId")] = CompanyA.ToString(),
            [Key("Companies", 1, "TenantId")] = TenantA.ToString(), [Key("Companies", 1, "CompanyId")] = CompanyA2.ToString(),
            [Key("Companies", 2, "TenantId")] = TenantB.ToString(), [Key("Companies", 2, "CompanyId")] = CompanyB.ToString(),
            [PrivilegedMfaPolicy.DisabledForTestingConfigKey] = "true",
        };
        Host = new AuthorizationPipelineHost(cs, null, cfg, new Dictionary<string, string?> { [PrivilegedMfaPolicy.DisabledForTestingConfigKey] = "true" });
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.AddRange(new Tenant { Id = TenantA, Name = "Bank template HTTP A", Slug = "bt-http-a", IsActive = true },
                                new Tenant { Id = TenantB, Name = "Bank template HTTP B", Slug = "bt-http-b", IsActive = true });
            await db.SaveChangesAsync();
            foreach (var (id, tenant) in new[] { (CompanyA, TenantA), (CompanyA2, TenantA), (CompanyB, TenantB) })
                db.Companies.Add(new Company { Id = id, TenantId = tenant, LegalNameEn = $"Synthetic Co {id:N}"[..24], CountryCode = "SA", DefaultCurrency = "SAR" });
            await db.SaveChangesAsync();

            void User(string email, Guid tenant, params string[] perms)
            {
                var u = new User
                {
                    TenantId = tenant, Email = $"{email}@bt-http.local", NormalizedEmail = $"{email}@bt-http.local".ToUpperInvariant(), FullName = email,
                    PasswordHash = "no-login-path-in-this-harness", Status = "Active", AccessMode = "FullPortal", IsActive = true,
                    IsEmailConfirmed = true, IsGroupScope = true,
                };
                db.Users.Add(u);
                db.Set<UserPermissionOverride>().AddRange(perms.Select(p => new UserPermissionOverride
                { TenantId = tenant, UserId = u.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "bank template http test" }));
            }
            User("maker", TenantA, "payroll.export", "payroll.structure_manage");
            User("checker", TenantA, "payroll.approve");
            User("exporter", TenantA, "payroll.export");
            User("nobody", TenantA, "employees.read");
            User("foreign", TenantB, "payroll.export", "payroll.structure_manage", "payroll.approve");
            await db.SaveChangesAsync();

            BatchA = await SaudiBankExportTestData.SeedBatchAsync(db, TenantA, CompanyA, 9);
            // Both employees need valid Saudi IBANs for a custom file.
            var rec = await db.PayrollPaymentRecords.Where(r => r.PaymentBatchId == BatchA && r.Amount == 5250.50m).SingleAsync();
            rec.Iban = CustomBankFileGeneratorTests.IbanB;
            var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == rec.EmployeeId);
            profile.Iban = CustomBankFileGeneratorTests.IbanB; profile.AccountNumber = string.Empty;
            await db.SaveChangesAsync();
        }

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var ts = scope.ServiceProvider.GetRequiredService<ITokenService>();
            MakerToken = await Mint(db, ts, "maker"); CheckerToken = await Mint(db, ts, "checker");
            ExporterToken = await Mint(db, ts, "exporter"); NoPermissionToken = await Mint(db, ts, "nobody");
            ForeignToken = await Mint(db, ts, "foreign");
        }
    }

    private static async Task<string> Mint(ZayraDbContext db, ITokenService ts, string who)
    {
        var email = $"{who}@bt-http.local";
        var user = await db.Users.Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides).Include(x => x.EmployeeUserAccounts).Include(x => x.EntityAccesses)
            .AsNoTracking().FirstAsync(x => x.Email == email);
        var grants = Array.Empty<EntityAccessGrant>();
        return ts.CreateAccessToken(user, Array.Empty<string>(), AuthService.GetPermissions(user), user.Tenant!, grants,
            EntityScopeClaims.Resolve(user.IsGroupScope, grants, Array.Empty<Guid>()), out _);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}

[CollectionDefinition("BankTemplateHttp")]
public sealed class BankTemplateHttpCollection : ICollectionFixture<BankTemplateHttpFixture> { }

[Collection("BankTemplateHttp")]
public sealed class CustomBankTemplateHttpTests(BankTemplateHttpFixture f)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private string Base(Guid company) => $"/api/payroll/bank-exports/companies/{company}/templates";

    private async Task<HttpResponseMessage> Send(HttpMethod m, string url, string? token, object? body = null)
    {
        using var req = new HttpRequestMessage(m, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, options: Web);
        return await f.Client.SendAsync(req);
    }

    private static object Proposal(string name = "HTTP layout") => new
    {
        definition = CustomBankTemplates.Clone(CustomBankTemplateStarters.Find(CustomBankTemplateStarters.GenericCsv)!.Definition) is { } d
            ? Rename(d, name) : null,
    };

    private static CustomBankTemplateDefinition Rename(CustomBankTemplateDefinition d, string n) { d.Name = n; return d; }

    private async Task<(Guid Id, string Hash)> CreateAsync(string name = "HTTP layout")
    {
        using var r = await Send(HttpMethod.Post, Base(f.CompanyA), f.MakerToken, Proposal(name));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("template");
        return (doc.GetProperty("id").GetGuid(), doc.GetProperty("versions")[0].GetProperty("definitionHash").GetString()!);
    }

    // ── 401 / 403 ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "templates/catalog")]
    [InlineData("GET", "{c}/templates")]
    [InlineData("POST", "{c}/templates")]
    [InlineData("PUT", "{c}/templates/{t}")]
    [InlineData("POST", "{c}/templates/{t}/approve")]
    [InlineData("POST", "{c}/templates/{t}/reject")]
    [InlineData("POST", "batches/{b}/preview")]
    public async Task Anonymous_IsUnauthorized_OnEveryTemplateRoute(string method, string route)
    {
        var url = "/api/payroll/bank-exports/" + route.Replace("{c}", $"companies/{f.CompanyA}").Replace("{t}", Guid.NewGuid().ToString()).Replace("{b}", f.BatchA.ToString());
        using var r = await Send(new HttpMethod(method), url, null, method == "GET" ? null : new { });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task UserWithoutAnyPayrollPermission_IsForbidden()
    {
        using var list = await Send(HttpMethod.Get, Base(f.CompanyA), f.NoPermissionToken);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        using var create = await Send(HttpMethod.Post, Base(f.CompanyA), f.NoPermissionToken, Proposal());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        using var preview = await Send(HttpMethod.Post, $"/api/payroll/bank-exports/batches/{f.BatchA}/preview", f.NoPermissionToken, new { });
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
    }

    [Fact]
    public async Task ExportOnly_CannotCreateOrChange_AndCannotBeTheChecker()
    {
        using var create = await Send(HttpMethod.Post, Base(f.CompanyA), f.ExporterToken, Proposal());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        var (id, hash) = await CreateAsync("export-only target");
        using var approve = await Send(HttpMethod.Post, $"{Base(f.CompanyA)}/{id}/approve", f.ExporterToken, new { version = 1, definitionHash = hash });
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
    }

    // ── maker-checker through the pipeline ───────────────────────────────────────────────────────

    [Fact]
    public async Task Proposer_IsRefused_ApproveOnlyUserApproves_AndTheTemplateBecomesActive()
    {
        var (id, hash) = await CreateAsync("maker-checker");
        // The maker holds no payroll.approve, so the pipeline refuses before the proposer rule is even reached.
        using var asMaker = await Send(HttpMethod.Post, $"{Base(f.CompanyA)}/{id}/approve", f.MakerToken, new { version = 1, definitionHash = hash });
        Assert.Equal(HttpStatusCode.Forbidden, asMaker.StatusCode);

        // A stale hash is refused even for the checker.
        using var stale = await Send(HttpMethod.Post, $"{Base(f.CompanyA)}/{id}/approve", f.CheckerToken, new { version = 1, definitionHash = "0000" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var ok = await Send(HttpMethod.Post, $"{Base(f.CompanyA)}/{id}/approve", f.CheckerToken, new { version = 1, definitionHash = hash });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var get = await Send(HttpMethod.Get, $"{Base(f.CompanyA)}/{id}", f.MakerToken);
        var state = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString();
        Assert.Equal("Active", state);

        // Approving again is a conflict (already decided).
        using var again = await Send(HttpMethod.Post, $"{Base(f.CompanyA)}/{id}/approve", f.CheckerToken, new { version = 1, definitionHash = hash });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // ── 404s through the real filters ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TemplateOfAnotherCompanyOrTenant_IsNotFound()
    {
        var (id, hash) = await CreateAsync("scope target");

        // Same tenant, another (activated) company: the id does not exist there.
        using var otherCompany = await Send(HttpMethod.Get, $"{Base(f.CompanyA2)}/{id}", f.MakerToken);
        Assert.Equal(HttpStatusCode.NotFound, otherCompany.StatusCode);
        using var otherCompanyApprove = await Send(HttpMethod.Post, $"{Base(f.CompanyA2)}/{id}/approve", f.CheckerToken, new { version = 1, definitionHash = hash });
        Assert.Equal(HttpStatusCode.NotFound, otherCompanyApprove.StatusCode);

        // Another tenant (holding every permission) reaching for tenant A's company and template.
        foreach (var (m, url, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"{Base(f.CompanyA)}/{id}", null),
            (HttpMethod.Get, Base(f.CompanyA), null),
            (HttpMethod.Put, $"{Base(f.CompanyA)}/{id}", Proposal("hijack")),
            (HttpMethod.Post, $"{Base(f.CompanyA)}/{id}/approve", new { version = 1, definitionHash = hash }),
            (HttpMethod.Post, $"{Base(f.CompanyA)}/{id}/reject", new { version = 1, definitionHash = hash, note = "no reason" }),
            (HttpMethod.Post, $"/api/payroll/bank-exports/batches/{f.BatchA}/preview", new { definition = (object?)null }),
        })
        {
            using var r = await Send(m, url, f.ForeignToken, body);
            Assert.True(r.StatusCode == HttpStatusCode.NotFound, $"{m} {url} -> {(int)r.StatusCode}");
        }
        // The template is untouched.
        using var still = await Send(HttpMethod.Get, $"{Base(f.CompanyA)}/{id}", f.MakerToken);
        Assert.Equal("PendingApproval", JsonDocument.Parse(await still.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString());
    }

    // ── request size limit ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OversizedTemplateBody_IsRefused()
    {
        var huge = new { definition = new { name = new string('x', 200_000) } };
        using var r = await Send(HttpMethod.Post, Base(f.CompanyA), f.MakerToken, huge);
        // Kestrel answers 413 from [RequestSizeLimit]; the in-memory TestServer does not enforce body limits, so here the
        // validator refuses it (422). Either way it is refused; the attribute itself is pinned below.
        Assert.True(r.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, $"got {(int)r.StatusCode}");
    }

    [Theory]
    [InlineData("CreateTemplate")]
    [InlineData("UpdateTemplate")]
    [InlineData("ApproveTemplate")]
    [InlineData("RejectTemplate")]
    [InlineData("Preview")]
    public void TemplateRoutes_CarryABodySizeLimit(string action)
    {
        var method = typeof(Zayra.Api.Controllers.SaudiBankExportsController).GetMethod(action);
        Assert.NotNull(method);
        var limit = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), inherit: true);
        Assert.Single(limit);
    }

    // ── preview and download over HTTP ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_ReturnsMaskedLines_AndNeverTheRealIbanOrId()
    {
        var date = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd");
        using var r = await Send(HttpMethod.Post, $"/api/payroll/bank-exports/batches/{f.BatchA}/preview", f.ExporterToken, new
        {
            batchReference = "2026092601", paymentDate = date,
            definition = CustomBankTemplateStarters.Find(CustomBankTemplateStarters.GenericCsv)!.Definition,
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(CustomBankFileGeneratorTests.IbanA, text);
        Assert.DoesNotContain("1012345678", text);
        Assert.Contains("\"redacted\":true", text);
    }

    [Fact]
    public async Task Download_ServesTheExactFile_WithNoStoreNosniffAndAnAttachmentName()
    {
        using (var scope = f.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var tpl = new CustomBankTemplateService(db);
            var maker = Guid.NewGuid(); var checker = Guid.NewGuid();
            var t = (await tpl.ProposeAsync(f.TenantA, f.CompanyA, maker, null, new CustomBankTemplateProposal { StarterId = CustomBankTemplateStarters.GenericCsv }, default)).Value!;
            await tpl.DecideAsync(f.TenantA, f.CompanyA, checker, t.Id, true, new CustomBankTemplateDecision { Version = 1, DefinitionHash = t.Versions[0].DefinitionHash }, default);
            var svc = new SaudiBankExportService(db);
            var saved = await svc.SaveSettingsAsync(f.TenantA, f.CompanyA, maker,
                new SaudiBankExportSettingsDto { FormatId = t.FormatId, MolEstablishmentId = "7001234567" }, default);
            Assert.Equal(SaudiBankExportOutcome.Ok, saved.Outcome);
        }

        var date = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd");
        using var gen = await Send(HttpMethod.Post, $"/api/payroll/bank-exports/batches/{f.BatchA}/generate", f.ExporterToken, new { batchReference = "2026092601", paymentDate = date });
        Assert.True(gen.StatusCode == HttpStatusCode.OK, await gen.Content.ReadAsStringAsync());

        using var dl = await Send(HttpMethod.Get, $"/api/payroll/bank-exports/batches/{f.BatchA}/download", f.ExporterToken);
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("application/octet-stream", dl.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", dl.Headers.CacheControl?.ToString() ?? "");
        Assert.True(dl.Headers.CacheControl?.Private);
        Assert.Contains("nosniff", dl.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal("attachment", dl.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("salary-2026092601.csv", dl.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var body = await dl.Content.ReadAsStringAsync();
        Assert.Contains(CustomBankFileGeneratorTests.IbanA, body);

        // Another tenant cannot download it.
        using var foreign = await Send(HttpMethod.Get, $"/api/payroll/bank-exports/batches/{f.BatchA}/download", f.ForeignToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }
}
