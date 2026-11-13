using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Editing an opening or a requisition applies the same placement rules as creating one: a department, designation or grade it names
/// must be an active record of the caller's own organisation (400 otherwise), and the department name and designation title are taken
/// from that record, never from the typed text.
/// </summary>
public sealed class RecruitmentPlacementUpdateTests
{
    private sealed class NoNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class World
    {
        public required ZayraDbContext Db { get; init; }
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid CompanyId { get; } = Guid.NewGuid();
        public Department Dept = null!, InactiveDept = null!, OtherTenantDept = null!;
        public Designation Desig = null!, InactiveDesig = null!, OtherTenantDesig = null!;
        public Grade Grade = null!, InactiveGrade = null!;
        public JobOpening Opening = null!;
        public ManpowerRequisition Requisition = null!;
    }

    private static async Task<World> SeedAsync()
    {
        var w = new World { Db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options) };
        var other = Guid.NewGuid();
        w.Dept = new Department { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "FIN", NameEn = "Finance" };
        w.InactiveDept = new Department { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "OLD", NameEn = "Old Dept", IsActive = false };
        w.OtherTenantDept = new Department { TenantId = other, Code = "X", NameEn = "Elsewhere" };
        w.Desig = new Designation { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "ACC", TitleEn = "Accountant" };
        w.InactiveDesig = new Designation { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "OLDD", TitleEn = "Old Title", IsActive = false };
        w.OtherTenantDesig = new Designation { TenantId = other, Code = "Y", TitleEn = "Elsewhere Title" };
        w.Grade = new Grade { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "G5", Name = "Grade 5" };
        w.InactiveGrade = new Grade { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "G0", Name = "Old Grade", IsActive = false };
        w.Db.Departments.AddRange(w.Dept, w.InactiveDept, w.OtherTenantDept);
        w.Db.Designations.AddRange(w.Desig, w.InactiveDesig, w.OtherTenantDesig);
        w.Db.Grades.AddRange(w.Grade, w.InactiveGrade);
        w.Opening = new JobOpening
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, JobCode = "JO-1", Title = "Accountant", DepartmentId = w.Dept.Id, DepartmentName = "Finance",
            DesignationId = w.Desig.Id, DesignationTitle = "Accountant", Status = "Open",
        };
        w.Requisition = new ManpowerRequisition
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, RequisitionNumber = "MR-1", DepartmentId = w.Dept.Id, DepartmentName = "Finance",
            DesignationId = w.Desig.Id, DesignationTitle = "Accountant", HeadCount = 1, Status = "Draft",
        };
        w.Db.JobOpenings.Add(w.Opening);
        w.Db.ManpowerRequisitions.Add(w.Requisition);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return w;
    }

    private static T As<T>(T controller, World w) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "HR Manager"), new Claim("is_group_scope", "true"),
                }, "test")),
            },
        };
        return controller;
    }

    private static OpeningsController Openings(World w) => As(new OpeningsController(w.Db, new RecruitmentService(w.Db), new NoNotifications()), w);

    private static RequisitionsController Requisitions(World w) =>
        As(new RequisitionsController(w.Db, new RecruitmentService(w.Db), new NoNotifications(), new ApprovalWorkflowService(w.Db, new AuditService(w.Db))), w);

    private static CreateOpeningRequest OpeningEdit(Guid? dept = null, Guid? desig = null, Guid? grade = null) =>
        new(null, "Accountant", "Typed Department", "Typed Title", "FullTime", 1, "d", "r", "r", null, null, "Riyadh", "HR",
            DepartmentId: dept, DesignationId: desig, GradeId: grade);

    private static CreateRequisitionRequest RequisitionEdit(Guid? dept = null, Guid? desig = null, Guid? grade = null) =>
        new(dept, "Typed Department", desig, "Typed Title", 1, "FullTime", "Normal", "j", "s", null, null, null, null, null, "HR", GradeId: grade);

    public static IEnumerable<object[]> BadPlacements() => new[]
    {
        new object[] { "inactive department" }, new object[] { "another tenant's department" },
        new object[] { "inactive designation" }, new object[] { "another tenant's designation" },
        new object[] { "inactive grade" },
    };

    private static (Guid? Dept, Guid? Desig, Guid? Grade) Pick(World w, string bad) => bad switch
    {
        "inactive department" => (w.InactiveDept.Id, null, null),
        "another tenant's department" => (w.OtherTenantDept.Id, null, null),
        "inactive designation" => (null, w.InactiveDesig.Id, null),
        "another tenant's designation" => (null, w.OtherTenantDesig.Id, null),
        "inactive grade" => (null, null, w.InactiveGrade.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(bad)),
    };

    [Theory]
    [MemberData(nameof(BadPlacements))]
    public async Task Opening_Update_RefusesAPlacementOutsideTheActiveCatalogue(string bad)
    {
        var w = await SeedAsync();
        var (dept, desig, grade) = Pick(w, bad);

        var result = await Openings(w).Update(w.Opening.Id, OpeningEdit(dept, desig, grade), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(bad);
        w.Db.ChangeTracker.Clear();
        var stored = await w.Db.JobOpenings.AsNoTracking().SingleAsync();
        stored.DepartmentId.Should().Be(w.Dept.Id);
        stored.DesignationId.Should().Be(w.Desig.Id);
        stored.GradeId.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(BadPlacements))]
    public async Task Requisition_Update_RefusesAPlacementOutsideTheActiveCatalogue(string bad)
    {
        var w = await SeedAsync();
        var (dept, desig, grade) = Pick(w, bad);

        var result = await Requisitions(w).Update(w.Requisition.Id, RequisitionEdit(dept, desig, grade), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(bad);
        w.Db.ChangeTracker.Clear();
        var stored = await w.Db.ManpowerRequisitions.AsNoTracking().SingleAsync();
        stored.DepartmentId.Should().Be(w.Dept.Id);
        stored.DesignationId.Should().Be(w.Desig.Id);
    }

    [Fact]
    public async Task Opening_Update_TakesTheNamesFromTheCatalogue_NotTheTypedText()
    {
        var w = await SeedAsync();

        var renamedOnly = await Openings(w).Update(w.Opening.Id, OpeningEdit(), CancellationToken.None);
        renamedOnly.Should().BeOfType<OkObjectResult>();
        var kept = await w.Db.JobOpenings.AsNoTracking().SingleAsync();
        kept.DepartmentName.Should().Be("Finance", "the stored department still governs the name");
        kept.DesignationTitle.Should().Be("Accountant");

        w.Db.ChangeTracker.Clear();
        var placed = await Openings(w).Update(w.Opening.Id, OpeningEdit(w.Dept.Id, w.Desig.Id, w.Grade.Id), CancellationToken.None);
        placed.Should().BeOfType<OkObjectResult>();
        var stored = await w.Db.JobOpenings.AsNoTracking().SingleAsync();
        stored.DepartmentName.Should().Be("Finance");
        stored.DesignationTitle.Should().Be("Accountant");
        stored.GradeId.Should().Be(w.Grade.Id);
    }

    [Fact]
    public async Task Requisition_Update_TakesTheNamesFromTheCatalogue_NotTheTypedText()
    {
        var w = await SeedAsync();

        var renamedOnly = await Requisitions(w).Update(w.Requisition.Id, RequisitionEdit(), CancellationToken.None);
        renamedOnly.Should().BeOfType<OkObjectResult>();
        var kept = await w.Db.ManpowerRequisitions.AsNoTracking().SingleAsync();
        kept.DepartmentName.Should().Be("Finance", "the stored department still governs the name");
        kept.DesignationTitle.Should().Be("Accountant");

        w.Db.ChangeTracker.Clear();
        var placed = await Requisitions(w).Update(w.Requisition.Id, RequisitionEdit(w.Dept.Id, w.Desig.Id, w.Grade.Id), CancellationToken.None);
        placed.Should().BeOfType<OkObjectResult>();
        var stored = await w.Db.ManpowerRequisitions.AsNoTracking().SingleAsync();
        stored.DepartmentName.Should().Be("Finance");
        stored.DesignationTitle.Should().Be("Accountant");
        stored.GradeId.Should().Be(w.Grade.Id);
    }
}
