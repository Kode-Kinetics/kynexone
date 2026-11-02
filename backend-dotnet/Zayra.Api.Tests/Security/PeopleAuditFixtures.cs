using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
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
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>Shared fixtures for the People audit regression tests (PEO-xx).</summary>
internal static class PeopleAuditFixtures
{
    internal static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    internal static Employee SeedEmployee(
        ZayraDbContext db, Guid tenantId, string code, string name,
        int? managerEmployeeId = null, string status = "Active", Guid? userAccountId = null)
    {
        var employee = new Employee
        {
            TenantId            = tenantId,
            UserAccountId       = userAccountId,
            EmployeeCode        = code,
            FullName            = name,
            EnglishName         = name,
            Department          = "HR",
            Designation         = "Officer",
            Status              = status,
            ManagerEmployeeId   = managerEmployeeId,
            JoiningDate         = DateTime.UtcNow.Date.AddYears(-1),
            ProfileCompletenessScore = 10m,   // < 80 so the default insight branch matches everyone
            Salary              = 50_000m,
            BankName            = "Test Bank",
            BankIban            = "SA0000000000000000001234",
            WpsBankDetails      = "WPS-REF-001",
            PassportNumber      = "P99999999",
            IqamaNumber         = "2000000001",
        };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    /// <param name="permissions">Effective permission claims. Null gives an ESS-only principal.</param>
    internal static EmployeesController CreateController(
        ZayraDbContext db, Guid tenantId, string role, string[]? permissions = null, int? callerEmployeeId = null)
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            new NullDocumentStorage(),
            TestNotifications.For(db),
            new StubHijriDateService(),
            new DataScopeService(db),
            new StubLetterService());

        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
        };
        if (callerEmployeeId is int empId) claims.Add(new Claim("employee_id", empId.ToString()));
        claims.AddRange((permissions ?? []).Select(p => new Claim("permission", p)));

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    internal sealed class StubHijriDateService : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) =>
            new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    internal sealed class StubLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
