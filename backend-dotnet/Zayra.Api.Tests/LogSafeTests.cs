using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Notifications;

namespace Zayra.Api.Tests;

/// <summary>
/// CodeQL flagged "Log entries created from user input" on the platform SMTP save and on the
/// SMTP send path, plus "Exposure of private information" on the recipient address. These tests
/// pin the fix so it cannot regress into a dismissed-alert-shaped hole.
///
/// The attack these defend against is log FORGERY, not log noise: the logging sink flattens the
/// message template and its arguments onto one text line, so a CRLF inside a caller-supplied
/// value produces what looks like an additional, genuine log entry.
/// </summary>
public class LogSafeTests
{
    [Theory]
    [InlineData("smtp.example.com\r\n2026-09-23 INFO auth succeeded")]
    [InlineData("smtp.example.com\n2026-09-23 INFO auth succeeded")]
    [InlineData("smtp.example.com\rINJECTED")]
    public void Text_leaves_no_line_break_for_a_forged_entry(string hostile)
    {
        var safe = LogSafe.Text(hostile);

        Assert.DoesNotContain('\r', safe);
        Assert.DoesNotContain('\n', safe);
        Assert.Single(safe.Split('\n'));
    }

    [Theory]
    [InlineData(0x85)]   // NEL — a line break to many log readers
    [InlineData(0x0b)]   // vertical tab
    [InlineData(0x0c)]   // form feed
    public void Text_strips_the_less_obvious_line_terminators(int codepoint)
    {
        // These cannot be written as \uXXXX escapes in an InlineData literal: the C# lexer
        // treats them as real line terminators in source, so the attribute will not compile.
        var safe = LogSafe.Text($"smtp{(char)codepoint}example.com");

        Assert.Equal("smtp example.com", safe);
    }

    [Fact]
    public void Text_strips_control_and_escape_characters()
    {
        var safe = LogSafe.Text("host\u001b[31mRED\u0000\u007f");

        Assert.DoesNotContain('\u001b', safe);
        Assert.DoesNotContain('\u0000', safe);
        Assert.DoesNotContain('\u007f', safe);
    }

    [Fact]
    public void Text_caps_length_so_one_field_cannot_flood_a_capped_log()
    {
        var safe = LogSafe.Text(new string('x', 5_000));

        Assert.True(safe.Length <= LogSafe.MaxLength + 1, $"was {safe.Length}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Text_never_returns_an_empty_argument(string? blank)
    {
        // An empty log argument reads as a missing field, which sends an operator hunting for a
        // logging bug that is not there.
        Assert.Equal("(none)", LogSafe.Text(blank));
    }

    [Fact]
    public void Text_preserves_an_ordinary_value_unchanged()
    {
        Assert.Equal("smtp.sendgrid.net", LogSafe.Text("smtp.sendgrid.net"));
    }

    // ── The masking path, which is what actually reaches the SMTP log lines ──────

    [Theory]
    [InlineData("victim@example.com\r\n2026-09-23 INFO auth succeeded")]
    [InlineData("a@x.com\nFORGED")]
    public void MaskEmail_output_is_safe_to_put_on_one_log_line(string hostile)
    {
        var masked = NotificationBodyPolicy.MaskEmail(hostile);

        Assert.DoesNotContain('\r', masked);
        Assert.DoesNotContain('\n', masked);
        Assert.DoesNotContain("FORGED", masked);
        Assert.DoesNotContain("auth succeeded", masked);
    }

    [Theory]
    [InlineData("noah.williams@kkdemo.com")]
    [InlineData("info@kodekinetics.com")]
    public void MaskEmail_discloses_neither_the_local_part_nor_the_domain(string address)
    {
        var at = address.IndexOf('@');
        var local = address[..at];
        var domain = address[(at + 1)..];

        var masked = NotificationBodyPolicy.MaskEmail(address);

        Assert.DoesNotContain(local, masked);
        Assert.DoesNotContain(domain[..domain.LastIndexOf('.')], masked);
        Assert.StartsWith(local[..1], masked);
    }

    [Fact]
    public void MaskEmail_never_leaks_the_LogSafe_placeholder_into_a_mask()
    {
        // Scrubbing the FRAGMENTS rather than the whole address would turn an all-whitespace
        // local part into the literal "(none)" inside what is supposed to look like an address.
        Assert.DoesNotContain("(none)", NotificationBodyPolicy.MaskEmail(" @example.com"));
        Assert.DoesNotContain("(none)", NotificationBodyPolicy.MaskEmail("\u0000@example.com"));
    }
}
