using System.Net;
using Zayra.Api.Infrastructure.Caching;

namespace Zayra.Api.Tests;

public sealed class RedisConfigurationParserTests
{
    [Fact]
    public void Parse_RedisUri_DoesNotDuplicatePort()
    {
        var options = RedisConfigurationParser.Parse("redis://redis:6379");

        var endpoint = Assert.IsType<DnsEndPoint>(Assert.Single(options.EndPoints));
        Assert.Equal("redis", endpoint.Host);
        Assert.Equal(6379, endpoint.Port);
        Assert.False(options.Ssl);
        Assert.False(options.AbortOnConnectFail);
    }

    [Fact]
    public void Parse_SecureUri_PreservesCredentialsAndDatabase()
    {
        var options = RedisConfigurationParser.Parse("rediss://app:p%40ss@cache.example.com:6380/3");

        var endpoint = Assert.IsType<DnsEndPoint>(Assert.Single(options.EndPoints));
        Assert.Equal("cache.example.com", endpoint.Host);
        Assert.Equal(6380, endpoint.Port);
        Assert.Equal("app", options.User);
        Assert.Equal("p@ss", options.Password);
        Assert.Equal(3, options.DefaultDatabase);
        Assert.True(options.Ssl);
    }
}
