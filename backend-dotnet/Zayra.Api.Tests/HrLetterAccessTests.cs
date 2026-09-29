using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The boundary B6 has to hold: an employee can ASK for a salary certificate and cannot ISSUE
/// one. An employee who could issue their own could issue one saying anything, which is the
/// entire reason a bank asks for the document on company letterhead.
/// </summary>
public class HrLetterAccessTests
{
    [Fact]
    public async Task AnEmployee_CanRaiseADocumentRequest_AndItLandsInTheHrTicketQueue()
    {
        await using var db = CreateDb();
        var tenantId = await SeedAsync(db);
        var employeeId = (await db.Employees.FirstAsync()).Id;
        var controller = EssController(db, tenantId, employeeId);

        var response = await controller.CreateDocumentRequest(
            new EssDocumentRequestDto(HrLetterTypes.SalaryCertificate, "bilingual", "bank loan", "Riyad Bank"),
            CancellationToken.None);

        Assert.IsType<CreatedResult>(response);
        var request = await db.EmployeeDocumentRequests.SingleAsync();
        Assert.Equal(employeeId, request.EmployeeId);
        Assert.Equal(HrLetterTypes.SalaryCertificate, request.LetterType);
        Assert.Equal(EmployeeDocumentRequestStatuses.Pending, request.Status);
        Assert.Null(request.IssuedLetterId);

        // It raised a ticket in the queue HR already works, not a second inbox beside it.
        var ticket = await db.HRRequests.SingleAsync();
        Assert.Equal(request.HrRequestId, ticket.Id);
        Assert.Equal("Salary Certificate", ticket.CategoryName);

        // Asking is not issuing.
        Assert.Empty(await db.IssuedLetters.ToListAsync());
    }

    [Fact]
    public async Task AnEmployee_CannotDownloadALetterThatWasNeverIssued()
    {
        await using var db = CreateDb();
        var tenantId = await SeedAsync(db);
        var employeeId = (await db.Employees.FirstAsync()).Id;
        var controller = EssController(db, tenantId, employeeId);

        await controller.CreateDocumentRequest(
            new EssDocumentRequestDto(HrLetterTypes.SalaryCertificate, "en", "bank loan", "Riyad Bank"),
            CancellationToken.None);
        var request = await db.EmployeeDocumentRequests.SingleAsync();

        var result = await controller.MyDocumentRequestPdf(request.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("not_issued_yet", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task AnEmployee_CannotRequestForSomeoneElse_BecauseTheIdComesFromTheirOwnToken()
    {
        await using var db = CreateDb();
        var tenantId = await SeedAsync(db);
        var employees = await db.Employees.OrderBy(x => x.Id).ToListAsync();
        var me = employees[0];
        var colleague = employees[1];

        // The DTO has no employee id at all — there is nothing to tamper with. The request is
        // stamped from GetEssContextAsync, which reads the caller's own token.
        Assert.Null(typeof(EssDocumentRequestDto).GetProperty("EmployeeId"));

        var controller = EssController(db, tenantId, me.Id);
        await controller.CreateDocumentRequest(
            new EssDocumentRequestDto(HrLetterTypes.SalaryCertificate, "en", "loan", "Bank"),
            CancellationToken.None);

        var request = await db.EmployeeDocumentRequests.SingleAsync();
        Assert.Equal(me.Id, request.EmployeeId);
        Assert.NotEqual(colleague.Id, request.EmployeeId);
    }

    [Fact]
    public async Task ASecondOpenRequestForTheSameDocument_IsRefused()
    {
        await using var db = CreateDb();
        var tenantId = await SeedAsync(db);
        var employeeId = (await db.Employees.FirstAsync()).Id;
        var controller = EssController(db, tenantId, employeeId);
        var dto = new EssDocumentRequestDto(HrLetterTypes.SalaryCertificate, "en", "loan", "Bank");

        await controller.CreateDocumentRequest(dto, CancellationToken.None);
        var second = await controller.CreateDocumentRequest(dto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(second);
        Assert.Equal(1, await db.EmployeeDocumentRequests.CountAsync());
    }

    /// <summary>
    /// The issuing endpoints, evaluated through ASP.NET's real authorization machinery rather
    /// than by reading an attribute string: the <c>[Authorize(Roles = …)]</c> metadata is taken
    /// off the actual MethodInfo, combined into a policy the same way the framework does, and
    /// evaluated against an employee principal and an HR principal.
    /// </summary>
    [Theory]
    [InlineData(nameof(HrLettersController.Issue))]
    [InlineData(nameof(HrLettersController.IssueForRequest))]
    [InlineData(nameof(HrLettersController.DeclineRequest))]
    [InlineData(nameof(HrLettersController.Register))]
    [InlineData(nameof(HrLettersController.Reprint))]
    public async Task IssuingEndpoints_RefuseAnEmployee_AndAdmitHr(string methodName)
    {
        var policy = await PolicyFor(typeof(HrLettersController), methodName);
        Assert.NotNull(policy);

        var evaluator = BuildAuthorizationService();

        var employee = Principal("Employee");
        var hrOfficer = Principal("HR Officer");
        var hrManager = Principal("HR Manager");

        Assert.False((await evaluator.AuthorizeAsync(employee, null, policy!)).Succeeded,
            $"{methodName} admitted a plain Employee.");
        Assert.True((await evaluator.AuthorizeAsync(hrOfficer, null, policy!)).Succeeded);
        Assert.True((await evaluator.AuthorizeAsync(hrManager, null, policy!)).Succeeded);
    }

    [Fact]
    public async Task TemplateEditing_IsNarrowerStill_AnHrOfficerCannotRewriteTheWording()
    {
        var policy = await PolicyFor(typeof(HrLettersController), nameof(HrLettersController.UpdateTemplate));
        var evaluator = BuildAuthorizationService();

        // An HR Officer may issue the letter; changing what every future letter SAYS is a
        // different act and belongs with the people who own the template.
        Assert.False((await evaluator.AuthorizeAsync(Principal("HR Officer"), null, policy!)).Succeeded);
        Assert.True((await evaluator.AuthorizeAsync(Principal("HR Manager"), null, policy!)).Succeeded);
        Assert.True((await evaluator.AuthorizeAsync(Principal("Admin"), null, policy!)).Succeeded);
    }

    [Fact]
    public void TheEssController_ExposesNoRouteThatIssuesALetter()
    {
        var essRoutes = typeof(EmployeeSelfServiceController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>())
            .Select(a => a.Template ?? string.Empty)
            .ToList();

        Assert.NotEmpty(essRoutes);
        Assert.DoesNotContain(essRoutes, r => r.Contains("issue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(essRoutes, r => r.Contains("hr-letters", StringComparison.OrdinalIgnoreCase));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static async Task<AuthorizationPolicy?> PolicyFor(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName) ?? throw new InvalidOperationException(methodName);
        IEnumerable<IAuthorizeData> data =
        [
            .. method.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
            .. controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
        ];
        var options = new AuthorizationOptions();
        return await AuthorizationPolicy.CombineAsync(new DefaultAuthorizationPolicyProvider(
            Microsoft.Extensions.Options.Options.Create(options)), data);
    }

    private static IAuthorizationService BuildAuthorizationService()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AuthorizationOptions());
        var handlers = new DefaultAuthorizationHandlerProvider(
            new IAuthorizationHandler[] { new PassThroughAuthorizationHandler(), new RolesAuthorizationHandler() });
        return new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(options),
            handlers,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultAuthorizationService>.Instance,
            new DefaultAuthorizationHandlerContextFactory(),
            new DefaultAuthorizationEvaluator(),
            options);
    }

    private sealed class RolesAuthorizationHandler : AuthorizationHandler<RolesAuthorizationRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RolesAuthorizationRequirement requirement)
        {
            if (context.User.Identity?.IsAuthenticated == true
                && requirement.AllowedRoles.Any(context.User.IsInRole))
                context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role),
        ], "test"));

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Name = "ESS Letters", Slug = $"ess-letters-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        db.HRRequestCategories.Add(new HRRequestCategory
        {
            TenantId = tenant.Id, Code = "SAL-CERT", Name = "Salary Certificate", DefaultSlaHours = 24, IsActive = true,
        });
        foreach (var template in HrLetterTemplateDefaults.Build())
        {
            template.TenantId = tenant.Id;
            db.HrLetterTemplates.Add(template);
        }
        db.Employees.AddRange(
            new Employee { TenantId = tenant.Id, EmployeeCode = "E1", FullName = "Me", EnglishName = "Me", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) },
            new Employee { TenantId = tenant.Id, EmployeeCode = "E2", FullName = "Colleague", EnglishName = "Colleague", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) });
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static EmployeeSelfServiceController EssController(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var letters = new LetterService();
        var controller = new EmployeeSelfServiceController(
            db,
            letters,
            new PdfRenderGate(1),
            new Zayra.Api.Infrastructure.Leave.LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db)),
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(db, TestNotifications.For(db), new StubHttpClientFactory()),
            new HrLetterIssuer(db, letters, new NullDocumentStorage()));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim("employee_id", employeeId.ToString()),
                    new Claim("access_mode", "FullPortal"),
                    new Claim("permission", "ess.read"),
                    new Claim("permission", "ess.write"),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Employee"),
                ], "test")),
            },
        };
        return controller;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
