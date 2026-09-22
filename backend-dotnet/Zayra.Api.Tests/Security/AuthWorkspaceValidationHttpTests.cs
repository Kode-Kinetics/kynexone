using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zayra.Api.Application.Auth;

namespace Zayra.Api.Tests.Security;

public sealed class AuthWorkspaceValidationHttpTests : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private AuthorizationPipelineHost _host = null!;
    private RecordingAuthService _auth = null!;
    private int _requestNumber;

    public async Task InitializeAsync()
    {
        var databaseName = $"auth-workspace-validation-{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{databaseName}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connectionString);
        await _anchor.OpenAsync();
        _auth = new RecordingAuthService();
        _host = new AuthorizationPipelineHost(connectionString, services =>
        {
            services.RemoveAll<IAuthService>();
            services.AddSingleton<IAuthService>(_auth);
        }, new Dictionary<string, string?>
        {
            ["RateLimit:LoginPermitLimit"] = "2",
            ["RateLimit:LoginWindowSeconds"] = "60"
        });
        _ = _host.CreateClient(); // boot the real Program/MVC pipeline
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _anchor.DisposeAsync();
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        object?[] workspaces = { Missing.Value, null, string.Empty, "   " };
        foreach (var workspace in workspaces)
        {
            yield return new object[] { "/api/auth/login", Json(workspace,
                "\"email\":\"user@example.test\",\"password\":\"Exact Password!\"") };
            yield return new object[] { "/api/auth/forgot-password", Json(workspace,
                "\"email\":\"user@example.test\"") };
            yield return new object[] { "/api/auth/reset-password", Json(workspace,
                "\"resetToken\":\"reset-secret\",\"newPassword\":\"Exact Password!\"") };
            yield return new object[] { "/api/auth/accept-invitation", Json(workspace,
                "\"invitationToken\":\"invite-secret\",\"newPassword\":\"Exact Password!\"") };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task MissingNullEmptyOrBlankWorkspace_Returns400BeforeService(
        string path,
        string json)
    {
        var before = _auth.TotalCalls;
        var requestNumber = Interlocked.Increment(ref _requestNumber);
        var context = await _host.Server.SendAsync(httpContext =>
        {
            // Keep the real limiter enabled without allowing the 16-row validation
            // matrix to turn its own expected 400s into unrelated 429s.
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse($"198.51.100.{requestNumber}");
            httpContext.Request.Method = "POST";
            httpContext.Request.Path = path;
            httpContext.Request.ContentType = "application/json";
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(before, _auth.TotalCalls);
    }

    [Fact]
    public async Task AuthLogin_AtConfiguredLimit_Returns429FromRealMiddleware()
    {
        const string json = "{\"email\":\"\",\"password\":\"Exact Password!\",\"tenantSlug\":\"acme\"}";

        async Task<int> SendAsync()
        {
            var context = await _host.Server.SendAsync(httpContext =>
            {
                httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.250");
                httpContext.Request.Method = "POST";
                httpContext.Request.Path = "/api/auth/login";
                httpContext.Request.ContentType = "application/json";
                httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            });
            return context.Response.StatusCode;
        }

        Assert.Equal(StatusCodes.Status400BadRequest, await SendAsync());
        Assert.Equal(StatusCodes.Status400BadRequest, await SendAsync());
        Assert.Equal(StatusCodes.Status429TooManyRequests, await SendAsync());
        Assert.Equal(0, _auth.TotalCalls);
    }

    private static string Json(object? workspace, string fields)
    {
        if (ReferenceEquals(workspace, Missing.Value)) return $"{{{fields}}}";
        var workspaceJson = workspace is null
            ? "null"
            : $"\"{workspace.ToString()!.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        return $"{{{fields},\"tenantSlug\":{workspaceJson}}}";
    }

    private sealed class Missing
    {
        public static readonly Missing Value = new();
    }

    private sealed class RecordingAuthService : IAuthService
    {
        public int TotalCalls;

        private T Enter<T>()
        {
            Interlocked.Increment(ref TotalCalls);
            throw new InvalidOperationException("Validation failed to short-circuit the auth service.");
        }

        private Task Enter()
        {
            Interlocked.Increment(ref TotalCalls);
            throw new InvalidOperationException("Validation failed to short-circuit the auth service.");
        }

        public Task<AuthLoginResult> LoginAsync(LoginRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Enter<AuthLoginResult>());
        public Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Enter<AuthResponse>());
        public Task LogoutAsync(LogoutRequest request, RequestContext context, CancellationToken cancellationToken) => Enter();
        public Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Enter<ForgotPasswordResponse>());
        public Task ResetPasswordAsync(ResetPasswordRequest request, RequestContext context, CancellationToken cancellationToken) => Enter();
        public Task<AuthResponse> AcceptInvitationAsync(AcceptInvitationRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Enter<AuthResponse>());
        public Task<AuthUserDto?> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Enter<AuthUserDto?>());
        public Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, RequestContext context, CancellationToken cancellationToken) => Enter();
        public Task<AuthResponse> CompleteMfaLoginAsync(Guid userId, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Enter<AuthResponse>());
    }
}
