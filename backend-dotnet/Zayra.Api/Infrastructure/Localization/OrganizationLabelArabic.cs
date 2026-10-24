using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Localization;

/// <summary>
/// Curated Modern Standard Arabic wording for organisation labels: places, branches and offices, departments,
/// cost centres, job titles, grades and site words. Fully offline and deterministic.
///
/// The rule is "a reviewed phrase or nothing": a label is suggested only when every word of it is covered by an
/// entry or a pattern below. There is no letter-by-letter fallback and no partial result, so an unknown word gives
/// "" and the person types the Arabic themselves. When unsure of an entry's standard business usage, it is left out.
/// </summary>
internal static class OrganizationLabelArabic
{
    /// <summary>
    /// A business function. <c>Definite</c> stands alone and closes an idafa ("إدارة الموارد البشرية").
    /// <c>Construct</c> opens an idafa with a place ("عمليات جدة"); without it "{Place} {Function}" is not suggested.
    /// <c>Indefinite</c> follows أخصائي ("أخصائي موارد بشرية"). <c>Qualified</c> replaces Definite inside a pattern
    /// where the standalone word would read badly ("إدارة الشؤون الإدارية", not "إدارة الإدارة").
    /// <c>Department</c> is the idiomatic department name when it is not simply "إدارة {Definite}".
    /// </summary>
    private sealed record Function(string Definite, string? Construct = null, string? Indefinite = null, string? Qualified = null, string? Department = null)
    {
        public string InPattern => Qualified ?? Definite;
        public string DepartmentName => Department ?? $"إدارة {InPattern}";
    }

    private static readonly Dictionary<string, Function> Functions = BuildFunctions();
    private static readonly Dictionary<string, string> Places = BuildPlaces();
    /// <summary>Whole labels. <c>Modifiable</c>: an indefinite title that takes "أول" (Senior) or "مبتدئ" (Junior) after it.</summary>
    private static readonly Dictionary<string, (string Arabic, bool Modifiable)> Phrases = BuildPhrases();
    /// <summary>Grade tiers, as in the country-pack ladder "Grade 2 - Junior".</summary>
    private static readonly Dictionary<string, string> GradeTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Junior"] = "مبتدئ",
        ["Professional"] = "مهني",
        ["Executive"] = "تنفيذي",
        ["Senior Executive"] = "تنفيذي أول",
        ["Manager"] = "مدير",
        ["Senior Manager"] = "مدير أول",
    };

    /// <summary>"{Place} Branch" and the like: the Arabic noun goes first, the place closes the idafa.</summary>
    private static readonly (string Suffix, string Arabic)[] PlaceSuffixes =
    {
        (" Head Office", "المكتب الرئيسي في"),
        (" Headquarters", "المقر الرئيسي في"),
        (" Cost Centre", "مركز تكلفة"),
        (" Cost Center", "مركز تكلفة"),
        (" Branch", "فرع"),
        (" Office", "مكتب"),
        (" Warehouse", "مستودع"),
        (" Factory", "مصنع"),
        (" Site", "موقع"),
    };

    /// <summary>"{Function} Department" and the like.</summary>
    private static readonly (string Suffix, Func<Function, string?> Make)[] FunctionSuffixes =
    {
        (" Department", f => f.DepartmentName),
        (" Cost Centre", f => $"مركز تكلفة {f.InPattern}"),
        (" Cost Center", f => $"مركز تكلفة {f.InPattern}"),
        (" Section", f => $"قسم {f.InPattern}"),
        (" Unit", f => $"وحدة {f.InPattern}"),
        (" Team", f => $"فريق {f.InPattern}"),
        (" Manager", f => $"مدير {f.InPattern}"),
        (" Director", f => $"مدير {f.DepartmentName}"),
        (" Supervisor", f => $"مشرف {f.InPattern}"),
        (" Coordinator", f => $"منسق {f.InPattern}"),
        (" Specialist", f => f.Indefinite is null ? null : $"أخصائي {f.Indefinite}"),
        (" Officer", f => f.Indefinite is null ? null : $"أخصائي {f.Indefinite}"),
    };

    /// <summary>"Department of {Function}", "Manager of {Function}" and the like.</summary>
    private static readonly (string Prefix, Func<Function, string?> Make)[] FunctionPrefixes =
    {
        ("Department of ", f => f.DepartmentName),
        ("Manager of ", f => $"مدير {f.InPattern}"),
        ("Director of ", f => $"مدير {f.DepartmentName}"),
        ("Supervisor of ", f => $"مشرف {f.InPattern}"),
        ("Assistant Manager of ", f => $"مساعد مدير {f.InPattern}"),
    };

    private static readonly Regex NumberedLabel = new(@"^(Grade|Level|Gate|Building|Floor)\s*(\d{1,3})(?:\s*[-–—:]\s*(.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string Suggest(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return string.Empty;
        var label = Regex.Replace(text.Trim(), @"\s+", " ");
        return Exact(label) ?? Numbered(label) ?? Modified(label) ?? PlaceLabel(label) ?? FunctionLabel(label) ?? PlaceFunction(label) ?? string.Empty;
    }

    private static string? Exact(string label)
    {
        if (Phrases.TryGetValue(label, out var phrase)) return phrase.Arabic;
        if (Functions.TryGetValue(label, out var function)) return function.Definite;
        if (GradeTiers.TryGetValue(label, out var tier)) return tier;
        return Place(label);
    }

    private static string? Numbered(string label)
    {
        var m = NumberedLabel.Match(label);
        if (!m.Success) return null;
        var number = m.Groups[2].Value;
        var noun = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "grade" => "الدرجة",
            "level" => "المستوى",
            "gate" => "البوابة",
            "building" => "المبنى",
            _ => "الطابق",
        };
        if (!m.Groups[3].Success) return $"{noun} {number}";
        // "Grade 2 - Junior": only a grade or level carries a tier, and only a known one. No partial label.
        if (noun is not ("الدرجة" or "المستوى")) return null;
        return GradeTiers.TryGetValue(m.Groups[3].Value.Trim(), out var tier) ? $"{noun} {number} - {tier}" : null;
    }

    /// <summary>"Senior Accountant" → "محاسب أول"; only for indefinite titles where the adjective follows cleanly.</summary>
    private static string? Modified(string label)
    {
        foreach (var (prefix, arabic) in new[] { ("Senior ", "أول"), ("Junior ", "مبتدئ") })
        {
            if (!label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = label[prefix.Length..].Trim();
            return Phrases.TryGetValue(rest, out var title) && title.Modifiable ? $"{title.Arabic} {arabic}" : null;
        }
        return null;
    }

    private static string? PlaceLabel(string label)
    {
        foreach (var (suffix, arabic) in PlaceSuffixes)
        {
            if (!label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var place = Place(label[..^suffix.Length]);
            if (place is not null) return $"{arabic} {place}";
        }
        return null;
    }

    private static string? FunctionLabel(string label)
    {
        foreach (var (suffix, make) in FunctionSuffixes)
        {
            if (!label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var head = label[..^suffix.Length].Trim();
            // "Assistant HR Manager" → "مساعد مدير الموارد البشرية".
            if (suffix == " Manager" && head.StartsWith("Assistant ", StringComparison.OrdinalIgnoreCase)
                && Functions.TryGetValue(head["Assistant ".Length..].Trim(), out var assisted))
                return $"مساعد مدير {assisted.InPattern}";
            if (Functions.TryGetValue(head, out var function)) return make(function);
        }
        foreach (var (prefix, make) in FunctionPrefixes)
        {
            if (!label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var tail = label[prefix.Length..].Trim();
            if (tail.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) tail = tail[4..].Trim();
            if (Functions.TryGetValue(tail, out var function)) return make(function);
        }
        return null;
    }

    /// <summary>"Jeddah Operations" → "عمليات جدة". Only functions with a construct form; others give nothing.</summary>
    private static string? PlaceFunction(string label)
    {
        var words = label.Split(' ');
        for (var split = words.Length - 1; split >= 1; split--)
        {
            var place = Place(string.Join(' ', words[..split]));
            if (place is null) continue;
            return Functions.TryGetValue(string.Join(' ', words[split..]), out var function) && function.Construct is { } construct
                ? $"{construct} {place}"
                : null;
        }
        return null;
    }

    private static string? Place(string name) => Places.TryGetValue(PlaceKey(name), out var arabic) ? arabic : null;

    /// <summary>One key for "Al-Olaya", "Al Olaya", "olaya" and "Ha'il"/"Hail": the Arabic already carries ال.</summary>
    private static string PlaceKey(string name)
    {
        var key = Regex.Replace(name.Trim().ToLowerInvariant().Replace('-', ' ').Replace("'", "").Replace("’", ""), @"\s+", " ");
        foreach (var article in new[] { "al ", "el " })
            if (key.StartsWith(article, StringComparison.Ordinal) && key.Length > article.Length) return key[article.Length..];
        return key;
    }

    private static Dictionary<string, Function> BuildFunctions()
    {
        var f = new Dictionary<string, Function>(StringComparer.OrdinalIgnoreCase);
        void Add(Function function, params string[] names) { foreach (var name in names) f[name] = function; }
        Add(new("العمليات", Construct: "عمليات", Indefinite: "عمليات"), "Operations");
        Add(new("الهندسة"), "Engineering");
        Add(new("الموارد البشرية", Indefinite: "موارد بشرية"), "Human Resources", "HR");
        Add(new("المالية", Department: "الإدارة المالية"), "Finance");
        Add(new("تقنية المعلومات", Indefinite: "تقنية معلومات"), "Information Technology", "IT");
        Add(new("المبيعات", Construct: "مبيعات", Indefinite: "مبيعات"), "Sales");
        Add(new("التسويق", Indefinite: "تسويق"), "Marketing");
        Add(new("المشتريات", Construct: "مشتريات", Indefinite: "مشتريات"), "Procurement", "Purchasing");
        Add(new("الشؤون القانونية"), "Legal", "Legal Affairs");
        Add(new("المحاسبة", Indefinite: "محاسبة"), "Accounting");
        Add(new("الحسابات"), "Accounts");
        Add(new("الرواتب", Indefinite: "رواتب"), "Payroll");
        Add(new("خدمة العملاء", Indefinite: "خدمة عملاء"), "Customer Service");
        Add(new("الإدارة", Qualified: "الشؤون الإدارية"), "Administration", "Admin");
        Add(new("الشؤون الإدارية"), "Administrative Affairs");
        Add(new("ضمان الجودة"), "Quality Assurance", "QA");
        Add(new("الجودة", Indefinite: "جودة"), "Quality");
        Add(new("مراقبة الجودة"), "Quality Control");
        Add(new("البحث والتطوير"), "Research and Development", "Research & Development", "R&D");
        Add(new("الخدمات اللوجستية"), "Logistics");
        Add(new("الصيانة", Construct: "صيانة", Indefinite: "صيانة"), "Maintenance");
        Add(new("الإنتاج", Indefinite: "إنتاج"), "Production");
        Add(new("الأمن"), "Security");
        Add(new("السلامة", Indefinite: "سلامة"), "Safety");
        Add(new("الصحة والسلامة والبيئة"), "Health, Safety and Environment", "HSE");
        Add(new("العلاقات الحكومية", Indefinite: "علاقات حكومية"), "Government Relations");
        Add(new("العلاقات العامة", Indefinite: "علاقات عامة"), "Public Relations");
        Add(new("تطوير الأعمال", Indefinite: "تطوير أعمال"), "Business Development");
        Add(new("المشاريع", Construct: "مشاريع", Indefinite: "مشاريع"), "Projects");
        Add(new("المراجعة الداخلية"), "Internal Audit");
        Add(new("الالتزام", Indefinite: "التزام"), "Compliance");
        Add(new("المخاطر"), "Risk");
        Add(new("الخزينة"), "Treasury");
        Add(new("الاستراتيجية"), "Strategy");
        Add(new("المرافق"), "Facilities");
        Add(new("سلسلة الإمداد", Indefinite: "سلسلة إمداد"), "Supply Chain");
        Add(new("التدريب", Indefinite: "تدريب"), "Training");
        Add(new("التوظيف", Indefinite: "توظيف"), "Recruitment");
        Add(new("الزكاة والضريبة"), "Zakat and Tax");
        return f;
    }

    private static Dictionary<string, (string, bool)> BuildPhrases()
    {
        var p = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        void Add(string arabic, params string[] names) { foreach (var name in names) p[name] = (arabic, false); }
        void Title(string arabic, params string[] names) { foreach (var name in names) p[name] = (arabic, true); }

        // Sites and offices.
        Add("المكتب الرئيسي", "Head Office", "Main Office");
        Add("المقر الرئيسي", "Headquarters", "HQ");
        Add("الفرع الرئيسي", "Main Branch");
        Add("البوابة الرئيسية", "Main Gate");
        Add("المدخل الرئيسي", "Main Entrance");
        Add("المستودع", "Warehouse");
        Add("المستودع الرئيسي", "Main Warehouse");
        Add("المصنع", "Factory");
        Add("الاستقبال", "Reception");
        Add("مكتب الموقع", "Site Office");

        // Leadership.
        Add("الرئيس التنفيذي", "Chief Executive Officer", "CEO");
        Add("المدير المالي", "Chief Financial Officer", "CFO");
        Add("الرئيس التنفيذي للعمليات", "Chief Operating Officer", "COO");
        Add("الرئيس التنفيذي للتقنية", "Chief Technology Officer", "CTO");
        Add("المدير العام", "General Manager", "GM");
        Add("نائب المدير العام", "Deputy General Manager");
        Add("العضو المنتدب", "Managing Director");
        Add("رئيس مجلس الإدارة", "Chairman", "Chairman of the Board");
        Add("عضو مجلس الإدارة", "Board Member");
        Title("مدير", "Manager");
        Add("مساعد مدير", "Assistant Manager");
        Add("مدير المكتب", "Office Manager");
        Add("مدير الفرع", "Branch Manager");
        Title("مدير مشروع", "Project Manager");
        Title("مشرف", "Supervisor");
        Add("قائد فريق", "Team Leader", "Team Lead");
        Add("رئيس عمال", "Foreman");

        // Professional and technical.
        Title("محاسب", "Accountant");
        Title("محاسب تكاليف", "Cost Accountant");
        Title("محاسب عام", "General Accountant");
        Title("مراجع داخلي", "Internal Auditor");
        Title("مهندس", "Engineer");
        Title("مهندس برمجيات", "Software Engineer");
        Add("مهندس تقنية المعلومات", "IT Engineer", "Information Technology Engineer");
        Title("مهندس شبكات", "Network Engineer");
        Title("مهندس مدني", "Civil Engineer");
        Title("مهندس ميكانيكي", "Mechanical Engineer");
        Title("مهندس كهربائي", "Electrical Engineer");
        Title("مهندس موقع", "Site Engineer");
        Title("مطور برمجيات", "Software Developer");
        Title("مطور", "Developer");
        Title("محلل بيانات", "Data Analyst");
        Title("محلل أعمال", "Business Analyst");
        Title("محلل مالي", "Financial Analyst");
        Title("محلل نظم", "Systems Analyst");
        Title("أخصائي دعم فني", "IT Support Specialist", "IT Support");
        Title("أخصائي", "Specialist");
        Title("منسق", "Coordinator");
        Title("مستشار", "Consultant");
        Title("مستشار قانوني", "Legal Advisor", "Legal Counsel");
        Add("محامٍ", "Lawyer");
        Title("مترجم", "Translator");
        Add("أخصائي موارد بشرية", "HR Officer", "Human Resources Officer");
        Add("أخصائي توظيف", "Recruiter");
        Add("مسؤول السلامة", "Safety Officer");
        Add("مسؤول الصحة والسلامة والبيئة", "HSE Officer");
        Add("مسؤول العلاقات الحكومية", "Government Relations Officer");
        Add("مسؤول الالتزام", "Compliance Officer");
        Title("مفتش جودة", "Quality Inspector");
        Title("ممرض", "Nurse");
        Add("طبيب", "Doctor");
        Title("صيدلي", "Pharmacist");
        Title("معلم", "Teacher");

        // Sales, service and administration.
        Title("مندوب مبيعات", "Sales Representative", "Salesman");
        Add("مسؤول مبيعات", "Sales Executive");
        Add("ممثل خدمة العملاء", "Customer Service Representative");
        Add("مساعد إداري", "Admin Assistant", "Administrative Assistant");
        Add("موظف إداري", "Admin Officer", "Administrative Officer");
        Add("سكرتير", "Secretary");
        Add("سكرتير تنفيذي", "Executive Secretary");
        Add("موظف استقبال", "Receptionist");
        Add("أمين صندوق", "Cashier");
        Add("أمين مستودع", "Storekeeper", "Store Keeper", "Warehouse Keeper");
        Add("مراسل", "Messenger");

        // Trades and operations.
        Title("فني", "Technician");
        Title("كهربائي", "Electrician");
        Title("سباك", "Plumber");
        Title("ميكانيكي", "Mechanic");
        Title("نجار", "Carpenter");
        Title("لحام", "Welder");
        Add("سائق", "Driver");
        Add("حارس أمن", "Security Guard");
        Add("عامل نظافة", "Cleaner");
        Add("عامل", "Labourer", "Laborer", "Worker");
        Add("مشغل", "Operator");
        Add("مشغل معدات", "Equipment Operator");
        Title("طباخ", "Cook");
        Add("نادل", "Waiter");
        Add("متدرب", "Intern", "Trainee");
        return p;
    }

    private static Dictionary<string, string> BuildPlaces()
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string arabic, params string[] names) { foreach (var name in names) p[PlaceKey(name)] = arabic; }

        // Countries.
        Add("المملكة العربية السعودية", "Saudi Arabia", "KSA");
        Add("الإمارات العربية المتحدة", "United Arab Emirates", "UAE");
        Add("قطر", "Qatar");
        Add("الكويت", "Kuwait");
        Add("البحرين", "Bahrain");
        Add("عُمان", "Oman");

        // Saudi Arabia: cities and regions.
        Add("الرياض", "Riyadh");
        Add("جدة", "Jeddah", "Jiddah");
        Add("الدمام", "Dammam");
        Add("الخبر", "Khobar");
        Add("الظهران", "Dhahran");
        Add("مكة المكرمة", "Makkah", "Mecca");
        Add("المدينة المنورة", "Madinah", "Medina");
        Add("الطائف", "Taif");
        Add("تبوك", "Tabuk");
        Add("أبها", "Abha");
        Add("خميس مشيط", "Khamis Mushait");
        Add("بريدة", "Buraidah", "Buraydah");
        Add("عنيزة", "Unaizah");
        Add("حائل", "Hail");
        Add("جازان", "Jazan", "Jizan");
        Add("نجران", "Najran");
        Add("الباحة", "Baha");
        Add("ينبع", "Yanbu");
        Add("الجبيل", "Jubail");
        Add("الأحساء", "Ahsa", "Hasa");
        Add("الهفوف", "Hofuf");
        Add("القطيف", "Qatif");
        Add("الخرج", "Kharj");
        Add("عرعر", "Arar");
        Add("سكاكا", "Sakaka");
        Add("حفر الباطن", "Hafar Al Batin");
        Add("رابغ", "Rabigh");
        Add("رأس تنورة", "Ras Tanura");
        Add("العلا", "Ula");
        Add("نيوم", "NEOM");
        Add("القصيم", "Qassim");
        Add("المنطقة الشرقية", "Eastern Province");

        // Riyadh districts.
        Add("العليا", "Olaya");
        Add("السليمانية", "Sulaymaniyah", "Sulaimaniyah", "Sulaimaniya");
        Add("الملز", "Malaz");
        Add("المربع", "Murabba");
        Add("الملقا", "Malqa");
        Add("حطين", "Hittin");
        Add("الياسمين", "Yasmin");
        Add("النخيل", "Nakheel");
        Add("الصحافة", "Sahafa");
        Add("الورود", "Wurud");
        Add("الروضة", "Rawdah");
        Add("النسيم", "Naseem");
        Add("العقيق", "Aqiq");
        Add("الربوة", "Rabwah");
        Add("المروج", "Muruj");
        Add("النرجس", "Narjis");
        Add("حي السفارات", "Diplomatic Quarter");
        Add("مركز الملك عبدالله المالي", "King Abdullah Financial District", "KAFD");

        // Jeddah and Eastern Province districts.
        Add("الحمراء", "Hamra");
        Add("السلامة", "Salamah");
        Add("البلد", "Balad");
        Add("الشاطئ", "Shati");
        Add("الزهراء", "Zahra");
        Add("الصفا", "Safa");
        Add("الأندلس", "Andalus");
        Add("أبحر", "Obhur");
        Add("الخالدية", "Khalidiyah");
        Add("النعيم", "Naeem");
        Add("المروة", "Marwah");
        Add("النهضة", "Nahda");
        Add("العزيزية", "Aziziyah");
        Add("الفيصلية", "Faisaliyah");
        Add("الراكة", "Rakah");
        Add("العقربية", "Aqrabiyah");
        Add("الثقبة", "Thuqbah");

        // United Arab Emirates.
        Add("دبي", "Dubai");
        Add("أبوظبي", "Abu Dhabi");
        Add("الشارقة", "Sharjah");
        Add("عجمان", "Ajman");
        Add("رأس الخيمة", "Ras Al Khaimah");
        Add("الفجيرة", "Fujairah");
        Add("أم القيوين", "Umm Al Quwain");
        Add("العين", "Ain");
        Add("جبل علي", "Jebel Ali");
        Add("ديرة", "Deira");
        Add("بر دبي", "Bur Dubai");
        Add("الخليج التجاري", "Business Bay");
        Add("مصفح", "Mussafah");
        Add("مدينة خليفة", "Khalifa City");

        // Qatar.
        Add("الدوحة", "Doha");
        Add("الوكرة", "Wakrah");
        Add("الخور", "Khor");
        Add("لوسيل", "Lusail");
        Add("الريان", "Rayyan");
        Add("مسيعيد", "Mesaieed");
        Add("رأس لفان", "Ras Laffan");
        Add("الخليج الغربي", "West Bay");
        Add("السد", "Sadd");

        // Kuwait.
        Add("مدينة الكويت", "Kuwait City");
        Add("حولي", "Hawalli");
        Add("السالمية", "Salmiya");
        Add("الفروانية", "Farwaniya");
        Add("الأحمدي", "Ahmadi");
        Add("الجهراء", "Jahra");
        Add("الفحيحيل", "Fahaheel");
        Add("مبارك الكبير", "Mubarak Al Kabeer");
        Add("الشويخ", "Shuwaikh");

        // Bahrain.
        Add("المنامة", "Manama");
        Add("المحرق", "Muharraq");
        Add("الرفاع", "Riffa");
        Add("مدينة عيسى", "Isa Town");
        Add("مدينة حمد", "Hamad Town");
        Add("سترة", "Sitra");
        Add("الجفير", "Juffair");
        Add("السيف", "Seef");

        // Oman.
        Add("مسقط", "Muscat");
        Add("صلالة", "Salalah");
        Add("صحار", "Sohar");
        Add("نزوى", "Nizwa");
        Add("صور", "Sur");
        Add("الدقم", "Duqm");
        Add("روي", "Ruwi");
        Add("مطرح", "Muttrah");
        Add("السيب", "Seeb");
        Add("بركاء", "Barka");
        Add("عبري", "Ibri");
        Add("البريمي", "Buraimi");
        return p;
    }
}
