namespace Zayra.Api.Application.Common;

/// <summary>
/// Makes a caller-supplied string safe to write into a log line.
///
/// Structured logging does NOT protect against this. The sink flattens the message template and
/// its arguments into one text line, so a value containing CR or LF becomes extra lines that
/// look exactly like genuine log entries — an attacker who controls an SMTP host field or an
/// email address can forge "authentication succeeded" records in the operator's own log.
/// Control characters are the same problem one layer down: a terminal reading the log can be
/// driven by escape sequences embedded in a value.
///
/// Truncation is part of the guarantee, not a convenience: without it a single field can push
/// real entries out of a capped log buffer.
/// </summary>
public static class LogSafe
{
    /// <summary>Longest run of a single untrusted value we will put in one log line.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Returns <paramref name="value"/> with every CR, LF, tab and other control character
    /// replaced by a single space, collapsed, trimmed, and capped at <see cref="MaxLength"/>.
    /// Null or whitespace becomes the literal "(none)" so an empty field cannot be mistaken for
    /// a missing log argument.
    /// </summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(none)";

        var buf = new System.Text.StringBuilder(Math.Min(value.Length, MaxLength) + 1);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            // char.IsControl covers CR, LF, tab, NUL and the C1 range. DEL is not a control
            // char by that predicate but drives terminals, so it is named explicitly.
            var scrubbed = char.IsControl(ch) || ch == '\u007f' ? ' ' : ch;
            if (scrubbed == ' ')
            {
                if (lastWasSpace || buf.Length == 0) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            buf.Append(scrubbed);
            if (buf.Length >= MaxLength) { buf.Append('…'); break; }
        }

        var result = buf.ToString().TrimEnd();
        return result.Length == 0 ? "(none)" : result;
    }
}
