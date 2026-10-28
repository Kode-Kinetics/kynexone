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
    private static readonly IReadOnlyDictionary<string, string> Particles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bin"] = "بن", ["ibn"] = "بن", ["bint"] = "بنت", ["abu"] = "أبو", ["abo"] = "أبو", ["umm"] = "أم",
    };
    private static readonly IReadOnlyDictionary<string, string> Families = BuildFamilies();
    private static readonly IReadOnlyDictionary<string, string> Attributes = BuildAttributes();
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
            if (reading is null && !TryReadStructuredPart(tokens, index, out reading, out matchedParts))
                return string.Empty; // no partial result and no letter-by-letter fallback
            result.Add(reading!);
            index += matchedParts;
        }
        return string.Join(' ', result);
    }

    private static readonly string[] Articles = ["al", "el", "ash", "as", "ad", "ar", "an", "at", "az", "ath", "adh"];
    private static readonly string[] AbdPrefixes = ["abdul", "abdel", "abdal", "abdur", "abder", "abdu", "abd"];

    /// <summary>
    /// The conventional parts that are built from a rule rather than listed whole: a family name behind the article
    /// (Al-Qahtani, AlHarbi), bin / bint / abu before a name, a family name on its own (Khan, Siddiqui), and
    /// Abdul / Abd al- before one of God's attributes (Abdul Majeed). A part is only read when every word of it is in
    /// the curated lists; anything else still refuses the whole name.
    /// </summary>
    private static bool TryReadStructuredPart(string[] tokens, int index, out string? reading, out int consumed)
    {
        reading = null; consumed = 0;
        var word = tokens[index].ToLowerInvariant();
        var hasNext = index + 1 < tokens.Length;

        // bin / ibn / bint / abu / umm + the next word (which must itself be read, so a dangling particle refuses).
        if (hasNext && Particles.TryGetValue(word, out var particle)) { reading = particle; consumed = 1; return true; }

        // Abdul / Abd (al) + an attribute: one word (abdulmajeed) or two or three (abdul majeed, abd al majeed).
        foreach (var prefix in AbdPrefixes)
        {
            if (!word.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = word[prefix.Length..];
            if (rest.Length > 0 && Attributes.TryGetValue(rest, out var joined)) { reading = "عبد ال" + joined; consumed = 1; return true; }
            if (rest.Length > 0) continue;
            var at = index + 1;
            if (at < tokens.Length && Array.IndexOf(Articles, tokens[at].ToLowerInvariant()) >= 0 && at + 1 < tokens.Length) at++;
            if (at < tokens.Length && Attributes.TryGetValue(tokens[at].ToLowerInvariant(), out var attribute))
            { reading = "عبد ال" + attribute; consumed = at - index + 1; return true; }
        }

        // The article + a family name, as two words (Al Harbi) or one (AlHarbi).
        if (hasNext && Array.IndexOf(Articles, word) >= 0 && Families.TryGetValue(tokens[index + 1].ToLowerInvariant(), out var family))
        { reading = "ال" + family; consumed = 2; return true; }
        foreach (var article in Articles)
            if (word.Length > article.Length + 2 && word.StartsWith(article, StringComparison.Ordinal) && Families.TryGetValue(word[article.Length..], out var joinedFamily))
            { reading = "ال" + joinedFamily; consumed = 1; return true; }

        // A family name on its own (no article).
        if (Families.TryGetValue(word, out var bare)) { reading = bare; consumed = 1; return true; }
        return false;
    }

    /// <summary>Curated wording only (OrganizationLabelArabic); "" when any word is not covered. Never phonetic.</summary>
    public string ToArabicOrganizationLabel(string text) => OrganizationLabelArabic.Suggest(text);

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
        Add("عمر", "umar", "umer", "omar");
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

        // More common given names. TryAdd keeps a name that is already listed above untouched.
        void AddNew(string arabic, params string[] aliases) { foreach (var a in aliases) names.TryAdd(a, arabic); }
        AddNew("سعود", "saud", "soud");
        AddNew("سلمان", "salman");
        AddNew("سلطان", "sultan");
        AddNew("تركي", "turki", "turky");
        AddNew("فهد", "fahad", "fahd");
        AddNew("نواف", "nawaf");
        AddNew("مشعل", "mishal", "meshal", "mishaal");
        AddNew("بدر", "badr", "bader");
        AddNew("سعد", "saad", "sad");
        AddNew("ماجد", "majed", "majid");
        AddNew("حمد", "hamad");
        AddNew("وليد", "waleed", "walid");
        AddNew("طلال", "talal");
        AddNew("بندر", "bandar");
        AddNew("نايف", "naif", "nayef", "nayif");
        AddNew("منصور", "mansour", "mansoor", "mansur");
        AddNew("مبارك", "mubarak");
        AddNew("هشام", "hisham", "hesham");
        AddNew("عمران", "imran");
        AddNew("زيد", "zaid", "zayd");
        AddNew("جابر", "jaber", "jabir");
        AddNew("عادل", "adel", "adil");
        AddNew("هاني", "hani", "hany");
        AddNew("كريم", "karim", "kareem");
        AddNew("نبيل", "nabil", "nabeel");
        AddNew("سمير", "samir", "sameer");
        AddNew("سامي", "sami", "samy");
        AddNew("رامي", "rami", "ramy");
        AddNew("زياد", "ziad", "ziyad");
        AddNew("عمرو", "amr", "amro");
        AddNew("مازن", "mazen", "mazin");
        AddNew("جمال", "jamal");
        AddNew("كمال", "kamal");
        AddNew("نادر", "nader", "nadir");
        AddNew("وائل", "wael");
        AddNew("أيمن", "ayman");
        AddNew("عدنان", "adnan");
        AddNew("ياسين", "yasin", "yaseen");
        AddNew("إدريس", "idris", "idrees");
        AddNew("سليمان", "sulaiman", "sulayman", "suleiman", "sulaimaan");
        AddNew("داود", "dawood", "daoud", "daud");
        AddNew("هارون", "harun", "haroon");
        AddNew("أيوب", "ayub", "ayyub", "ayoub");
        AddNew("زكريا", "zakaria", "zakariya", "zakariyya");
        AddNew("إلياس", "ilyas", "elias");
        AddNew("جبريل", "jibril", "jibreel");
        AddNew("رضوان", "ridwan", "rizwan", "radwan");
        AddNew("شاهد", "shahid");
        AddNew("عرفان", "irfan");
        AddNew("نديم", "nadeem");
        AddNew("ساجد", "sajid");
        AddNew("زبير", "zubair", "zubayr");
        AddNew("عارف", "arif", "aarif");
        AddNew("فاروق", "farooq", "faruq");
        AddNew("كاشف", "kashif");
        AddNew("شعيب", "shoaib", "shuaib", "shuayb");
        AddNew("جنيد", "junaid", "junayd");
        AddNew("آصف", "asif");
        AddNew("عثمان", "usman", "uthman", "othman", "osman");
        AddNew("بكر", "bakr", "bakar", "abubakr");
        AddNew("محمود", "mahmood");
        AddNew("نورة", "noura", "nora", "norah", "nourah");
        AddNew("سارة", "sara", "sarah");
        AddNew("لمى", "lama");
        AddNew("ريم", "reem", "rim");
        AddNew("دينا", "dina", "deena");
        AddNew("هند", "hind");
        AddNew("لينا", "lina", "leena");
        AddNew("رنا", "rana");
        AddNew("منى", "mona", "muna");
        AddNew("ندى", "nada");
        AddNew("أمل", "amal");
        AddNew("عبير", "abeer");
        AddNew("آية", "aya");
        AddNew("جمانة", "jumana");
        AddNew("رهف", "rahaf");
        AddNew("غادة", "ghada");
        AddNew("شهد", "shahd");
        AddNew("رزان", "razan");
        AddNew("هيا", "haya");
        AddNew("مها", "maha");
        AddNew("سلوى", "salwa");
        AddNew("حليمة", "halima", "halimah");
        AddNew("زهراء", "zahra", "zahraa");
        AddNew("آمنة", "amna", "aamna", "amnah");
        AddNew("هاجر", "hajar", "hajer");
        AddNew("إيمان", "iman", "eman");
        AddNew("سعدية", "sadia", "saadia");
        AddNew("تسنيم", "tasneem", "tasnim");

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

    private static IReadOnlyDictionary<string, string> BuildFamilies()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string arabic, params string[] aliases) { foreach (var a in aliases) names.TryAdd(a, arabic); }
        // Gulf and Arabian tribal / family names (written with the article when it is present: ال + this).
        Add("قحطاني", "qahtani", "qahtany", "kahtani", "qahtanee");
        Add("حربي", "harbi", "harby");
        Add("عتيبي", "otaibi", "utaibi", "otaiby", "otaibe", "oteibi", "utaybi");
        Add("دوسري", "dosari", "dosary", "dossari", "dawsari");
        Add("شهري", "shehri", "shahri", "shehry", "shahry");
        Add("غامدي", "ghamdi", "ghamidi", "ghamdy");
        Add("زهراني", "zahrani", "zahrany");
        Add("شمري", "shammari", "shamari", "shammary");
        Add("مطيري", "mutairi", "mutairy", "mutayri", "mtairi");
        Add("عنزي", "anazi", "enezi", "anzi", "aniza");
        Add("سبيعي", "subaie", "subaiy", "subaie", "sebaie", "subai");
        Add("يامي", "yami", "yamy");
        Add("جهني", "juhani", "juhany", "johani");
        Add("بلوي", "balawi", "balawy");
        Add("حارثي", "harthi", "harithi", "harthy");
        Add("عسيري", "asiri", "assiri", "asiry");
        Add("حازمي", "hazmi", "hazmy");
        Add("مالكي", "maliki", "malki", "malky");
        Add("سلمي", "sulami", "solami", "sulamy");
        Add("تميمي", "tamimi", "tamimy", "temimi");
        Add("رشيدي", "rashidi", "rasheedi");
        Add("عمري", "amri", "omari", "amry");
        Add("شمراني", "shamrani", "shamrany");
        Add("أحمدي", "ahmadi", "ahmedi");
        Add("مصري", "masri", "misri", "masry");
        Add("خطيب", "khatib", "khateeb");
        Add("نجار", "najjar", "najar");
        Add("حكيم", "hakim", "hakeem");
        Add("شريف", "sharif", "shareef");
        Add("حبيب", "habib", "habeeb");
        Add("حمدان", "hamdan");
        Add("جابر", "jaber", "jabir");
        Add("هاجري", "hajri", "hajry");
        Add("غانم", "ghanem", "ghanim");
        Add("منصور", "mansour", "mansoor", "mansur");
        Add("سعود", "saud", "soud");
        // South Asian family names in their conventional Arabic spelling.
        Add("خان", "khan");
        Add("شاه", "shah");
        Add("ملك", "malik");
        Add("شيخ", "sheikh", "shaikh", "shiekh", "sheik");
        Add("صديقي", "siddiqui", "siddiqi", "sidiqui", "siddique");
        Add("قريشي", "qureshi", "quraishi", "qurashi", "koreshi");
        Add("أنصاري", "ansari", "ansary");
        Add("هاشمي", "hashmi", "hashimi");
        Add("سيد", "syed", "sayed", "sayyid");
        Add("رضا", "raza", "reza", "rida");
        Add("إقبال", "iqbal");
        Add("أكرم", "akram");
        Add("نواز", "nawaz");
        Add("ميرزا", "mirza");
        Add("فاروقي", "farooqui", "faruqi", "farooqi");
        Add("عثماني", "usmani", "osmani", "uthmani");
        Add("نقوي", "naqvi", "naqavi");
        Add("رضوي", "rizvi", "rizwi");
        Add("زيدي", "zaidi", "zaydi");
        Add("جعفري", "jafri", "jaafari", "jafry");
        Add("عباسي", "abbasi", "abbassi");
        Add("رحمن", "rahman", "rehman");
        Add("رحيم", "rahim", "raheem");
        Add("إسلام", "islam");
        Add("أختر", "akhtar", "akhter");
        Add("أسلم", "aslam");
        Add("أشرف", "ashraf");
        Add("أعظم", "azam", "azim");
        Add("بشير", "bashir", "basheer");
        Add("غلام", "ghulam", "gulam");
        Add("حيدر", "haider", "hyder", "hayder");
        Add("جميل", "jamil", "jameel");
        Add("لطيف", "latif", "lateef");
        Add("قاسم", "qasim", "kasim", "qassim");
        Add("رفيق", "rafiq", "rafique", "rafeeq");
        Add("شفيق", "shafiq", "shafique", "shafeeq");
        Add("طاهر", "tahir", "taher");
        Add("يعقوب", "yaqoob", "yaqub", "yacoob", "yakub");
        Add("ظفر", "zafar");
        Add("زاهد", "zahid", "zaheed");
        Add("ضياء", "zia", "diya");
        return names;
    }

    private static IReadOnlyDictionary<string, string> BuildAttributes()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string arabic, params string[] aliases) { foreach (var a in aliases) names.TryAdd(a, arabic); }
        // The part after "عبد ال": Abdul + this. Written as one word each (the ال is added by the caller).
        Add("رحمن", "rahman", "rehman");
        Add("رحيم", "rahim", "raheem", "rahiem");
        Add("عزيز", "aziz", "azeez");
        Add("كريم", "karim", "kareem");
        Add("ملك", "malik");
        Add("قادر", "qadir", "qader", "kadir", "qadeer");
        Add("لطيف", "latif", "lateef");
        Add("وهاب", "wahab", "wahhab");
        Add("حميد", "hamid", "hameed");
        Add("مجيد", "majid", "majeed");
        Add("سلام", "salam", "salaam");
        Add("غني", "ghani", "ghany");
        Add("جبار", "jabbar", "jabar");
        Add("صمد", "samad");
        Add("رزاق", "razzaq", "razaq", "razak", "razzak");
        Add("حكيم", "hakim", "hakeem");
        Add("باسط", "basit", "basid");
        Add("رؤوف", "rauf", "raouf", "raof", "rauff");
        Add("ولي", "wali", "waly");
        Add("ناصر", "nasser", "nasir", "naser");
        Add("باري", "bari");
        Add("غفار", "ghaffar", "ghafar");
        Add("رشيد", "rashid", "rasheed");
        Add("حليم", "halim", "haleem");
        Add("هادي", "hadi");
        Add("منان", "mannan");
        Add("محسن", "muhsin", "mohsin");
        Add("كبير", "kabir", "kabeer");
        Add("قيوم", "qayyum", "qayum");
        Add("ودود", "wadud");
        Add("فتاح", "fattah", "fatah");
        Add("حفيظ", "hafiz", "hafeez");
        Add("جليل", "jalil", "jaleel");
        Add("خالق", "khaliq", "khaleq");
        Add("واحد", "wahid", "waheed");
        Add("منعم", "monem", "moneim", "munim", "mun'im");
        Add("سميع", "samee", "sami");
        Add("بصير", "baseer", "basir");
        Add("مطلب", "muttalib", "mutalib");
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
