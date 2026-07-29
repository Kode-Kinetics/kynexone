using System.Text;
using Zayra.Api.Application.Localization;

namespace Zayra.Api.Infrastructure.Localization;

/// <summary>
/// Rule-based, fully offline Latin→Arabic transliteration (no external network, no third party).
/// Deterministic: the same input always yields the same output. Digraphs are matched before single
/// letters so "sh"/"kh"/"th" map to their correct Arabic phonemes. Output is a phonetic suggestion,
/// not a certified translation — the user reviews and edits it before accepting.
/// </summary>
public sealed class TransliterationService : ITransliterationService
{
    // Two-letter phonemes, checked before single letters.
    private static readonly (string Latin, string Arabic)[] Digraphs =
    {
        ("sh", "ش"), ("ch", "تش"), ("th", "ث"), ("kh", "خ"), ("gh", "غ"),
        ("ph", "ف"), ("dh", "ذ"), ("zh", "ژ"), ("ck", "ك"),
        ("oo", "و"), ("ou", "و"), ("ee", "ي"), ("ei", "ي"), ("ai", "اي"),
        ("aa", "ا"), ("ah", "ا"),
    };

    private static readonly Dictionary<char, string> Singles = new()
    {
        ['a'] = "ا", ['b'] = "ب", ['c'] = "ك", ['d'] = "د", ['e'] = "ي",
        ['f'] = "ف", ['g'] = "غ", ['h'] = "ه", ['i'] = "ي", ['j'] = "ج",
        ['k'] = "ك", ['l'] = "ل", ['m'] = "م", ['n'] = "ن", ['o'] = "و",
        ['p'] = "ب", ['q'] = "ق", ['r'] = "ر", ['s'] = "س", ['t'] = "ت",
        ['u'] = "و", ['v'] = "ف", ['w'] = "و", ['x'] = "كس", ['y'] = "ي",
        ['z'] = "ز",
    };

    public string ToArabic(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length * 2);
        foreach (var rawWord in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(TransliterateWord(rawWord.ToLowerInvariant()));
        }
        return sb.ToString().Trim();
    }

    private static string TransliterateWord(string word)
    {
        var sb = new StringBuilder(word.Length * 2);
        int i = 0;
        while (i < word.Length)
        {
            // Try a digraph first.
            if (i + 1 < word.Length)
            {
                var pair = word.Substring(i, 2);
                var digraph = Digraphs.FirstOrDefault(d => d.Latin == pair);
                if (digraph.Latin is not null)
                {
                    sb.Append(digraph.Arabic);
                    i += 2;
                    continue;
                }
            }

            var ch = word[i];
            if (Singles.TryGetValue(ch, out var arabic))
                sb.Append(arabic);
            else if (!char.IsLetter(ch))
                sb.Append(ch); // preserve digits, hyphens, apostrophes in names
            // unknown letters are dropped
            i++;
        }
        return sb.ToString();
    }
}
