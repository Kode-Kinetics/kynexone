using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Employee-directory and workspace authorization contract. Attribute cases run through the same
/// authorization middleware registrations as production; body-dependent cases invoke the real action.
/// </summary>
public sealed class EmployeeWorkspaceAuthorizationTests
{
    public static TheoryData<string> EmployeeReadCases => new()
    {
        nameof(EmployeesController.Search),
        nameof(EmployeesController.ExEmployees),
        nameof(EmployeesController.Get),
        nameof(EmployeesController.FieldCatalog),
        nameof(EmployeesController.EmployeeDocuments),
        nameof(EmployeesController.EmployeeHistory),
        nameof(EmployeesController.Readiness),
        nameof(EmployeesController.OrgChart),
        nameof(EmployeesController.GetReportingLines),
        nameof(EmployeesController.ResolveHierarchy),
        nameof(EmployeesController.ResolveWorkflowApprovers),
    };

    public static TheoryData<Type, string, string> MutationCases => new()
    {
        { typeof(EmployeesController), nameof(EmployeesController.CreateEmployee), "employees.write" },
        { typeof(EmployeesController), nameof(EmployeesController.UpdateEmployee), "employees.write" },
        { typeof(EmployeesController), nameof(EmployeesController.ChangeStatus), "employees.write" },
        { typeof(EmployeesController), nameof(EmployeesController.Activate), "employees.approve" },
        { typeof(EmployeesController), nameof(EmployeesController.Terminate), "employees.approve" },
        { typeof(EmployeesController), nameof(EmployeesController.Delete), "employees.delete" },
        { typeof(EmployeesController), nameof(EmployeesController.DuplicateCheck), "employees.write" },
        { typeof(EmployeesController), nameof(EmployeesController.ResolveDuplicate), "employees.write" },
        { typeof(EmployeesController), nameof(EmployeesController.ConfirmImportedBankDetails), "employees.write" },
        { typeof(EmployeesController), nameof(EmployeesController.UploadEmployeeDocument), "employees.documents" },
        { typeof(EmployeesController), nameof(EmployeesController.UpdateEmployeeDocument), "employees.documents" },
        { typeof(EmployeesController), nameof(EmployeesController.VerifyEmployeeDocument), "employees.documents" },
        { typeof(EmployeesController), nameof(EmployeesController.RejectEmployeeDocument), "employees.documents" },
        { typeof(EmployeesController), nameof(EmployeesController.ArchiveEmployeeDocument), "employees.approve" },
        { typeof(EmployeesController), nameof(EmployeesController.RequestTransfer), "manager.approve" },
        { typeof(EmployeesController), nameof(EmployeesController.ApproveCurrentManager), "manager.approve" },
        { typeof(EmployeesController), nameof(EmployeesController.ApproveNewManager), "manager.approve" },
        { typeof(EmployeesController), nameof(EmployeesController.ApproveHrTransfer), "employees.approve" },
        { typeof(EmployeesController), nameof(EmployeesController.ImportPreview), "employees.bulk_import" },
        { typeof(EmployeesController), nameof(EmployeesController.Import), "employees.bulk_import" },
        { typeof(EmployeeCompletionController), nameof(EmployeeCompletionController.Get), "employees.write" },
        { typeof(EmployeeCompletionController), nameof(EmployeeCompletionController.Request), "employees.write" },
        { typeof(EmployeeAccessController), nameof(EmployeeAccessController.IssueCodes), "employees.access.issue" },
        { typeof(EmployeeAccessController), nameof(EmployeeAccessController.BackfillWorkEmails), "employees.access.issue" },
    };

    [Theory]
    [MemberData(nameof(EmployeeReadCases))]
    public async Task ReadEndpoint_WithoutEmployeesRead_ReturnsForbidden(string action)
    {
        ExplicitPermission(typeof(EmployeesController), action).Should().Contain("employees.read",
            $"{action} must publish the workspace read contract locally instead of relying on role inference");

        var status = await PipelineStatus(typeof(EmployeesController), action, Caller());

        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ImportTemplate_WithoutBulkImport_ReturnsForbidden()
    {
        ExplicitPermission(typeof(EmployeesController), nameof(EmployeesController.ImportTemplate))
            .Should().Contain("employees.bulk_import");

        var status = await PipelineStatus(
            typeof(EmployeesController), nameof(EmployeesController.ImportTemplate), Caller("employees.read"));

        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task MutationEndpoint_WithoutActionPermission_ReturnsForbidden(
        Type controller, string action, string permission)
    {
        ExplicitPermission(controller, action).Should().Contain(permission);

        var status = await PipelineStatus(controller, action, Caller("employees.read"));

        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("activate", "employees.approve")]
    [InlineData("deactivate", "employees.write")]
    [InlineData("delete", "employees.delete")]
    [InlineData("export", "employees.write")]
    public async Task BulkAction_UsesItsOwnPermission(string action, string permission)
    {
        await using var db = CreateDb();
        var controller = Controller(db, Caller("employees.read"), new UnrestrictedScope());
        var request = new EmployeesController.BulkActionRequest(
            action, "ids", [1], null, action is "delete" or "deactivate" ? "Reason" : null, null, null);

        var denied = await controller.BulkAction(request, CreateService(db), CancellationToken.None);

        denied.Should().BeOfType<ForbidResult>($"{action} requires {permission}, not directory read");
    }

    [Fact]
    public async Task SensitiveRead_WithoutSensitivePermission_IsMasked()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, companyId: null);
        db.EmployeeDocuments.Add(new EmployeeDocument
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            DocumentType = "Passport",
            DocumentCategory = "Identity",
            FileName = "passport-1234.pdf",
            ContentType = "application/pdf",
            StorageUrl = $"{tenantId:N}/passport-1234.pdf",
            Notes = "Passport P1234567",
            IssueDate = new DateOnly(2024, 1, 1),
            ExpiryDate = new DateOnly(2034, 1, 1),
        });
        await db.SaveChangesAsync();

        var detail = await CreateService(db).GetAsync(
            tenantId, employee.Id, includeSensitive: false, Context(tenantId), CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.Salary.Should().BeNull();
        detail.PassportNumber.Should().BeEmpty();
        detail.Documents.Should().ContainSingle();
        var document = detail.Documents.Single();
        document.FileName.Should().BeEmpty();
        document.StorageUrl.Should().BeEmpty();
        document.Notes.Should().BeEmpty();
        document.IssueDate.Should().BeNull();
        document.ExpiryDate.Should().BeNull();

        var controller = Controller(db, Caller(["employees.read"], tenantId), new UnrestrictedScope());
        var metadataResult = await controller.EmployeeDocuments(employee.Id, CreateService(db), CancellationToken.None);
        var metadata = Assert.IsType<OkObjectResult>(metadataResult.Result).Value
            .Should().BeAssignableTo<IReadOnlyCollection<EmployeeDocumentDto>>().Subject.Single();
        metadata.FileName.Should().BeEmpty();
        metadata.StorageUrl.Should().BeEmpty();
        metadata.Notes.Should().BeEmpty();
        metadata.ExpiryDate.Should().BeNull();
    }

    [Fact]
    public async Task ScopedReader_CannotReadAnotherCompanyEmployee()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, Guid.NewGuid());
        var controller = Controller(db, Caller(["employees.read"], tenantId), new FixedScope([]));

        var result = await controller.Get(employee.Id, CreateService(db), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData("General", false, false, false, 403)]
    [InlineData("General", true, false, false, 403)]
    [InlineData("General", false, true, false, 403)]
    [InlineData("General", true, true, false, 200)]
    [InlineData("Identity", true, true, false, 403)]
    [InlineData("Identity", true, true, true, 200)]
    [InlineData("Payroll", true, true, false, 403)]
    [InlineData("Payroll", true, true, true, 200)]
    public async Task RawDocumentDownload_RequiresReadAndDocuments_AndSensitiveForProtectedCategories(
        string category, bool canRead, bool canManageDocuments, bool canReadSensitive, int expected)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, companyId: null);
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            DocumentType = category == "General" ? "Certificate" : "Passport",
            DocumentCategory = category,
            FileName = "record.pdf",
            ContentType = "application/pdf",
            StorageUrl = $"{tenantId:N}/record.pdf",
        };
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync();
        var permissions = new List<string>();
        if (canRead) permissions.Add("employees.read");
        if (canManageDocuments) permissions.Add("employees.documents");
        if (canReadSensitive) permissions.Add("employees.sensitive");
        var caller = Caller(permissions.ToArray(), tenantId);
        var controller = Controller(db, caller, new UnrestrictedScope(), new ByteDocumentStorage());

        async Task<IActionResult> Invoke()
        {
            var result = await controller.DownloadDocument(document.Id, CancellationToken.None);
            return result is FileResult ? new OkResult() : result;
        }

        var status = await ProductionAuthorizationGate.StatusAsync<EmployeesController>(
            caller, nameof(EmployeesController.DownloadDocument), Invoke);

        status.Should().Be(expected);
    }

    private static string[] ExplicitPermission(Type controller, string action) =>
        controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
            .GetCustomAttribute<HasPermissionAttribute>()?.Permissions.ToArray() ?? [];

    private static async Task<int> PipelineStatus(Type controller, string action, ClaimsPrincipal caller)
    {
        var gate = typeof(ProductionAuthorizationGate)
            .GetMethod(nameof(ProductionAuthorizationGate.StatusAsync))!
            .MakeGenericMethod(controller);
        return await (Task<int>)gate.Invoke(null, new object[]
        {
            caller,
            action,
            (Func<Task<IActionResult>>)(() => Task.FromResult<IActionResult>(new OkResult())),
        })!;
    }

    private static ClaimsPrincipal Caller(params string[] permissions) => Caller(permissions, Guid.NewGuid());

    private static ClaimsPrincipal Caller(string[] permissions, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Employee Workspace Test Role"),
            new("is_group_scope", "true"),
        };
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase($"employee-workspace-auth-{Guid.NewGuid():N}")
            .Options);

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId, Guid? companyId)
    {
        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = companyId,
            EmployeeCode = $"EMP-{Guid.NewGuid():N}"[..12],
            FullName = "Workspace Employee",
            EnglishName = "Workspace Employee",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.Date.AddYears(-1),
            Salary = 50_000m,
            PassportNumber = "P1234567",
        };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static EmployeeManagementService CreateService(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static EmployeesController Controller(
        ZayraDbContext db,
        ClaimsPrincipal caller,
        IDataScopeService scope,
        IDocumentStorage? documents = null)
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            documents ?? new NullDocumentStorage(),
            TestNotifications.For(db),
            new StubHijriDateService(),
            scope,
            new StubLetterService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = caller },
        };
        return controller;
    }

    private static RequestContext Context(Guid tenantId) =>
        new("127.0.0.1", "employee-workspace-test", Guid.NewGuid(), tenantId);

    private sealed class UnrestrictedScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private sealed class FixedScope(int[] allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Team, AllowedEmployeeIds = allowed });
    }

    private sealed class ByteDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string ResolvePath(string storageUrl) => storageUrl;

        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(new byte[] { 1, 2, 3 });
    }

    private sealed class StubHijriDateService : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) =>
            new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class StubLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }
}
