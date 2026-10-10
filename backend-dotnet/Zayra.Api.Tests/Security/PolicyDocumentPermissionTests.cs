using FluentAssertions;
using Zayra.Api.Application.AI;
using Zayra.Api.Controllers;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The policy assistant (list the policy documents, ask a question grounded in them), judged by the REAL
/// seeded role bundles.
///
/// <para>THE DEFECT. Listing accepted three role names or <c>policy.documents.read</c> / <c>ai.policy.ask</c>;
/// asking accepted the role names, <c>ai.query</c> or <c>ai.policy.ask</c>. The two narrow keys were never in
/// the permission catalog, so a tenant could not extend policy-document access beyond Admin / HR Manager /
/// HR Officer — the Allow override is refused for a key the catalog does not hold — and a role the tenant
/// HAD given the AI assistant (<c>ai.query</c>) could ask questions but was refused the list of documents
/// the answers came from, on the same Assistant page.</para>
///
/// <para>Now both are one capability gated on <c>ai.query</c>, the key <c>LegacyRolePermissionResolver</c>
/// already maps this controller to. No seeded bundle other than Admin holds <c>ai.query</c>, so the
/// seeded-role outcome is unchanged; what changes is that the tenant's own grant now works.</para>
/// </summary>
public class PolicyDocumentPermissionTests
{
    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Director")]
    [InlineData("HR Manager")]
    [InlineData("HR Officer")]
    public async Task TheSeededPolicyRoles_CanListAndAsk(string role)
    {
        var (db, tenantId) = await NewTenantAsync("policy-roles");
        var caller = await CallerAsync(db, tenantId, role);

        StatusOf((await Controller(caller).List(CancellationToken.None)).Result!).Should().Be(200);
        StatusOf((await Controller(caller).Ask(new PolicyAskRequest("How many days of annual leave?"), CancellationToken.None)).Result!)
            .Should().Be(200);
    }

    [Fact]
    public async Task ARoleTheTenantGaveTheAssistant_CanListTheDocumentsItAnswersFrom()
    {
        // The tenant administrator adds "Query the AI HR assistant" to the Employee role in the Roles &
        // Permissions matrix — a catalog key, so the grant is accepted.
        var (db, tenantId) = await NewTenantAsync("policy-grant");
        (await PermissionsOfAsync(db, tenantId, "Employee")).Should().NotContain("ai.query");
        await GrantAsync(db, tenantId, "Employee", "ai.query");
        var caller = await CallerAsync(db, tenantId, "Employee");

        StatusOf((await Controller(caller).Ask(new PolicyAskRequest("Is Friday a working day?"), CancellationToken.None)).Result!)
            .Should().Be(200);
        StatusOf((await Controller(caller).List(CancellationToken.None)).Result!).Should().Be(200,
            "the list is the corpus the assistant answers from — whoever may ask may see what it read");
    }

    [Theory]
    [InlineData("Employee")]
    [InlineData("Payroll Officer")]
    public async Task ARoleWithoutTheAssistant_IsRefusedBoth(string role)
    {
        var (db, tenantId) = await NewTenantAsync("policy-deny");
        var caller = await CallerAsync(db, tenantId, role);

        StatusOf((await Controller(caller).List(CancellationToken.None)).Result!).Should().Be(403);
        StatusOf((await Controller(caller).Ask(new PolicyAskRequest("What is the bonus policy?"), CancellationToken.None)).Result!)
            .Should().Be(403);
    }

    private static PolicyDocumentController Controller(System.Security.Claims.ClaimsPrincipal caller) =>
        Bind(new PolicyDocumentController(new StubPolicyDocuments()), caller);

    /// <summary>The service is not under test — only who reaches it. No model is called.</summary>
    private sealed class StubPolicyDocuments : IPolicyDocumentService
    {
        public Task<IReadOnlyList<PolicyDocumentDto>> ListAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PolicyDocumentDto>>(new[]
            {
                new PolicyDocumentDto(Guid.NewGuid(), "Employee Handbook 2026.pdf", "application/pdf", 1024, "Ready", 12, null, DateTime.UtcNow),
            });

        public Task<PolicyAskResponse> AskAsync(Guid tenantId, string question, CancellationToken ct) =>
            Task.FromResult(new PolicyAskResponse("See section 4.", new[] { "Employee Handbook 2026.pdf" }, true));

        public Task<PolicyDocumentDto> UploadAsync(Guid tenantId, Guid? userId, Stream content, string fileName, string mimeType, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid tenantId, Guid documentId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
