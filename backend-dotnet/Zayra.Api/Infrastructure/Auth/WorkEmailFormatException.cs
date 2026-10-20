namespace Zayra.Api.Infrastructure.Auth;

/// <summary>Validates a complete IT-issued address independently of company email preferences.</summary>
public sealed class WorkEmailFormatException : Exception
{
    public const string Code = "work_email_invalid_format";
    public const string Text = "Enter the complete work email address issued by IT, for example name@company.com.";
    public WorkEmailFormatException() : base(Text) { }

    public static bool IsInvalid(string? address)
    {
        var value = (address ?? string.Empty).Trim();
        if (value.Length == 0) return false;
        var at = value.IndexOf('@');
        if (value.Length > 254 || at <= 0 || at > 64 || at != value.LastIndexOf('@') || value.Any(char.IsWhiteSpace)) return true;
        var local = value[..at];
        if (local.StartsWith('.') || local.EndsWith('.') || local.Contains("..")) return true;
        var labels = value[(at + 1)..].Split('.');
        return labels.Length < 2 || labels.Any(label => label.Length is 0 or > 63
            || label.StartsWith('-') || label.EndsWith('-')
            || label.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')));
    }

    public static void ThrowIfInvalid(string? address)
    {
        if (IsInvalid(address)) throw new WorkEmailFormatException();
    }
}
