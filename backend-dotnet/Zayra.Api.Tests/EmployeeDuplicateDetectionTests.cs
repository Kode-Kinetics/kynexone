using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Duplicate-person detection across create + import + merge. Laws under test: NEVER silently create a dup
/// (STRONG create 409 without ack), NEVER hard-block the batch (dup rows import as accept-never-block gaps),
/// NEVER auto-merge (human resolution only). Plus the GCC guards: sentinel-DOB skip, passport-only = probable,
/// transliteration-variance (Mohammed/Muhammad/Mohamed) = probable, cross-company scope masking.
/// </summary>
public class EmployeeDuplicateDetectionTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenant(ZayraDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = "Zayra", Slug = $"z-{id:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = id, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId, string name)
    {
        var c = new Company
        {
            TenantId = tenantId, LegalNameEn = name, CountryCode = "SA", Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static async Task<Employee> SeedEmployee(ZayraDbContext db, Guid tenantId, string code, string fullName,
        Guid? companyId = null, DateOnly? dob = null, string? iqama = null, string? emiratesId = null, string? qid = null,
        string? civilId = null, string? idNumber = null, string? passport = null, string? arabicName = null, string? nationality = null,
        string status = "Active")
    {
        var e = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = fullName, EnglishName = fullName,
            ArabicName = arabicName ?? string.Empty, CompanyId = companyId, DateOfBirth = dob,
            Nationality = nationality ?? string.Empty, Status = status, JoiningDate = DateTime.UtcNow.Date,
            IqamaNumber = iqama ?? string.Empty, EmiratesId = emiratesId ?? string.Empty, Qid = qid ?? string.Empty,
            CivilId = civilId ?? string.Empty, IdNumber = idNumber ?? string.Empty, PassportNumber = passport ?? string.Empty,
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private static DuplicateProbe Probe(string? name = null, DateOnly? dob = null, string? arabic = null,
        string? nationality = null, Guid? companyId = null, int? exclude = null, params (string key, string val)[] identity)
        => new(name, arabic, dob, nationality, companyId,
            identity.ToDictionary(x => x.key, x => x.val, StringComparer.OrdinalIgnoreCase), exclude);

    private static EmployeesController BuildController(ZayraDbContext db, Guid tenantId, string role = "Admin",
        Guid? userId = null, Claim[]? extraClaims = null)
    {
        var controller = new EmployeesController(
            db, new Pbkdf2PasswordHasher(), new AuditService(db), new FakeDocStorage2(),
            new NotificationService(db, new FakeEmailSvc2(), NullLogger<NotificationService>.Instance),
            new FakeHijriSvc2(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new FakeLetterSvc2());
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Role, role),
        };
        if (extraClaims is not null) claims.AddRange(extraClaims);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return controller;
    }

    private static EmployeeManagementService BuildService(ZayraDbContext db) => new(
        db, new AuditService(db), new FakeDocStorage2(),
        new NotificationService(db, new FakeEmailSvc2(), NullLogger<NotificationService>.Instance));

    private static T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;

    // ══════════════════════════ DETECTOR / NORMALIZER UNIT TESTS ══════════════════════════

    [Theory]
    [InlineData("iqama_number", "IqamaNumber")]
    [InlineData("emirates_id", "EmiratesId")]
    [InlineData("qid", "Qid")]
    [InlineData("civil_id", "CivilId")]
    [InlineData("id_number", "IdNumber")]
    public async Task Detector_ExactNationalIdMatch_IsStrong(string fieldKey, string column)
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var e = new Employee { TenantId = t, EmployeeCode = "E1", FullName = "Person One", Status = "Active", JoiningDate = DateTime.UtcNow.Date };
        typeof(Employee).GetProperty(column)!.SetValue(e, "9988776655");
        db.Employees.Add(e); await db.SaveChangesAsync();

        var det = new EmployeeDuplicateDetector(db);
        var matches = await det.FindAsync(t, Probe(name: "Someone Else", identity: (fieldKey, "9988776655")), CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal(DuplicateMatchTypes.Strong, matches[0].MatchType);
        Assert.Equal(e.Id, matches[0].EmployeeId);
    }

    [Fact]
    public async Task Detector_ArabicIndicDigitVariance_StillMatches()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var e = await SeedEmployee(db, t, "E1", "Ahmed", iqama: "2412345678");

        var det = new EmployeeDuplicateDetector(db);
        // Same number entered with Arabic-Indic digits.
        var matches = await det.FindAsync(t, Probe(name: "X", identity: ("iqama_number", "٢٤١٢٣٤٥٦٧٨")), CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal(DuplicateMatchTypes.Strong, matches[0].MatchType);
    }

    [Fact]
    public async Task Detector_TransliterationVariance_SameDob_IsProbable()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var dob = new DateOnly(1990, 5, 15);
        await SeedEmployee(db, t, "E1", "Mohammed Ali", dob: dob);

        var det = new EmployeeDuplicateDetector(db);
        foreach (var variant in new[] { "Muhammad Ali", "Mohamed Ali", "Ali Mohammed" })
        {
            var matches = await det.FindAsync(t, Probe(name: variant, dob: dob), CancellationToken.None);
            Assert.True(matches.Count == 1, $"expected a probable match for '{variant}'");
            Assert.Equal(DuplicateMatchTypes.Probable, matches[0].MatchType);
        }
    }

    [Fact]
    public async Task Detector_SameNameDifferentDob_NoProbable()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedEmployee(db, t, "E1", "Mohammed Ali", dob: new DateOnly(1990, 5, 15));

        var det = new EmployeeDuplicateDetector(db);
        var matches = await det.FindAsync(t, Probe(name: "Muhammad Ali", dob: new DateOnly(1991, 6, 16)), CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task Detector_SentinelDob_DoesNotDrivePossible()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var sentinel = new DateOnly(1970, 1, 1);
        await SeedEmployee(db, t, "E1", "Mohammed Ali", dob: sentinel);

        var det = new EmployeeDuplicateDetector(db);
        var matches = await det.FindAsync(t, Probe(name: "Muhammad Ali", dob: sentinel), CancellationToken.None);

        Assert.Empty(matches); // name + sentinel DOB alone must never flag (M2)
    }

    [Fact]
    public async Task Detector_DiacriticsCaseWhitespace_Fold()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var dob = new DateOnly(1988, 3, 3);
        await SeedEmployee(db, t, "E1", "José  Múñoz", dob: dob);

        var det = new EmployeeDuplicateDetector(db);
        var matches = await det.FindAsync(t, Probe(name: "jose munoz", dob: dob), CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal(DuplicateMatchTypes.Probable, matches[0].MatchType);
    }

    [Fact]
    public async Task Detector_ArabicNameDirectCompare_IsProbable()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var dob = new DateOnly(1985, 7, 7);
        // English names differ; Arabic names match → must flag via the direct Arabic compare (S5).
        await SeedEmployee(db, t, "E1", "M Ali", dob: dob, arabicName: "محمد علي");

        var det = new EmployeeDuplicateDetector(db);
        var matches = await det.FindAsync(t, Probe(name: "Totally Different", arabic: "محمد علي", dob: dob), CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal(DuplicateMatchTypes.Probable, matches[0].MatchType);
    }

    [Fact]
    public async Task Detector_ExcludeEmployeeId_ExcludesSelf()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var e = await SeedEmployee(db, t, "E1", "Ahmed", iqama: "555");

        var det = new EmployeeDuplicateDetector(db);
        var matches = await det.FindAsync(t, Probe(name: "Ahmed", exclude: e.Id, identity: ("iqama_number", "555")), CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task Detector_IsTenantWideAcrossCompanies()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var x = await SeedCompany(db, t, "CoX");
        var y = await SeedCompany(db, t, "CoY");
        await SeedEmployee(db, t, "E1", "Ahmed", companyId: y.Id, iqama: "777");

        var det = new EmployeeDuplicateDetector(db);
        // Probe declares company X but the match lives in Y — cross-company detection is the whole point.
        var matches = await det.FindAsync(t, Probe(name: "Ahmed", companyId: x.Id, identity: ("iqama_number", "777")), CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal(y.Id, matches[0].CompanyId);
    }

    [Fact]
    public async Task Detector_PassportOnly_IsProbable_PassportPlusName_IsStrong()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedEmployee(db, t, "E1", "Sami Noor", passport: "P111", dob: new DateOnly(1980, 2, 2));

        var det = new EmployeeDuplicateDetector(db);
        var alone = await det.FindAsync(t, Probe(name: "Different Human", identity: ("passport_number", "P111")), CancellationToken.None);
        Assert.Single(alone);
        Assert.Equal(DuplicateMatchTypes.Probable, alone[0].MatchType); // passports recycle → probable alone (M4)

        var corroborated = await det.FindAsync(t, Probe(name: "Sami Noor", identity: ("passport_number", "P111")), CancellationToken.None);
        Assert.Single(corroborated);
        Assert.Equal(DuplicateMatchTypes.Strong, corroborated[0].MatchType);
    }

    // ══════════════════════════ CREATE PATH ══════════════════════════

    private static EmployeeCreateRequest CreateReq(string name, DateOnly? dob = null, bool ack = false,
        params (string key, string val)[] identity)
        => new(
            EmployeeCode: null, ManualEmployeeCode: false, EnglishName: name, ArabicName: null, PreferredName: null,
            Gender: "Male", DateOfBirth: dob, Nationality: "Saudi", MaritalStatus: null, PersonalEmail: null,
            WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: null, BranchId: null,
            DepartmentId: null, DesignationId: null, GradeId: null, CostCenterId: null, JobTitle: null,
            ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null, EmploymentType: "Full-time",
            ContractType: "Unlimited", JoiningDate: DateTime.UtcNow.Date, ConfirmationDate: null, ProbationStartDate: null,
            ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
            LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null,
            ComplianceRecords: identity.Select(i => new EmployeeComplianceRecordRequest("SA", i.key, i.key, i.val, null, null, false, false)).ToList(),
            PositionId: null, AcknowledgeDuplicate: ack);

    [Fact]
    public async Task Create_StrongDuplicateWithoutAck_Refused409_NoRecordCreated()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedCompany(db, t, "Acme");
        await SeedEmployee(db, t, "EXIST-1", "Old Record", iqama: "1112223334");
        var ctrl = BuildController(db, t);

        var result = await ctrl.CreateEmployee(CreateReq("New Person", identity: ("iqama_number", "1112223334")), BuildService(db), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("possible_duplicate", JsonSerializer.Serialize(conflict.Value));
        Assert.Equal(1, await db.Employees.CountAsync(e => e.TenantId == t)); // only the pre-existing one
    }

    [Fact]
    public async Task Create_StrongDuplicateWithAck_Creates_AndAuditsOverride()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedCompany(db, t, "Acme");
        await SeedEmployee(db, t, "EXIST-1", "Old Record", iqama: "1112223334");
        var ctrl = BuildController(db, t);

        var result = await ctrl.CreateEmployee(CreateReq("New Person", ack: true, identity: ("iqama_number", "1112223334")), BuildService(db), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(2, await db.Employees.CountAsync(e => e.TenantId == t));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "employee.duplicate_override_created"));
    }

    [Fact]
    public async Task Create_NoDuplicate_CreatesCleanly()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedCompany(db, t, "Acme");
        var ctrl = BuildController(db, t);

        var result = await ctrl.CreateEmployee(CreateReq("Fresh Hire", identity: ("iqama_number", "0001112223")), BuildService(db), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(1, await db.Employees.CountAsync(e => e.TenantId == t));
        Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "employee.duplicate_override_created"));
    }

    // Regression: Saudi host-national ID (Hawiyya, fieldKey "id_number") must be mirrored to the
    // Employee.IdNumber scalar on the CREATE path — otherwise a modal-created citizen keeps IdNumber=""
    // and a repeat Hawiyya slips through un-flagged. Both records go through the real create path here
    // (the other create tests seed the scalar directly, which masked this gap).
    [Fact]
    public async Task Create_SaudiNationalIdMirrored_RepeatHawiyyaRefused409()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedCompany(db, t, "Acme");

        // #1 created through the modal/create path with a national ID compliance record.
        var first = await BuildController(db, t)
            .CreateEmployee(CreateReq("Faisal Al Saud", identity: ("id_number", "1098765432")), BuildService(db), CancellationToken.None);
        Assert.IsType<CreatedAtActionResult>(first.Result);
        // The scalar the detector matches on must have been populated by the create mirror.
        Assert.Equal("1098765432", await db.Employees.Where(e => e.TenantId == t).Select(e => e.IdNumber).SingleAsync());

        // #2 with the SAME Hawiyya, no ack → strong duplicate → 409, no second record.
        var second = await BuildController(db, t)
            .CreateEmployee(CreateReq("Different Name", identity: ("id_number", "1098765432")), BuildService(db), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Contains("possible_duplicate", JsonSerializer.Serialize(conflict.Value));
        Assert.Equal(1, await db.Employees.CountAsync(e => e.TenantId == t));
    }

    [Fact]
    public async Task DuplicateCheckEndpoint_ReturnsStrong_AlwaysOk()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedEmployee(db, t, "E1", "Ahmed", emiratesId: "784-1990-1234567-1", nationality: "UAE");
        var ctrl = BuildController(db, t);

        var result = await ctrl.DuplicateCheck(new DuplicateCheckRequest(
            "Ahmed", null, null, "UAE", null,
            new[] { new DuplicateIdentityValueDto("emirates_id", "784-1990-1234567-1") }, null), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resp = Assert.IsType<DuplicateCheckResponse>(ok.Value);
        Assert.True(resp.HasStrong);
        Assert.Single(resp.Matches);
        Assert.True(resp.Matches[0].CanView);
        Assert.Equal("E1", resp.Matches[0].EmployeeCode);
    }

    [Fact]
    public async Task DuplicateCheckEndpoint_CrossCompany_IsMasked_NoPii()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var x = await SeedCompany(db, t, "CoX");
        var y = await SeedCompany(db, t, "CoY");
        await SeedEmployee(db, t, "SECRET-1", "Hidden Person", companyId: y.Id, iqama: "24680");
        // Caller scoped to company X only (v2 entity_scope claim).
        var scopeClaim = new Claim("entity_scope", JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { x.Id } }));
        var ctrl = BuildController(db, t, extraClaims: new[] { scopeClaim });

        var result = await ctrl.DuplicateCheck(new DuplicateCheckRequest(
            "Whoever", null, null, null, x.Id,
            new[] { new DuplicateIdentityValueDto("iqama_number", "24680") }, null), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resp = Assert.IsType<DuplicateCheckResponse>(ok.Value);
        Assert.Single(resp.Matches);
        Assert.False(resp.Matches[0].CanView);
        Assert.Equal(string.Empty, resp.Matches[0].EmployeeCode);   // masked
        Assert.DoesNotContain("Hidden Person", JsonSerializer.Serialize(resp)); // no PII leak
    }

    // ══════════════════════════ IMPORT PATH ══════════════════════════

    private static EmployeesController ImportController(ZayraDbContext db, Guid tenantId) =>
        HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

    private static object Payload(IActionResult r) => Assert.IsType<OkObjectResult>(r).Value!;

    [Fact]
    public async Task Import_StrongDuplicateOfExisting_ImportsWithGap_NeverDropped()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedCompany(db, t, "Acme");
        await SeedEmployee(db, t, "EXIST-1", "Original Person", iqama: "9090909090");
        var ctrl = ImportController(db, t);

        var csv =
            "EmployeeCode,FullName,IqamaNumber,JoiningDate\n" +
            "IMP-1,Imported Person,9090909090,2024-01-01\n";
        var payload = Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        Assert.Equal(1, Prop<int>(payload, "created"));           // never dropped
        Assert.Equal(0, Prop<int>(payload, "skipped"));
        Assert.Equal(1, Prop<int>(payload, "possibleDuplicates"));

        var imported = await db.Employees.SingleAsync(e => e.EmployeeCode == "IMP-1");
        var gaps = await db.EmployeeImportGaps.Where(g => g.EmployeeId == imported.Id).Select(g => g.GapType).ToListAsync();
        Assert.Contains("dup:strong", gaps);
        Assert.Equal("NeedsAttention", imported.ReadinessState);   // advisory gap → NeedsAttention, not Blocked
        Assert.Equal(0, imported.ActivationBlockersCount);         // never touches Blocking (never-block-batch)
    }

    [Fact]
    public async Task Import_IntraFileDuplicates_FlagBothRows()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedCompany(db, t, "Acme");
        var ctrl = ImportController(db, t);

        // Two rows, same Iqama → both must be flagged (back-flag the earlier one).
        var csv =
            "EmployeeCode,FullName,IqamaNumber,JoiningDate\n" +
            "A,Person A,1234509876,2024-01-01\n" +
            "B,Person B,1234509876,2024-01-01\n";
        var payload = Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        Assert.Equal(2, Prop<int>(payload, "created"));
        Assert.Equal(2, Prop<int>(payload, "possibleDuplicates")); // BOTH rows flagged (S2)

        var a = await db.Employees.SingleAsync(e => e.EmployeeCode == "A");
        var b = await db.Employees.SingleAsync(e => e.EmployeeCode == "B");
        Assert.True(await db.EmployeeImportGaps.AnyAsync(g => g.EmployeeId == a.Id && g.GapType == "dup:strong"));
        Assert.True(await db.EmployeeImportGaps.AnyAsync(g => g.EmployeeId == b.Id && g.GapType == "dup:strong"));
    }

    [Fact]
    public async Task Import_NoDuplicates_NoDupGaps()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedCompany(db, t, "Acme");
        var ctrl = ImportController(db, t);

        var csv =
            "EmployeeCode,FullName,IqamaNumber,JoiningDate\n" +
            "A,Person A,111,2024-01-01\n" +
            "B,Person B,222,2024-01-01\n";
        var payload = Payload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        Assert.Equal(2, Prop<int>(payload, "created"));
        Assert.Equal(0, Prop<int>(payload, "possibleDuplicates"));
        Assert.False(await db.EmployeeImportGaps.AnyAsync(g => g.GapType == "dup:strong" || g.GapType == "dup:possible"));
    }

    [Fact]
    public async Task ImportPreview_ProjectsDuplicateWarning()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        await SeedCompany(db, t, "Acme");
        await SeedEmployee(db, t, "EXIST-1", "Original", iqama: "5150");
        var ctrl = ImportController(db, t);

        var csv =
            "EmployeeCode,FullName,IqamaNumber,JoiningDate\n" +
            "IMP-1,Imported,5150,2024-01-01\n";
        var payload = Payload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        var rows = Prop<List<object>>(payload, "rows");
        var warnings = Prop<List<string>>(rows[0], "warnings");
        Assert.Contains(warnings, w => w.Contains("Possible existing employee"));
    }

    // ══════════════════════════ MERGE / CONFIRM ══════════════════════════

    private static async Task<(int gapId, int empId)> SeedImportedDup(ZayraDbContext db, Guid t, string code, string name)
    {
        var e = await SeedEmployee(db, t, code, name, status: "Draft");
        db.EmployeeImportGaps.Add(new EmployeeImportGap
        {
            TenantId = t, EmployeeId = e.Id, ImportBatchId = Guid.NewGuid(), RowNumber = 2,
            GapType = "dup:strong", GapCategory = "dup", Detail = "Possible existing employee: X",
        });
        await db.SaveChangesAsync();
        return (0, e.Id);
    }

    [Fact]
    public async Task Resolve_Distinct_ClearsGap_AndAudits()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var (_, empId) = await SeedImportedDup(db, t, "DUP-1", "Ali");
        var ctrl = BuildController(db, t);

        var result = await ctrl.ResolveDuplicate(empId, new ResolveDuplicateRequest("distinct", null, "Confirmed a different person — different national ID."), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.False(await db.EmployeeImportGaps.AnyAsync(g => g.EmployeeId == empId && g.ResolvedAtUtc == null));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "employee.duplicate_confirmed_distinct"));
    }

    [Fact]
    public async Task Resolve_Distinct_RequiresReason()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var (_, empId) = await SeedImportedDup(db, t, "DUP-1", "Ali");
        var ctrl = BuildController(db, t);

        var result = await ctrl.ResolveDuplicate(empId, new ResolveDuplicateRequest("distinct", null, "  "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(await db.EmployeeImportGaps.AnyAsync(g => g.EmployeeId == empId && g.ResolvedAtUtc == null)); // unchanged
    }

    [Fact]
    public async Task Resolve_Merge_LinksSoftRemoves_CancelsApprovals_DeactivatesPayroll_Audits()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var survivor = await SeedEmployee(db, t, "SURV-1", "Real Person");
        var (_, dupId) = await SeedImportedDup(db, t, "DUP-1", "Real Person");
        // A pending approval for the duplicate + an active payroll footprint.
        db.ApprovalRequests.Add(new ApprovalRequest
        {
            TenantId = t, Status = "Pending", RequestedForEmployeeId = dupId, EntityName = "Employee",
            EntityId = dupId.ToString(), SlaHours = 24,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = t, EmployeeId = dupId, WpsEligible = true });
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure { TenantId = t, EmployeeId = dupId, IsActive = true, EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        await db.SaveChangesAsync();
        var ctrl = BuildController(db, t);

        var result = await ctrl.ResolveDuplicate(dupId, new ResolveDuplicateRequest("merge", survivor.Id, "Same person imported twice."), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var merged = await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == dupId); // soft-deleted → bypass filter
        Assert.True(merged.IsDeleted);
        Assert.Equal(survivor.Id, merged.DuplicateOfEmployeeId);
        Assert.Equal("MergedDuplicate", merged.PrivacyStatus);
        Assert.False(await db.EmployeeImportGaps.AnyAsync(g => g.EmployeeId == dupId && g.ResolvedAtUtc == null));
        Assert.All(await db.ApprovalRequests.Where(a => a.RequestedForEmployeeId == dupId).ToListAsync(), a => Assert.Equal("Cancelled", a.Status));
        Assert.All(await db.EmployeePayrollProfiles.Where(p => p.EmployeeId == dupId).ToListAsync(), p => Assert.False(p.WpsEligible));
        Assert.All(await db.EmployeeSalaryStructures.Where(s => s.EmployeeId == dupId).ToListAsync(), s => Assert.False(s.IsActive));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "employee.merged_into_existing"));
    }

    [Fact]
    public async Task Resolve_Merge_RequiresReasonAndSurvivor()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var (_, dupId) = await SeedImportedDup(db, t, "DUP-1", "Ali");
        var ctrl = BuildController(db, t);

        Assert.IsType<BadRequestObjectResult>(await ctrl.ResolveDuplicate(dupId, new ResolveDuplicateRequest("merge", null, "reason"), CancellationToken.None));
        var survivor = await SeedEmployee(db, t, "SURV-1", "Real");
        Assert.IsType<BadRequestObjectResult>(await ctrl.ResolveDuplicate(dupId, new ResolveDuplicateRequest("merge", survivor.Id, "  "), CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_Unmerge_RestoresRecord_AndReactivatesPayroll()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var survivor = await SeedEmployee(db, t, "SURV-1", "Real Person");
        var (_, dupId) = await SeedImportedDup(db, t, "DUP-1", "Real Person");
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = t, EmployeeId = dupId, WpsEligible = true });
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure { TenantId = t, EmployeeId = dupId, IsActive = true, EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        await db.SaveChangesAsync();
        var ctrl = BuildController(db, t);

        await ctrl.ResolveDuplicate(dupId, new ResolveDuplicateRequest("merge", survivor.Id, "merge"), CancellationToken.None);
        var afterMerge = await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == dupId); // soft-deleted → bypass filter
        Assert.True(afterMerge.IsDeleted);

        var result = await ctrl.ResolveDuplicate(dupId, new ResolveDuplicateRequest("unmerge", null, "wrong — twins"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var restored = await db.Employees.SingleAsync(e => e.Id == dupId);
        Assert.False(restored.IsDeleted);
        Assert.Null(restored.DuplicateOfEmployeeId);
        Assert.All(await db.EmployeePayrollProfiles.Where(p => p.EmployeeId == dupId).ToListAsync(), p => Assert.True(p.WpsEligible));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "employee.duplicate_merge_reversed"));
    }

    [Fact]
    public async Task DupGap_DoesNotAutoHeal_AndFoldsToNeedsAttention()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var e = await SeedEmployee(db, t, "E1", "Ahmed", iqama: "abc");
        db.EmployeeImportGaps.Add(new EmployeeImportGap
        {
            TenantId = t, EmployeeId = e.Id, ImportBatchId = Guid.NewGuid(), RowNumber = 2,
            GapType = "dup:strong", GapCategory = "dup", Detail = "Possible existing employee",
        });
        await db.SaveChangesAsync();

        var ready = new EmployeeReadiness("Ready", 100m, Array.Empty<ReadinessItem>(), Array.Empty<ReadinessItem>(),
            Array.Empty<ReadinessItem>(), Array.Empty<ReadinessItem>(), Array.Empty<ReadinessItem>());
        var folded = await ImportGapHealer.HealAndMergeAsync(db, e, ready, CancellationToken.None);
        await db.SaveChangesAsync();

        // dup:* must never auto-heal — only an explicit resolve stamps ResolvedAtUtc; and it folds Ready→NeedsAttention.
        Assert.True(await db.EmployeeImportGaps.AnyAsync(g => g.EmployeeId == e.Id && g.GapType == "dup:strong" && g.ResolvedAtUtc == null));
        Assert.Equal("NeedsAttention", folded.State);
    }

    // ── file-scoped fakes ─────────────────────────────────────────────────────
    private sealed class FakeDocStorage2 : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeEmailSvc2 : IEmailService
    {
        public Task SendAsync(string to, string name, string subject, string html,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeHijriSvc2 : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetterSvc2 : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
