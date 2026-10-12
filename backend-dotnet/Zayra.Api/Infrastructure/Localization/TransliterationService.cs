using System.Text;
using System.Text.RegularExpressions;
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
    private static readonly IReadOnlyDictionary<string, string> PersonNameReadings = BuildPersonNameReadings();
    private static readonly int MaximumPersonNameParts = PersonNameReadings.Keys.Max(key => key.Count(c => c == ' ') + 1);

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

    public string ToArabicPersonName(string text)
    {
        // Never turn a truncated prefix into a suggestion for the person's whole name.
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return string.Empty;
        foreach (var character in text)
        {
            if (!IsLatinLetter(character) && !IsArabicLetter(character) && !IsArabicMark(character)
                && !char.IsWhiteSpace(character) && character is not ('-' or '\u2010' or '\u2011'))
                return string.Empty;
        }

        var normalized = text.Trim().Replace('\u2010', '-').Replace('\u2011', '-');
        if (normalized.StartsWith('-') || normalized.EndsWith('-') || Regex.IsMatch(normalized, @"-\s*-"))
            return string.Empty;
        var tokens = Regex.Split(normalized, @"[\s-]+");
        var result = new List<string>();
        for (var index = 0; index < tokens.Length;)
        {
            var token = tokens[index];
            if (IsArabicLetter(token[0]) && token.All(c => IsArabicLetter(c) || IsArabicMark(c)))
            {
                // Already supplied Arabic spelling is retained, including its diacritics.
                result.Add(token);
                index++;
                continue;
            }
            if (!token.All(IsLatinLetter)) return string.Empty; // no mixed-script word fragments

            string? reading = null;
            var matchedParts = 0;
            for (var count = Math.Min(MaximumPersonNameParts, tokens.Length - index); count > 0; count--)
            {
                var candidate = string.Join(" ", tokens, index, count).ToLowerInvariant();
                if (!PersonNameReadings.TryGetValue(candidate, out reading)) continue;
                matchedParts = count;
                break;
            }
            if (reading is null) return string.Empty; // no partial result and no letter-by-letter fallback
            result.Add(reading);
            index += matchedParts;
        }
        return string.Join(' ', result);
    }

    private static bool IsLatinLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static bool IsArabicLetter(char value) => value is >= '\u0600' and <= '\u06ff'
        && value != '\u0640' && char.IsLetter(value);
    private static bool IsArabicMark(char value) => value is >= '\u064b' and <= '\u065f' or '\u0670';

    private static IReadOnlyDictionary<string, string> BuildPersonNameReadings()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string arabic, params string[] aliases)
        {
            foreach (var alias in aliases) names.Add(alias, arabic);
        }

        // Explicit conventional readings, not legal-name verification. Ambiguous shorthand such
        // as Md, M, Amina, Sana, Hana, Said, Salim, Rashid and Hassan is deliberately not expanded.
        Add("محمد", "muhammad", "mohammed", "mohammad", "muhammed", "mohamed", "muhamed");
        Add("عمر", "umar", "omar");
        Add("علي", "ali");
        Add("أحمد", "ahmad", "ahmed");
        Add("محمود", "mahmoud", "mahmud");
        Add("مصطفى", "mustafa", "mostafa");
        Add("حسن", "hasan");
        Add("حسين", "hussein", "hussain", "husain", "husayn");
        Add("حمزة", "hamza", "hamzah");
        Add("خالد", "khalid", "khaled");
        Add("يوسف", "yusuf", "yousuf", "youssef", "yusef");
        Add("إبراهيم", "ibrahim");
        Add("إسماعيل", "ismail", "ismael");
        Add("إسحاق", "ishaq");
        Add("موسى", "musa", "moosa");
        Add("يحيى", "yahya");
        Add("بلال", "bilal");
        Add("أنس", "anas");
        Add("أنور", "anwar");
        Add("عمار", "ammar");
        Add("ياسر", "yasir", "yasser");
        Add("طارق", "tariq", "tarek");
        Add("فيصل", "faisal", "faysal");
        Add("رشيد", "rasheed");
        Add("راشد", "rashed");
        Add("ناصر", "nasser", "nasir");
        Add("سعيد", "saeed");
        Add("سليم", "saleem");
        Add("سالم", "salem");
        Add("أسامة", "osama", "usama");
        Add("مريم", "maryam", "mariam");
        Add("فاطمة", "fatima", "fatimah");
        Add("عائشة", "aisha", "aishah", "ayesha");
        Add("خديجة", "khadija", "khadijah");
        Add("زينب", "zainab", "zaynab");
        Add("حفصة", "hafsa", "hafsah");
        Add("أسماء", "asma", "asmaa");
        Add("أمينة", "ameena");
        Add("هدى", "huda", "hoda");
        Add("ليلى", "layla", "laila");
        Add("سلمى", "salma");
        Add("سمية", "sumayya", "sumayyah");
        Add("رقية", "ruqayya", "ruqayyah");
        Add("صفية", "safiya", "safiyyah");
        Add("نور", "noor", "nur");

        // Compound aliases are whole names. Their fragments (Abd/Abdul/Al/Bin) are never guessed.
        Add("عبد الله", "abdullah", "abdallah", "abd allah", "abd ullah");
        Add("عبد الرحمن", "abdulrahman", "abdurrahman", "abdelrahman", "abderrahman",
            "abdul rahman", "abdur rahman", "abdel rahman", "abder rahman", "abd al rahman", "abd el rahman",
            "abdul rehman", "abdur rehman", "abdulrehman", "abdurrehman");
        Add("عبد الرحيم", "abdulrahim", "abdurrahim", "abdelrahim", "abdul rahim", "abdur rahim", "abd al rahim");
        Add("عبد العزيز", "abdulaziz", "abdelaziz", "abdul aziz", "abdel aziz", "abd al aziz", "abd el aziz");
        Add("عبد الكريم", "abdulkarim", "abdelkarim", "abdul karim", "abdel karim", "abd al karim");
        Add("عبد الملك", "abdulmalik", "abdelmalik", "abdul malik", "abdel malik", "abd al malik");
        Add("عبد القادر", "abdulqadir", "abdelqadir", "abdul qadir", "abdel qadir", "abd al qadir");
        Add("عبد اللطيف", "abdullatif", "abdellatif", "abdul latif", "abdel latif", "abd al latif");
        Add("عبد الوهاب", "abdulwahab", "abdelwahab", "abdul wahab", "abdel wahab", "abd al wahab");
        Add("نور الدين", "nooruddin", "nuruddin", "noureddine", "noor ud din", "nur ud din", "noor al din", "nur al din");
        Add("صلاح الدين", "salahuddin", "salahuddeen", "salah ud din", "salah al din");
        return names;
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
