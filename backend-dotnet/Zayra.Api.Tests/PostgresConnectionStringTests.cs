using FluentAssertions;
using Npgsql;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Tests;

/// <summary>
/// A URI in ConnectionStrings__Default killed a production deploy: dotnet ef could not open the
/// database, the deploy job was skipped, and nothing shipped. These pin both forms.
/// </summary>
public class PostgresConnectionStringTests
{
    [Fact]
    public void KeywordForm_IsReturnedUnchanged()
    {
        const string keyword = "Host=localhost;Port=5432;Database=zayra;Username=postgres;Password=ci";
        PostgresConnectionString.Normalize(keyword).Should().Be(keyword);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyInput_IsNotInvented(string? value)
        => PostgresConnectionString.Normalize(value).Should().BeEmpty();

    [Fact]
    public void NeonUri_BecomesSomethingNpgsqlCanActuallyParse()
    {
        // The real shape, including the channel_binding Neon appends and Npgsql has no keyword for.
        var normalised = PostgresConnectionString.Normalize(
            "postgresql://neon_user:secret@ep-falling-frost-atnilrn7.c-9.us-east-1.aws.neon.tech/kynexone_clean"
            + "?sslmode=require&channel_binding=require");

        // The assertion that matters: Npgsql parses it. This is the exact call that threw
        // "Couldn't set <host>/<db>?sslmode" with an inner KeyNotFoundException.
        var builder = new NpgsqlConnectionStringBuilder(normalised);
        builder.Host.Should().Be("ep-falling-frost-atnilrn7.c-9.us-east-1.aws.neon.tech");
        builder.Database.Should().Be("kynexone_clean");
        builder.Username.Should().Be("neon_user");
        builder.Password.Should().Be("secret");
        builder.SslMode.Should().Be(SslMode.Require);
    }

    [Fact]
    public void UnknownQueryParameters_AreDroppedNotPassedThrough()
    {
        // channel_binding is not an Npgsql keyword. Passing it through reproduces the original bug.
        var normalised = PostgresConnectionString.Normalize(
            "postgresql://u:p@host/db?sslmode=require&channel_binding=require&options=-csearch_path%3Dpublic");

        normalised.Should().NotContain("channel_binding");
        var act = () => new NpgsqlConnectionStringBuilder(normalised);
        act.Should().NotThrow();
    }

    [Fact]
    public void PercentEncodedPassword_IsDecoded()
    {
        // A password with '@' arrives as %40. Left encoded, the string parses and authentication
        // fails — the worst shape of wrong, because everything looks correct.
        var builder = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize("postgresql://u%2Ename:p%40ss%2Fword@host:5433/db"));

        builder.Username.Should().Be("u.name");
        builder.Password.Should().Be("p@ss/word");
        builder.Port.Should().Be(5433);
    }

    [Fact]
    public void PortlessUri_LeavesNpgsqlsOwnDefault()
    {
        new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize("postgresql://u:p@host/db"))
            .Port.Should().Be(5432);
    }

    [Fact]
    public void PostgresScheme_IsAcceptedAsWellAsPostgresql()
        => new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize("postgres://u:p@host/db"))
            .Database.Should().Be("db");

    [Fact]
    public void MalformedUri_IsHandedBackSoNpgsqlReportsTheOriginalText()
    {
        // Better a real Npgsql error naming what the operator typed than one invented from a
        // half-parse of it.
        const string junk = "postgresql://";
        PostgresConnectionString.Normalize(junk).Should().Be(junk);
    }
}
