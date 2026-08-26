using StackExchange.Redis;

namespace Zayra.Api.Infrastructure.Caching;

public static class RedisConfigurationParser
{
    public static ConfigurationOptions Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Redis configuration cannot be empty.", nameof(value));

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != "redis" && uri.Scheme != "rediss"))
        {
            var parsed = ConfigurationOptions.Parse(trimmed);
            parsed.AbortOnConnectFail = false;
            return parsed;
        }

        var options = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            Ssl = uri.Scheme == "rediss",
        };
        options.EndPoints.Add(uri.Host, uri.IsDefaultPort ? 6379 : uri.Port);

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var separator = uri.UserInfo.IndexOf(':');
            if (separator >= 0)
            {
                var user = Uri.UnescapeDataString(uri.UserInfo[..separator]);
                var password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
                if (!string.IsNullOrEmpty(user)) options.User = user;
                if (!string.IsNullOrEmpty(password)) options.Password = password;
            }
            else
            {
                options.Password = Uri.UnescapeDataString(uri.UserInfo);
            }
        }

        var databaseSegment = uri.AbsolutePath.Trim('/');
        if (databaseSegment.Length > 0 && int.TryParse(databaseSegment, out var database))
            options.DefaultDatabase = database;

        return options;
    }
}
