using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// EMPLOYEE FIELD WIRING — the guards for five defects that put a value in the wrong column or lost it
/// without saying so. Every test here FAILS on the code as it was:
///
///  1. the field catalogue bound a "work permit expiry" input to the work-permit ISSUE-DATE column;
///  2. the frontend client read the `field-catalog` endpoint as a bare array, so the object envelope always
///     resolved to null and the server's country x nationality resolver never reached a screen;
///  3. `ApplyChanges` had no `default` arm, so five edit keys were accepted with 200 and discarded;
///  4. the importer validated no headers at all, so a misspelled column lost its data in silence;
///  5. `import-preview` promised a non-applicable identity value would be "ignored, not imported" while the
///     commit path wrote it unconditionally.
/// </summary>
public class EmployeeFieldWiringTests
{
    // ── 1 + 2: catalogue <-> edit-key contract ───────────────────────────────

    /// <summary>
    /// The load-bearing contract: EVERY edit key the field catalogue can hand the modal must be a key
    /// `ApplyChanges` actually applies. This is the test that makes defect 3 impossible to reintroduce, and
    /// it is a CONTRACT test, not a snapshot — it enumerates the live registry and the live allow-list and
    /// joins them, so adding a statutory field without wiring it fails here rather than in production.
    /// </summary>
    [Fact]
    public void EveryCatalogEditKey_IsAppliableByApplyChanges()
    {
        var unwired = new List<string>();
        foreach (var d in EmployeeFieldRegistry.Catalog)
        {
            if (d.ComplianceFieldKey is null) continue;   // only statutory rows become modal inputs
            foreach (var key in new[] { EmployeesController.EmployeeEditKey(d), EmployeesController.ExpiryEditKeyFor(d) })
                if (key is not null && !EmployeesController.EditableEmployeeFields.Contains(key))
                    unwired.Add($"{d.Key} -> {key}");
        }

        unwired.Should().BeEmpty(
            "the field catalogue must never render an input whose value ApplyChanges would throw away");
    }

    /// <summary>
    /// A statutory field's expiry edit-key must name a real *Expiry* column — never an ISSUE-date one. This
    /// is defect 1 stated as an invariant on the server (the registry), so it also covers the frontend
    /// fallback, which is now required to agree with the registry.
    /// </summary>
    [Fact]
    public void ExpiryEditKeys_NeverPointAtAnIssueDateColumn()
    {
        foreach (var d in EmployeeFieldRegistry.Catalog)
        {
            var expiryKey = EmployeesController.ExpiryEditKeyFor(d);
            if (expiryKey is null) continue;
            expiryKey.Should().NotContain("IssueDate",
                $"'{d.Key}' would bind an input labelled \"<field> expiry\" to an issue-date column");
            expiryKey.Should().Contain("xpiry",
                $"'{d.Key}' binds its expiry input to '{expiryKey}', which is not an expiry column");
        }
    }

    /// <summary>
    /// Work permit and residency have NO expiry column on `Employee`, so the registry declares no ExpiryKey
    /// and the endpoint must emit `expiryEntityKey: null` for both. The frontend fallback had invented
    /// `workPermitIssueDate` / `residencyIssueDate` here, which is how a future expiry date came to be stored
    /// as an issue date in six GCC profiles.
    /// </summary>
    [Theory]
    [InlineData("WorkPermitNumber")]
    [InlineData("ResidencyNumber")]
    public void FieldsWithNoExpiryColumn_EmitNoExpiryBinding(string registryKey)
    {
        var d = EmployeeFieldRegistry.Catalog.Single(x => x.Key == registryKey);
        d.ExpiryKey.Should().BeNull();
        EmployeesController.ExpiryEditKeyFor(d).Should().BeNull();
    }

    /// <summary>
    /// Every key in the allow-list must reach a real `ApplyChanges` case. This is the direction that matters:
    /// `UpdateEmployee` rejects anything OUTSIDE the allow-list, so a key listed but not implemented would be
    /// a 200 that changed nothing — the exact failure mode the allow-list exists to end.
    ///
    /// Asserted against the switch's own `default` arm (the method now returns the keys it did not
    /// recognise), so it cannot drift from the implementation the way a second hand-written list would.
    /// </summary>
    [Fact]
    public async Task EditableEmployeeFields_AreAllHandledByApplyChanges()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployee(db, "SA", "Indian");
        var ctrl = Controller(db, tenantId);

        var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in EmployeesController.EditableEmployeeFields) changes[key] = SampleFor(key);

        var apply = typeof(EmployeesController).GetMethod("ApplyChanges",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var unknown = (IReadOnlyList<string>)apply.Invoke(ctrl, new object[] { employee, changes })!;

        unknown.Should().BeEmpty("every allow-listed key must reach a real ApplyChanges case");
    }

    // ── 3: the five silently-discarded edit fields ───────────────────────────

    [Theory]
    [InlineData("AE", "emiratesIdExpiryDate")]
    [InlineData("QA", "qidExpiryDate")]
    [InlineData("KW", "civilIdExpiryDate")]
    public async Task GccCardExpiry_IsPersisted_NotDropped(string country, string editKey)
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployee(db, country, "Indian");

        var res = await Controller(db, tenantId).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes((editKey, "2027-03-01"))), CancellationToken.None);

        res.Should().NotBeOfType<BadRequestObjectResult>();
        db.ChangeTracker.Clear();
        var saved = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
        var stored = editKey switch
        {
            "emiratesIdExpiryDate" => saved.EmiratesIdExpiryDate,
            "qidExpiryDate" => saved.QidExpiryDate,
            _ => saved.CivilIdExpiryDate,
        };
        stored.Should().Be(new DateOnly(2027, 3, 1),
            "these are fail-closed PAY gates — dropping the edit leaves the employee payroll-blocked with no way to fix it");
    }

    [Fact]
    public async Task IdNumber_IsPersisted_NotDropped()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployee(db, "SA", "Saudi");

        await Controller(db, tenantId).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(("idNumber", "1012345678"))), CancellationToken.None);

        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id))
            .IdNumber.Should().Be("1012345678");
    }

    /// <summary>`qiwaContractNumber` is a SensitiveField, so it routes to the Approval Center and is applied
    /// by ApproveChange. It was dropped there — AFTER a human approved it, which is the worst version of the
    /// defect: an audited approval that changed nothing.</summary>
    [Fact]
    public async Task QiwaContractNumber_SurvivesTheApprovalRoundTrip()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployee(db, "SA", "Saudi");
        var requester = Guid.NewGuid();

        var accepted = await Controller(db, tenantId, requester).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(("qiwaContractNumber", "QIWA-4471"))), CancellationToken.None);
        accepted.Should().BeOfType<AcceptedResult>();

        var change = await db.EmployeeChangeRequests.AsNoTracking().SingleAsync(c => c.TenantId == tenantId);
        // A different user approves — maker-checker.
        await Controller(db, tenantId, Guid.NewGuid()).ApproveChange(change.Id, CancellationToken.None);

        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id))
            .QiwaContractNumber.Should().Be("QIWA-4471");
    }

    /// <summary>An unrecognised key must be REJECTED, never dropped: 400 naming it, and no partial write of
    /// the keys that came with it.</summary>
    [Fact]
    public async Task UnknownEditKey_Is400_AndAppliesNothingAlongsideIt()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployee(db, "AE", "Indian");
        var before = employee.PreferredName;

        var res = await Controller(db, tenantId).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(
                ("preferredName", "Sam"),
                ("emiratesIdExpiryDatee", "2027-03-01"))),   // one typo
            CancellationToken.None);

        var bad = res.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.SerializeToElement(bad.Value!).GetProperty("unknownFields")
            .EnumerateArray().Select(x => x.GetString()).Should().Contain("emiratesIdExpiryDatee");
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id))
            .PreferredName.Should().Be(before, "a rejected patch must not be half-applied");
    }

    /// <summary>The switch is ordinal, so the allow-list must be too — a case-insensitive guard would admit
    /// "BankIban" and then silently drop it.</summary>
    [Fact]
    public async Task EditKeys_AreCaseSensitive_AndWrongCaseIsRejectedNotDropped()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployee(db, "AE", "Indian");

        var res = await Controller(db, tenantId).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(("PreferredName", "Sam"))), CancellationToken.None);

        res.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── 2: the field-catalog envelope contract ───────────────────────────────

    /// <summary>
    /// The endpoint returns an OBJECT with the descriptors under `fields`. The TypeScript client read
    /// `Array.isArray(r.data) ? r.data : null`, which is null for every object, so the catalogue was dead for
    /// every user. This pins BOTH halves of the contract in one test: the server's envelope property name,
    /// and the constant the client unwraps by — if either side renames, this fails.
    /// </summary>
    [Fact]
    public async Task FieldCatalog_ReturnsEnvelope_WithFieldsArray_MatchingTheClientsUnwrapKey()
    {
        await using var db = CreateDb();
        var (tenantId, _) = await SeedEmployee(db, "AE", "Indian");

        var res = (OkObjectResult)await Controller(db, tenantId)
            .FieldCatalog(null, "AE", "Indian", CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(res.Value);

        json.ValueKind.Should().Be(JsonValueKind.Object, "the response is an envelope, not a bare array");
        var clientKey = FrontendSource.FieldCatalogEnvelopeKey();
        json.TryGetProperty(clientKey, out var fields).Should().BeTrue(
            $"the client unwraps FIELD_CATALOG_ENVELOPE_KEY = '{clientKey}', which the server must emit");
        fields.ValueKind.Should().Be(JsonValueKind.Array);
        fields.GetArrayLength().Should().BeGreaterThan(0);
    }

    /// <summary>The client must not go back to treating the response as a bare array. Asserts the source no
    /// longer contains the discarding expression and does unwrap the envelope.</summary>
    [Fact]
    public void FrontendClient_UnwrapsTheEnvelope_AndDoesNotDiscardObjects()
    {
        var src = FrontendSource.FieldCatalogClient();
        src.Should().NotContain("Array.isArray(r.data) ? r.data : null",
            "that expression is null for every object the endpoint returns, which silently disables the catalogue");
        src.Should().Contain("unwrapFieldCatalog(r.data)");
    }

    /// <summary>The offline fallback must not invent an expiry binding the server does not send. These two
    /// strings ARE defect 1 as it shipped.</summary>
    [Fact]
    public void FrontendFallback_HasNoIssueDateExpiryBindings()
    {
        var src = FrontendSource.FieldCatalogClient();
        src.Should().NotContain("expiryEntityKey: 'workPermitIssueDate'");
        src.Should().NotContain("expiryEntityKey: 'residencyIssueDate'");
    }

    // ── 4: importer header validation ────────────────────────────────────────

    [Fact]
    public async Task Import_RejectsAnUnknownHeader_BeforeWritingAnyRow()
    {
        await using var db = CreateDb();
        var (tenantId, _) = await SeedEmployee(db, "SA", "Indian");
        var existing = await db.Employees.CountAsync(e => e.TenantId == tenantId);

        // 'Mobile' instead of 'Phone' — the audit's example. It used to import clean and lose the column.
        const string csv = "EmployeeCode,FullName,Mobile\nEMP-H-1,Hana Said,+966500000001\n";
        var res = await Controller(db, tenantId).Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var bad = res.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.SerializeToElement(bad.Value!);
        json.GetProperty("headerErrors").EnumerateArray().Select(e => e.GetProperty("column").GetString())
            .Should().Contain("Mobile");
        (await db.Employees.CountAsync(e => e.TenantId == tenantId)).Should().Be(existing, "nothing may be written");
    }

    [Fact]
    public async Task ImportPreview_RejectsTheSameHeadersAsCommit()
    {
        await using var db = CreateDb();
        var (tenantId, _) = await SeedEmployee(db, "SA", "Indian");

        const string csv = "EmployeeCode,FullName,Iqama No.\nEMP-H-2,Hana Said,2000000001\n";
        var res = await Controller(db, tenantId).ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        res.Should().BeOfType<BadRequestObjectResult>(
            "a dry run that accepts a file the commit rejects is worse than no dry run");
    }

    [Fact]
    public async Task Import_AcceptsAValidHeaderRow_IncludingAPartialOne()
    {
        await using var db = CreateDb();
        var (tenantId, _) = await SeedEmployee(db, "SA", "Indian");

        const string csv = "FullName,CountryCode,Nationality\nPartial Row,SA,Indian\n";
        var res = await Controller(db, tenantId).Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        res.Should().BeOfType<OkObjectResult>("a partial column set is legitimate — only an unknown column is not");
    }

    /// <summary>The full template must pass its own validator. If it did not, the one file we hand operators
    /// would be unimportable.</summary>
    [Fact]
    public void Template_HeaderRow_PassesValidation()
    {
        var template = Zayra.Api.Application.Common.Csv.Template(
            EmployeeFieldRegistry.CsvHeaders, EmployeeFieldRegistry.CsvExampleRow);
        EmployeeCsvHeaderValidator.Validate(template).Should().BeEmpty();
    }

    [Fact]
    public void Validate_FlagsADuplicatedColumn()
    {
        // Csv.Parse keys its row map by header name, so the last copy wins and the first column's values vanish.
        var problems = EmployeeCsvHeaderValidator.Validate("EmployeeCode,FullName,FullName\nA,B,C\n");
        problems.Should().ContainSingle().Which.Column.Should().Be("FullName");
    }

    [Theory]
    [InlineData("Mobile", "MobileAllowance")]     // containment
    [InlineData("Iqama No.", "IqamaNumber")]      // punctuation-insensitive edit distance
    [InlineData("WorkEmial", "WorkEmail")]        // transposition
    public void Validate_NamesTheNearestValidColumn(string typo, string expected)
    {
        EmployeeCsvHeaderValidator.NearestHeader(typo, EmployeeFieldRegistry.CsvHeaders).Should().Be(expected);
    }

    [Fact]
    public void Validate_EmptyContent_IsNotAnError()
    {
        EmployeeCsvHeaderValidator.Validate(string.Empty).Should().BeEmpty();
        EmployeeCsvHeaderValidator.Validate(null).Should().BeEmpty();
    }

    // ── 5: the preview must describe what the commit does ────────────────────

    /// <summary>
    /// The preview said a non-applicable identity value "will be ignored, not imported"; the commit writes it
    /// unconditionally. One test proves both halves: the warning text and the column it lands in.
    /// </summary>
    [Fact]
    public async Task ImportPreview_NonApplicableIdentityValue_DescribesWhatCommitActuallyDoes()
    {
        await using var db = CreateDb();
        var (tenantId, _) = await SeedEmployee(db, "SA", "Saudi");

        // A Saudi NATIONAL carrying an Iqama (the expat residence permit) — not applicable to them.
        const string csv = "EmployeeCode,FullName,CountryCode,Nationality,IqamaNumber\nEMP-PV-1,Saud Ali,SA,Saudi,2000000001\n";

        var preview = (OkObjectResult)await Controller(db, tenantId)
            .ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        var warnings = JsonSerializer.SerializeToElement(preview.Value).GetProperty("rows").EnumerateArray()
            .SelectMany(r => r.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Select(x => x.GetString() ?? string.Empty)
                : Enumerable.Empty<string>())
            .ToList();
        var applicability = warnings.Where(w => w.Contains("not applicable")).ToList();
        applicability.Should().NotBeEmpty();
        applicability.Should().NotContain(w => w.Contains("ignored, not imported"),
            "the commit path writes IqamaNumber unconditionally — the dry run must not promise otherwise");

        // And prove the commit really does write it, which is what makes the old wording false.
        await Controller(db, tenantId).Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-PV-1"))
            .IqamaNumber.Should().Be("2000000001");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private static Dictionary<string, JsonElement> Changes(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = JsonSerializer.SerializeToElement(v);
        return d;
    }

    /// <summary>A value of the JSON KIND each allow-listed key's case reads, so the coverage sweep exercises
    /// the case instead of tripping over a type mismatch inside it.</summary>
    private static JsonElement SampleFor(string key) => key switch
    {
        "managerEmployeeId" => JsonSerializer.SerializeToElement((int?)null),
        "salary" => JsonSerializer.SerializeToElement(1234.56m),
        _ => JsonSerializer.SerializeToElement(
            key.EndsWith("Date", StringComparison.Ordinal) || key is "dateOfBirth" ? "2027-03-01" : "x"),
    };

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid TenantId, Employee Employee)> SeedEmployee(ZayraDbContext db, string country, string nationality)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        var dept = new Department { TenantId = tenant.Id, Code = "OPS", NameEn = "Operations", IsActive = true };
        var desig = new Designation { TenantId = tenant.Id, Code = "OPS-OFF", TitleEn = "Operations Officer", IsActive = true };
        db.AddRange(dept, desig);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenant.Id, EmployeeCode = $"E-{Guid.NewGuid():N}"[..12], FullName = "Field Wiring",
            EnglishName = "Field Wiring", PreferredName = "FW", CountryCode = country, Nationality = nationality,
            Status = "Draft", JoiningDate = DateTime.UtcNow, DepartmentId = dept.Id, DesignationId = desig.Id,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (tenant.Id, employee);
    }

    private static EmployeesController Controller(ZayraDbContext db, Guid tenantId, Guid? userId = null)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), audit, new FakeDocs(),
            new FakeNotifications(), new FakeHijri(), new DataScopeService(db), new FakeLetters(),
            new ApprovalWorkflowService(db, audit), NullLogger<EmployeesController>.Instance,
            new EstablishmentGuardService(db));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "employees.sensitive"),
            new Claim("permission", "employees.bulk_import"),
            new Claim("permission", "organization.establishment.write"),
            new Claim("is_group_scope", "true"),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    /// <summary>
    /// Reads the TypeScript client from the repo so the contract is checked ACROSS the boundary rather than
    /// against a copy of it. A snapshot of the response would not have caught defect 2 — the server's shape
    /// was correct and stable the whole time; it was the client's reading of it that was wrong.
    /// </summary>
    private static class FrontendSource
    {
        private const string RelativePath = "frontend/src/api/employeeFieldCatalog.ts";

        public static string FieldCatalogClient()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, RelativePath);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }
            throw new FileNotFoundException(
                $"Could not locate {RelativePath} from {AppContext.BaseDirectory}. This contract test must be "
                + "run from inside the repository.");
        }

        /// <summary>The envelope property the client unwraps, read out of its own declaration.</summary>
        public static string FieldCatalogEnvelopeKey()
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                FieldCatalogClient(), @"FIELD_CATALOG_ENVELOPE_KEY\s*=\s*'([^']+)'");
            m.Success.Should().BeTrue("the client must declare FIELD_CATALOG_ENVELOPE_KEY for this contract to be checkable");
            return m.Groups[1].Value;
        }
    }

    // ── Stubs (mirrors EmployeeActivationGateTests) ──────────────────────────

    private sealed class FakeDocs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
