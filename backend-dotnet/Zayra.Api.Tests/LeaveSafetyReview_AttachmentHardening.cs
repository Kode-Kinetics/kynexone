using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Review follow-up: the document download response.</summary>
public sealed partial class LeaveSafetyReviewTests
{
    [Fact]
    public async Task The_download_is_sandboxed_by_a_content_security_policy()
    {
        var (w, leave, key) = await SeedAsync();
        var c = Download(w, key, Caller(w, "HR Officer", null, "leave.read"), new AuditService(w.Db));

        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<FileContentResult>();

        c.Response.Headers["Content-Security-Policy"].ToString().Should().Be("sandbox");
    }

    [Fact]
    public async Task Without_an_audit_service_no_file_is_served()
    {
        var (w, leave, key) = await SeedAsync();
        var c = Download(w, key, Caller(w, "HR Officer", null, "leave.read"), audit: null);

        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }
}
