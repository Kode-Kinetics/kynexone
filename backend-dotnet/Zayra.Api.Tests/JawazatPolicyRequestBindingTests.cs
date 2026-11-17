using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Controllers;

namespace Zayra.Api.Tests;

/// <summary>
/// The Jawazat policy PATCH takes a positional record. MVC validates a positional record through its constructor
/// parameters, so the [Required] attribute has to sit on the parameter; on the property MVC refuses to bind the body at
/// all, and the route answered an error for every caller. These tests run the real MVC validator over the real request
/// type, which the direct-call controller tests never reach.
/// </summary>
public sealed class JawazatPolicyRequestBindingTests
{
    private static ModelStateDictionary Validate(object model)
    {
        var provider = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IObjectModelValidator>();
        var context = new ActionContext(new DefaultHttpContext { RequestServices = provider }, new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        validator.Validate(context, null, string.Empty, model);
        return context.ModelState;
    }

    [Fact]
    public void ARequestCarryingAPolicyDocumentValidates() =>
        Assert.True(Validate(new UpdateJawazatPolicyRequest("{\"schemaVersion\":1}")).IsValid);

    [Fact]
    public void ARequestWithoutAPolicyDocumentIsRefusedAsInvalidInput()
    {
        var state = Validate(new UpdateJawazatPolicyRequest(null!));
        Assert.False(state.IsValid);
        Assert.Contains(nameof(UpdateJawazatPolicyRequest.JawazatPolicyJson), state.Keys);
    }
}
