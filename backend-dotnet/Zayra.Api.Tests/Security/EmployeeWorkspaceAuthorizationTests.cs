using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
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
using Zayra.Api.Infrastructure.Organization;
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
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, companyId: null);
        var request = new EmployeesController.BulkActionRequest(
            action, "ids", [employee.Id], null, action is "delete" or "deactivate" ? "Reason" : null, null, null);

        var allowed = await Controller(db, Caller([permission], tenantId), new FixedScope([employee.Id]))
            .BulkAction(request, CreateService(db), CancellationToken.None);

        if (action == "export") allowed.Should().BeOfType<FileContentResult>();
        else allowed.Should().BeOfType<OkObjectResult>();

        foreach (var wrongPermission in new[] { "employees.approve", "employees.write", "employees.delete" }
                     .Where(candidate => candidate != permission))
        {
            var denied = await Controller(db, Caller([wrongPermission], tenantId), new FixedScope([employee.Id]))
                .BulkAction(request, CreateService(db), CancellationToken.None);

            denied.Should().BeOfType<ForbidResult>(
                $"{action} requires exactly {permission}, not the cross-action permission {wrongPermission}");
        }
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

    public static TheoryData<string, string?> SensitiveDocumentAliases => new()
    {
        { "Passport", null },
        { "passport", "" },
        { "National ID", "General" },
        { "IQAMA", "identity document" },
        { "Bank letter", null },
        { "Payslip", "pay roll" },
        { "Certificate", "IDENTITY" },
        { "Certificate", "salary and payroll" },
    };

    public static TheoryData<string, string> ProductionCatalogSensitiveDocumentTypes => new()
    {
        // Exact current documentTypesForCountry labels and statutory catalog keys.
        { "Passport Number", "Identity" },
        { "passport_number", "Identity" },
        { "National ID (Hawiyya)", "Identity" },
        { "id_number", "Identity" },
        { "Iqama (residence permit)", "Identity" },
        { "Iqama (residence permit) no.", "Identity" },
        { "iqama_number", "Identity" },
        { "Muqeem Reference", "Identity" },
        { "Muqeem number", "Identity" },
        { "muqeem_reference", "Identity" },
        { "GOSI Reference", "Identity" },
        { "gosi_reference", "Identity" },
        { "Qiwa Contract Number", "Identity" },
        { "qiwa_contract_reference", "Identity" },
        { "Work Permit Number", "Identity" },
        { "work_permit", "Identity" },
        { "Emirates ID", "Identity" },
        { "emirates_id", "Identity" },
        { "MOHRE work permit / labour card no.", "Identity" },
        { "Residence visa number", "Identity" },
        { "Visa number", "Identity" },
        { "visa_number", "Identity" },
        { "Visa File Number", "Identity" },
        { "visa_file_number", "Identity" },
        { "Labour Card Number", "Identity" },
        { "labor_card_number", "Identity" },
        { "Qatar ID (QID)", "Identity" },
        { "qid", "Identity" },
        { "Residence permit number", "Identity" },
        { "Kuwait Civil ID", "Identity" },
        { "civil_id", "Identity" },
        { "Residency (Article) number", "Identity" },
        { "Residency Number", "Identity" },
        { "residency_number", "Identity" },
        { "Oman Resident Card / Civil ID", "Identity" },
        { "Oman Resident Card / Civil ID no.", "Identity" },
        { "Bahrain CPR (Personal no.)", "Identity" },
        { "LMRA work permit number", "Identity" },
        { "LMRA work permit no.", "Identity" },
        { "Sponsor name", "Identity" },
        { "sponsor", "Identity" },

        // Exact sensitive payroll catalog labels/keys accepted as protected document types.
        { "Salary", "Payroll" },
        { "SalaryStructure", "Payroll" },
        { "Basic salary", "Payroll" },
        { "BasicSalary", "Payroll" },
        { "Housing allowance", "Payroll" },
        { "HousingAllowance", "Payroll" },
        { "Transport allowance", "Payroll" },
        { "TransportAllowance", "Payroll" },
        { "Food allowance", "Payroll" },
        { "FoodAllowance", "Payroll" },
        { "Mobile allowance", "Payroll" },
        { "MobileAllowance", "Payroll" },
        { "Other allowance", "Payroll" },
        { "OtherAllowance", "Payroll" },
        { "Fixed deduction", "Payroll" },
        { "FixedDeduction", "Payroll" },
        { "IBAN (WPS)", "Payroll" },
        { "BankIban", "Payroll" },
        { "Bank account number", "Payroll" },
        { "AccountNumber", "Payroll" },
        { "Bank routing code", "Payroll" },
        { "BankRoutingCode", "Payroll" },
        { "MOL personal ID", "Payroll" },
        { "MolId", "Payroll" },
    };

    [Theory]
    [MemberData(nameof(SensitiveDocumentAliases))]
    public async Task SensitiveDocumentClassification_MasksMetadataAndBlocksRawBytes(
        string documentType, string? documentCategory)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, companyId: null);
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            DocumentType = documentType,
            DocumentCategory = documentCategory ?? string.Empty,
            FileName = "protected-record.pdf",
            ContentType = "application/pdf",
            StorageUrl = $"{tenantId:N}/protected-record.pdf",
            Notes = "protected metadata",
        };
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync();
        if (documentCategory is null) document.DocumentCategory = null!;

        var metadata = EmployeeDocumentDto.Project(document, includeSensitive: false);
        metadata.FileName.Should().BeEmpty();
        metadata.StorageUrl.Should().BeEmpty();
        metadata.Notes.Should().BeEmpty();

        var caller = Caller(["employees.read", "employees.documents"], tenantId);
        var controller = Controller(db, caller, new FixedScope([employee.Id]), new ByteDocumentStorage());
        var download = await controller.DownloadDocument(document.Id, CancellationToken.None);

        download.Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [MemberData(nameof(ProductionCatalogSensitiveDocumentTypes))]
    public async Task ProductionCatalogSensitiveDocumentType_WithBlankCategory_MasksMetadataAndBlocksRawBytes(
        string documentType, string expectedCategory)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, companyId: null);
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            DocumentType = documentType,
            DocumentCategory = string.Empty,
            FileName = "protected-catalog-record.pdf",
            ContentType = "application/pdf",
            StorageUrl = $"{tenantId:N}/protected-catalog-record.pdf",
            Notes = "protected catalog metadata",
        };
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync();

        EmployeeDocumentSensitivity.CanonicalCategory(documentType, category: null)
            .Should().Be(expectedCategory);
        var metadata = EmployeeDocumentDto.Project(document, includeSensitive: false);
        metadata.FileName.Should().BeEmpty();
        metadata.StorageUrl.Should().BeEmpty();
        metadata.Notes.Should().BeEmpty();

        var controller = Controller(db,
            Caller(["employees.read", "employees.documents"], tenantId),
            new FixedScope([employee.Id]), new ByteDocumentStorage());
        var download = await controller.DownloadDocument(document.Id, CancellationToken.None);

        download.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void ServerCatalog_SensitiveIdentityAndPayrollLabelsAndKeys_AreProtectedDocumentTypes()
    {
        var sensitiveCatalogFields = EmployeeFieldRegistry.Catalog
            .Where(field => field.Sensitive && field.Section is "identity" or "payroll" or "salary")
            .ToList();

        sensitiveCatalogFields.Should().NotBeEmpty();
        foreach (var field in sensitiveCatalogFields)
        {
            EmployeeDocumentSensitivity.IsSensitive(field.Key, category: null)
                .Should().BeTrue($"catalog key '{field.Key}' is sensitive");
            EmployeeDocumentSensitivity.IsSensitive(field.Label, category: null)
                .Should().BeTrue($"catalog label '{field.Label}' is sensitive");
        }
    }

    [Theory]
    [InlineData("Passport Number copy")]
    [InlineData("Unverified salary discussion")]
    public void SensitiveDocumentTypeRegistry_DoesNotUseSubstringMatching(string documentType)
    {
        EmployeeDocumentSensitivity.IsSensitive(documentType, category: null).Should().BeFalse();
    }

    [Fact]
    public async Task ProductionPassportNumberUpload_WithoutClientCategory_IsCanonicalizedAsIdentity()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, companyId: null);
        var service = CreateService(db);
        var upload = new EmployeeDocumentUploadMetadata(
            "Passport Number", null, null, null, null, false, null, null);

        var document = await service.UploadDocumentAsync(
            tenantId, employee.Id, upload, FormFile("passport.pdf"), Context(tenantId), CancellationToken.None);

        document.DocumentCategory.Should().Be("Identity");
    }

    [Theory]
    [InlineData(nameof(EmployeesController.Get))]
    [InlineData(nameof(EmployeesController.EmployeeDocuments))]
    [InlineData(nameof(EmployeesController.EmployeeHistory))]
    [InlineData(nameof(EmployeesController.Readiness))]
    [InlineData(nameof(EmployeesController.GetReportingLines))]
    [InlineData(nameof(EmployeesController.ResolveHierarchy))]
    [InlineData(nameof(EmployeesController.ResolveWorkflowApprovers))]
    [InlineData(nameof(EmployeesController.DownloadDocument))]
    public async Task RestrictedScope_EmployeeReads_ReturnForbidden(string action)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, Guid.NewGuid());
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            DocumentType = "Contract",
            DocumentCategory = "General",
            FileName = "contract.pdf",
            ContentType = "application/pdf",
            StorageUrl = $"{tenantId:N}/contract.pdf",
        };
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = Controller(db, Caller(["employees.read", "employees.documents"], tenantId),
            new FixedScope([]), new ByteDocumentStorage());
        var service = CreateService(db);
        var hierarchy = new HrmHierarchyService(db, new AuditService(db));

        IActionResult result = action switch
        {
            nameof(EmployeesController.Get) => (await controller.Get(employee.Id, service, default)).Result!,
            nameof(EmployeesController.EmployeeDocuments) => (await controller.EmployeeDocuments(employee.Id, service, default)).Result!,
            nameof(EmployeesController.EmployeeHistory) => (await controller.EmployeeHistory(employee.Id, service, default)).Result!,
            nameof(EmployeesController.Readiness) => await controller.Readiness(employee.Id, default),
            nameof(EmployeesController.GetReportingLines) => (await controller.GetReportingLines(employee.Id, hierarchy, default)).Result!,
            nameof(EmployeesController.ResolveHierarchy) => (await controller.ResolveHierarchy(employee.Id, hierarchy, ct: default)).Result!,
            nameof(EmployeesController.ResolveWorkflowApprovers) => (await controller.ResolveWorkflowApprovers(employee.Id, "employee-change", hierarchy, default)).Result!,
            nameof(EmployeesController.DownloadDocument) => await controller.DownloadDocument(document.Id, default),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        result.Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(nameof(EmployeesController.UpdateEmployee))]
    [InlineData(nameof(EmployeesController.ChangeStatus))]
    [InlineData(nameof(EmployeesController.Activate))]
    [InlineData(nameof(EmployeesController.Terminate))]
    [InlineData(nameof(EmployeesController.Delete))]
    [InlineData(nameof(EmployeesController.UploadEmployeeDocument))]
    [InlineData(nameof(EmployeesController.UpdateEmployeeDocument))]
    [InlineData(nameof(EmployeesController.VerifyEmployeeDocument))]
    [InlineData(nameof(EmployeesController.RejectEmployeeDocument))]
    [InlineData(nameof(EmployeesController.ArchiveEmployeeDocument))]
    [InlineData(nameof(EmployeesController.ResolveDuplicate))]
    [InlineData(nameof(EmployeesController.ConfirmImportedBankDetails))]
    [InlineData(nameof(EmployeesController.RequestTransfer))]
    public async Task RestrictedScope_EmployeeMutations_ReturnForbidden(string action)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, Guid.NewGuid());
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            DocumentType = "Contract",
            FileName = "contract.pdf",
            ContentType = "application/pdf",
            StorageUrl = $"{tenantId:N}/contract.pdf",
        };
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync();
        var caller = Caller(
            ["employees.write", "employees.approve", "employees.delete", "employees.documents", "manager.approve"], tenantId);
        var controller = Controller(db, caller, new FixedScope([]));
        var service = CreateService(db);
        var status = new EmployeeStatusChangeRequest("Inactive", DateOnly.FromDateTime(DateTime.UtcNow), "Scope test");

        IActionResult result = action switch
        {
            nameof(EmployeesController.UpdateEmployee) => await controller.UpdateEmployee(
                employee.Id, new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow), []), default),
            nameof(EmployeesController.ChangeStatus) => (await controller.ChangeStatus(employee.Id, status, service, default)).Result!,
            nameof(EmployeesController.Activate) => (await controller.Activate(employee.Id, status with { Status = "Active" }, service, default)).Result!,
            nameof(EmployeesController.Terminate) => (await controller.Terminate(employee.Id, status with { Status = "Terminated" }, service, default)).Result!,
            nameof(EmployeesController.Delete) => await controller.Delete(employee.Id, default),
            nameof(EmployeesController.UploadEmployeeDocument) => (await controller.UploadEmployeeDocument(
                employee.Id, UploadForm("Passport"), service, default)).Result!,
            nameof(EmployeesController.UpdateEmployeeDocument) => (await controller.UpdateEmployeeDocument(
                employee.Id, document.Id, new UpdateDocumentMetadataRequest(null, null, null, null, null, null, "updated"), service, default)).Result!,
            nameof(EmployeesController.VerifyEmployeeDocument) => (await controller.VerifyEmployeeDocument(
                employee.Id, document.Id, new DocumentVerifyRequest("verified"), service, default)).Result!,
            nameof(EmployeesController.RejectEmployeeDocument) => (await controller.RejectEmployeeDocument(
                employee.Id, document.Id, new DocumentRejectRequest("rejected"), service, default)).Result!,
            nameof(EmployeesController.ArchiveEmployeeDocument) => await controller.ArchiveEmployeeDocument(
                employee.Id, document.Id, service, default),
            nameof(EmployeesController.ResolveDuplicate) => await controller.ResolveDuplicate(
                employee.Id, new ResolveDuplicateRequest("distinct", null, "Scope test"), default),
            nameof(EmployeesController.ConfirmImportedBankDetails) => await controller.ConfirmImportedBankDetails(
                employee.Id, new ConfirmBankDetailsRequest("Scope test"), default),
            nameof(EmployeesController.RequestTransfer) => (await controller.RequestTransfer(
                employee.Id, new EmployeeTransferCreateRequest(null, null, null, null,
                    DateOnly.FromDateTime(DateTime.UtcNow), "Scope test"), service, default)).Result!,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        result.Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(nameof(EmployeesController.ChangeStatus))]
    [InlineData(nameof(EmployeesController.Delete))]
    public async Task RestrictedScope_AbsentEmployee_IsNotFound_NotForbidden(string action)
    {
        // A company-scoped DataScope is a materialised id set that never contains a missing id. Checking it
        // before existence made the Chrome role matrix read every company HR's 404 as a permission refusal.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var inScope = SeedEmployee(db, tenantId, companyId);
        var caller = Caller(["employees.write", "employees.delete"], tenantId, companyIds: [companyId]);
        var controller = Controller(db, caller, new FixedScope([inScope.Id]));
        var absentId = inScope.Id + 10_000;
        var status = new EmployeeStatusChangeRequest("Inactive", DateOnly.FromDateTime(DateTime.UtcNow), "Scope test");

        IActionResult result = action switch
        {
            nameof(EmployeesController.ChangeStatus) => (await controller.ChangeStatus(absentId, status, CreateService(db), default)).Result!,
            nameof(EmployeesController.Delete) => await controller.Delete(absentId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        result.Should().BeAssignableTo<NotFoundResult>();
    }

    [Theory]
    [InlineData("current")]
    [InlineData("new")]
    [InlineData("hr")]
    public async Task RestrictedScope_TransferDecisions_ReturnForbidden(string decision)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, Guid.NewGuid());
        var transfer = new EmployeeTransferRequest
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            Status = decision == "current" ? "PendingCurrentManager"
                : decision == "new" ? "PendingNewManager" : "PendingHrApproval",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        db.EmployeeTransferRequests.Add(transfer);
        await db.SaveChangesAsync();
        var controller = Controller(db, Caller(
            ["manager.approve", "employees.approve"], tenantId, role: "Admin"), new FixedScope([]));
        var hierarchy = new HrmHierarchyService(db, new AuditService(db));

        var result = decision switch
        {
            "current" => await controller.ApproveCurrentManager(transfer.Id, default),
            "new" => await controller.ApproveNewManager(transfer.Id, default),
            "hr" => await controller.ApproveHrTransfer(transfer.Id, hierarchy, default),
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task CompanyScopedReader_CannotReadEmployeeFromAnotherCompany()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, Guid.NewGuid());
        var caller = Caller(["employees.read"], tenantId, companyIds: [Guid.NewGuid()]);
        var controller = Controller(db, caller, new UnrestrictedScope());

        var result = await controller.Get(employee.Id, CreateService(db), default);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task OrgChart_ReturnsOnlyEmployeesInsideResolvedScope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var visible = SeedEmployee(db, tenantId, Guid.NewGuid());
        var hidden = SeedEmployee(db, tenantId, Guid.NewGuid());
        hidden.ManagerEmployeeId = visible.Id;
        await db.SaveChangesAsync();
        var controller = Controller(db, Caller(["employees.read"], tenantId), new FixedScope([visible.Id]));

        var result = await controller.OrgChart(new HrmHierarchyService(db, new AuditService(db)), ct: default);

        var chart = Assert.IsAssignableFrom<IReadOnlyList<OrgChartNodeDto>>(Assert.IsType<OkObjectResult>(result).Value);
        chart.SelectMany(Flatten).Select(node => node.Id).Should().Equal(visible.Id);
    }

    [Fact]
    public async Task OrgChart_ReturnsOnlyEmployeesInsideCompanyScope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var visibleCompany = Guid.NewGuid();
        var visible = SeedEmployee(db, tenantId, visibleCompany);
        _ = SeedEmployee(db, tenantId, Guid.NewGuid());
        var caller = Caller(["employees.read"], tenantId, companyIds: [visibleCompany]);
        var controller = Controller(db, caller, new UnrestrictedScope());

        var result = await controller.OrgChart(new HrmHierarchyService(db, new AuditService(db)), ct: default);

        var chart = Assert.IsAssignableFrom<IReadOnlyList<OrgChartNodeDto>>(Assert.IsType<OkObjectResult>(result).Value);
        chart.SelectMany(Flatten).Select(node => node.Id).Should().Equal(visible.Id);
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

    private static ClaimsPrincipal Caller(
        string[] permissions,
        Guid tenantId,
        IReadOnlyCollection<Guid>? companyIds = null,
        string role = "Employee Workspace Test Role")
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
        };
        if (companyIds is null) claims.Add(new("is_group_scope", "true"));
        else claims.Add(new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new
        {
            v = 2,
            m = EntityScopeModes.Companies,
            c = companyIds,
        })));
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static IEnumerable<OrgChartNodeDto> Flatten(OrgChartNodeDto node)
    {
        yield return node;
        foreach (var child in node.DirectReports.SelectMany(Flatten)) yield return child;
    }

    private static IFormFile FormFile(string fileName)
    {
        var stream = new MemoryStream([1, 2, 3]);
        return new FormFile(stream, 0, stream.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };
    }

    private static EmployeeDocumentUploadForm UploadForm(string documentType) => new(
        documentType, null, null, null, null, false, null, null, FormFile("upload.pdf"));

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
