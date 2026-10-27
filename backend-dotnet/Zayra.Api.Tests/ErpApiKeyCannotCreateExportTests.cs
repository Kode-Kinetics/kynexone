using System.Reflection;
using FluentAssertions;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Tests;

/// <summary>An ERP API key reads and records ERP results only; it cannot create an export, so export creation is checked against the signed-in person alone.</summary>
public class ErpApiKeyCannotCreateExportTests
{
    [Fact]
    public void CreatingAnExport_DoesNotAcceptAnApiKey()
    {
        typeof(GlJournalExportsController).GetMethod(nameof(GlJournalExportsController.Create))!
            .GetCustomAttribute<ErpApiKeyAccessAttribute>().Should().BeNull();
    }

    [Fact]
    public void TheCreateCheck_CarriesNoApiKeyCreator()
    {
        typeof(JournalExportSodRequest).GetProperties().Select(p => p.Name).Should().NotContain("ApiKeyCreatorUserId");
        typeof(CostAccountingSod).GetMethod("ApiKeyHasConflictAsync").Should().BeNull();
    }
}
