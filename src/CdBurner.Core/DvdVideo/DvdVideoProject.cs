using System.Globalization;

namespace CdBurner.Core.DvdVideo;

/// <summary>Audio stream content role written into DVD-Video IFO <c>audio_attr_t.code_extension</c>.</summary>
public enum DvdAudioContentKind : byte
{
    Unspecified = 0,
    Normal = 1,
    VisuallyImpaired = 2,
    DirectorsComments = 3,
    AlternateDirectorsComments = 4
}

/// <summary>
/// Maps between ISO 639-2 three-letter language tags (used in project files, CLI flags, and MPEG/FFmpeg metadata)
/// and ISO 639-1 two-letter ASCII codes stored in DVD-Video IFO <c>audio_attr_t</c> and <c>subp_attr_t</c> tables.
/// </summary>
public static class DvdLanguageCodes
{
    private static readonly Dictionary<string, (string Part2, string? Part1)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aa"] = ("aar", "aa"), ["aar"] = ("aar", "aa"),
        ["ab"] = ("abk", "ab"), ["abk"] = ("abk", "ab"),
        ["af"] = ("afr", "af"), ["afr"] = ("afr", "af"),
        ["ak"] = ("aka", "ak"), ["aka"] = ("aka", "ak"),
        ["am"] = ("amh", "am"), ["amh"] = ("amh", "am"),
        ["ar"] = ("ara", "ar"), ["ara"] = ("ara", "ar"),
        ["an"] = ("arg", "an"), ["arg"] = ("arg", "an"),
        ["as"] = ("asm", "as"), ["asm"] = ("asm", "as"),
        ["av"] = ("ava", "av"), ["ava"] = ("ava", "av"),
        ["ae"] = ("ave", "ae"), ["ave"] = ("ave", "ae"),
        ["ay"] = ("aym", "ay"), ["aym"] = ("aym", "ay"),
        ["az"] = ("aze", "az"), ["aze"] = ("aze", "az"),
        ["ba"] = ("bak", "ba"), ["bak"] = ("bak", "ba"),
        ["bm"] = ("bam", "bm"), ["bam"] = ("bam", "bm"),
        ["be"] = ("bel", "be"), ["bel"] = ("bel", "be"),
        ["bn"] = ("ben", "bn"), ["ben"] = ("ben", "bn"),
        ["bi"] = ("bis", "bi"), ["bis"] = ("bis", "bi"),
        ["bo"] = ("bod", "bo"), ["bod"] = ("bod", "bo"), ["tib"] = ("bod", "bo"),
        ["bs"] = ("bos", "bs"), ["bos"] = ("bos", "bs"),
        ["br"] = ("bre", "br"), ["bre"] = ("bre", "br"),
        ["bg"] = ("bul", "bg"), ["bul"] = ("bul", "bg"),
        ["ca"] = ("cat", "ca"), ["cat"] = ("cat", "ca"),
        ["cs"] = ("ces", "cs"), ["ces"] = ("ces", "cs"), ["cze"] = ("ces", "cs"),
        ["ch"] = ("cha", "ch"), ["cha"] = ("cha", "ch"),
        ["ce"] = ("che", "ce"), ["che"] = ("che", "ce"),
        ["cu"] = ("chu", "cu"), ["chu"] = ("chu", "cu"),
        ["cv"] = ("chv", "cv"), ["chv"] = ("chv", "cv"),
        ["kw"] = ("cor", "kw"), ["cor"] = ("cor", "kw"),
        ["co"] = ("cos", "co"), ["cos"] = ("cos", "co"),
        ["cr"] = ("cre", "cr"), ["cre"] = ("cre", "cr"),
        ["cy"] = ("cym", "cy"), ["cym"] = ("cym", "cy"), ["wel"] = ("cym", "cy"),
        ["da"] = ("dan", "da"), ["dan"] = ("dan", "da"),
        ["de"] = ("deu", "de"), ["deu"] = ("deu", "de"), ["ger"] = ("deu", "de"),
        ["dv"] = ("div", "dv"), ["div"] = ("div", "dv"),
        ["dz"] = ("dzo", "dz"), ["dzo"] = ("dzo", "dz"),
        ["el"] = ("ell", "el"), ["ell"] = ("ell", "el"), ["gre"] = ("ell", "el"),
        ["en"] = ("eng", "en"), ["eng"] = ("eng", "en"),
        ["eo"] = ("epo", "eo"), ["epo"] = ("epo", "eo"),
        ["et"] = ("est", "et"), ["est"] = ("est", "et"),
        ["eu"] = ("eus", "eu"), ["eus"] = ("eus", "eu"), ["baq"] = ("eus", "eu"),
        ["ee"] = ("ewe", "ee"), ["ewe"] = ("ewe", "ee"),
        ["fo"] = ("fao", "fo"), ["fao"] = ("fao", "fo"),
        ["fa"] = ("fas", "fa"), ["fas"] = ("fas", "fa"), ["per"] = ("fas", "fa"),
        ["fj"] = ("fij", "fj"), ["fij"] = ("fij", "fj"),
        ["fi"] = ("fin", "fi"), ["fin"] = ("fin", "fi"),
        ["fr"] = ("fra", "fr"), ["fra"] = ("fra", "fr"), ["fre"] = ("fra", "fr"),
        ["fy"] = ("fry", "fy"), ["fry"] = ("fry", "fy"),
        ["ff"] = ("ful", "ff"), ["ful"] = ("ful", "ff"),
        ["gd"] = ("gla", "gd"), ["gla"] = ("gla", "gd"),
        ["ga"] = ("gle", "ga"), ["gle"] = ("gle", "ga"),
        ["gl"] = ("glg", "gl"), ["glg"] = ("glg", "gl"),
        ["gv"] = ("glv", "gv"), ["glv"] = ("glv", "gv"),
        ["gn"] = ("grn", "gn"), ["grn"] = ("grn", "gn"),
        ["gu"] = ("guj", "gu"), ["guj"] = ("guj", "gu"),
        ["ht"] = ("hat", "ht"), ["hat"] = ("hat", "ht"),
        ["ha"] = ("hau", "ha"), ["hau"] = ("hau", "ha"),
        ["he"] = ("heb", "he"), ["heb"] = ("heb", "he"),
        ["hz"] = ("her", "hz"), ["her"] = ("her", "hz"),
        ["hi"] = ("hin", "hi"), ["hin"] = ("hin", "hi"),
        ["ho"] = ("hmo", "ho"), ["hmo"] = ("hmo", "ho"),
        ["hr"] = ("hrv", "hr"), ["hrv"] = ("hrv", "hr"),
        ["hu"] = ("hun", "hu"), ["hun"] = ("hun", "hu"),
        ["hy"] = ("hye", "hy"), ["hye"] = ("hye", "hy"), ["arm"] = ("hye", "hy"),
        ["ig"] = ("ibo", "ig"), ["ibo"] = ("ibo", "ig"),
        ["io"] = ("ido", "io"), ["ido"] = ("ido", "io"),
        ["ii"] = ("iii", "ii"), ["iii"] = ("iii", "ii"),
        ["iu"] = ("iku", "iu"), ["iku"] = ("iku", "iu"),
        ["ie"] = ("ile", "ie"), ["ile"] = ("ile", "ie"),
        ["ia"] = ("ina", "ia"), ["ina"] = ("ina", "ia"),
        ["id"] = ("ind", "id"), ["ind"] = ("ind", "id"),
        ["ik"] = ("ipk", "ik"), ["ipk"] = ("ipk", "ik"),
        ["is"] = ("isl", "is"), ["isl"] = ("isl", "is"), ["ice"] = ("isl", "is"),
        ["it"] = ("ita", "it"), ["ita"] = ("ita", "it"),
        ["jv"] = ("jav", "jv"), ["jav"] = ("jav", "jv"),
        ["ja"] = ("jpn", "ja"), ["jpn"] = ("jpn", "ja"),
        ["kl"] = ("kal", "kl"), ["kal"] = ("kal", "kl"),
        ["kn"] = ("kan", "kn"), ["kan"] = ("kan", "kn"),
        ["ks"] = ("kas", "ks"), ["kas"] = ("kas", "ks"),
        ["ka"] = ("kat", "ka"), ["kat"] = ("kat", "ka"), ["geo"] = ("kat", "ka"),
        ["kr"] = ("kau", "kr"), ["kau"] = ("kau", "kr"),
        ["kk"] = ("kaz", "kk"), ["kaz"] = ("kaz", "kk"),
        ["km"] = ("khm", "km"), ["khm"] = ("khm", "km"),
        ["ki"] = ("kik", "ki"), ["kik"] = ("kik", "ki"),
        ["rw"] = ("kin", "rw"), ["kin"] = ("kin", "rw"),
        ["ky"] = ("kir", "ky"), ["kir"] = ("kir", "ky"),
        ["kv"] = ("kom", "kv"), ["kom"] = ("kom", "kv"),
        ["kg"] = ("kon", "kg"), ["kon"] = ("kon", "kg"),
        ["ko"] = ("kor", "ko"), ["kor"] = ("kor", "ko"),
        ["kj"] = ("kua", "kj"), ["kua"] = ("kua", "kj"),
        ["ku"] = ("kur", "ku"), ["kur"] = ("kur", "ku"),
        ["lo"] = ("lao", "lo"), ["lao"] = ("lao", "lo"),
        ["la"] = ("lat", "la"), ["lat"] = ("lat", "la"),
        ["lv"] = ("lav", "lv"), ["lav"] = ("lav", "lv"),
        ["li"] = ("lim", "li"), ["lim"] = ("lim", "li"),
        ["ln"] = ("lin", "ln"), ["lin"] = ("lin", "ln"),
        ["lt"] = ("lit", "lt"), ["lit"] = ("lit", "lt"),
        ["lb"] = ("ltz", "lb"), ["ltz"] = ("ltz", "lb"),
        ["lu"] = ("lub", "lu"), ["lub"] = ("lub", "lu"),
        ["lg"] = ("lug", "lg"), ["lug"] = ("lug", "lg"),
        ["mh"] = ("mah", "mh"), ["mah"] = ("mah", "mh"),
        ["ml"] = ("mal", "ml"), ["mal"] = ("mal", "ml"),
        ["mr"] = ("mar", "mr"), ["mar"] = ("mar", "mr"),
        ["mk"] = ("mkd", "mk"), ["mkd"] = ("mkd", "mk"), ["mac"] = ("mkd", "mk"),
        ["mg"] = ("mlg", "mg"), ["mlg"] = ("mlg", "mg"),
        ["mt"] = ("mlt", "mt"), ["mlt"] = ("mlt", "mt"),
        ["mn"] = ("mon", "mn"), ["mon"] = ("mon", "mn"),
        ["mi"] = ("mri", "mi"), ["mri"] = ("mri", "mi"), ["mao"] = ("mri", "mi"),
        ["ms"] = ("msa", "ms"), ["msa"] = ("msa", "ms"), ["may"] = ("msa", "ms"),
        ["my"] = ("mya", "my"), ["mya"] = ("mya", "my"), ["bur"] = ("mya", "my"),
        ["na"] = ("nau", "na"), ["nau"] = ("nau", "na"),
        ["nv"] = ("nav", "nv"), ["nav"] = ("nav", "nv"),
        ["nr"] = ("nbl", "nr"), ["nbl"] = ("nbl", "nr"),
        ["nd"] = ("nde", "nd"), ["nde"] = ("nde", "nd"),
        ["ng"] = ("ndo", "ng"), ["ndo"] = ("ndo", "ng"),
        ["ne"] = ("nep", "ne"), ["nep"] = ("nep", "ne"),
        ["nl"] = ("nld", "nl"), ["nld"] = ("nld", "nl"), ["dut"] = ("nld", "nl"),
        ["nn"] = ("nno", "nn"), ["nno"] = ("nno", "nn"),
        ["nb"] = ("nob", "nb"), ["nob"] = ("nob", "nb"),
        ["no"] = ("nor", "no"), ["nor"] = ("nor", "no"),
        ["ny"] = ("nya", "ny"), ["nya"] = ("nya", "ny"),
        ["oc"] = ("oci", "oc"), ["oci"] = ("oci", "oc"),
        ["oj"] = ("oji", "oj"), ["oji"] = ("oji", "oj"),
        ["or"] = ("ori", "or"), ["ori"] = ("ori", "or"),
        ["om"] = ("orm", "om"), ["orm"] = ("orm", "om"),
        ["os"] = ("oss", "os"), ["oss"] = ("oss", "os"),
        ["pa"] = ("pan", "pa"), ["pan"] = ("pan", "pa"),
        ["pi"] = ("pli", "pi"), ["pli"] = ("pli", "pi"),
        ["pl"] = ("pol", "pl"), ["pol"] = ("pol", "pl"),
        ["pt"] = ("por", "pt"), ["por"] = ("por", "pt"),
        ["ps"] = ("pus", "ps"), ["pus"] = ("pus", "ps"),
        ["qu"] = ("que", "qu"), ["que"] = ("que", "qu"),
        ["rm"] = ("roh", "rm"), ["roh"] = ("roh", "rm"),
        ["ro"] = ("ron", "ro"), ["ron"] = ("ron", "ro"), ["rum"] = ("ron", "ro"),
        ["rn"] = ("run", "rn"), ["run"] = ("run", "rn"),
        ["ru"] = ("rus", "ru"), ["rus"] = ("rus", "ru"),
        ["sg"] = ("sag", "sg"), ["sag"] = ("sag", "sg"),
        ["sa"] = ("san", "sa"), ["san"] = ("san", "sa"),
        ["si"] = ("sin", "si"), ["sin"] = ("sin", "si"),
        ["sk"] = ("slk", "sk"), ["slk"] = ("slk", "sk"), ["slo"] = ("slk", "sk"),
        ["sl"] = ("slv", "sl"), ["slv"] = ("slv", "sl"),
        ["se"] = ("sme", "se"), ["sme"] = ("sme", "se"),
        ["sm"] = ("smo", "sm"), ["smo"] = ("smo", "sm"),
        ["sn"] = ("sna", "sn"), ["sna"] = ("sna", "sn"),
        ["sd"] = ("snd", "sd"), ["snd"] = ("snd", "sd"),
        ["so"] = ("som", "so"), ["som"] = ("som", "so"),
        ["st"] = ("sot", "st"), ["sot"] = ("sot", "st"),
        ["es"] = ("spa", "es"), ["spa"] = ("spa", "es"),
        ["sq"] = ("sqi", "sq"), ["sqi"] = ("sqi", "sq"), ["alb"] = ("sqi", "sq"),
        ["sc"] = ("srd", "sc"), ["srd"] = ("srd", "sc"),
        ["sr"] = ("srp", "sr"), ["srp"] = ("srp", "sr"),
        ["ss"] = ("ssw", "ss"), ["ssw"] = ("ssw", "ss"),
        ["su"] = ("sun", "su"), ["sun"] = ("sun", "su"),
        ["sw"] = ("swa", "sw"), ["swa"] = ("swa", "sw"),
        ["sv"] = ("swe", "sv"), ["swe"] = ("swe", "sv"),
        ["ty"] = ("tah", "ty"), ["tah"] = ("tah", "ty"),
        ["ta"] = ("tam", "ta"), ["tam"] = ("tam", "ta"),
        ["tt"] = ("tat", "tt"), ["tat"] = ("tat", "tt"),
        ["te"] = ("tel", "te"), ["tel"] = ("tel", "te"),
        ["tg"] = ("tgk", "tg"), ["tgk"] = ("tgk", "tg"),
        ["tl"] = ("tgl", "tl"), ["tgl"] = ("tgl", "tl"),
        ["th"] = ("tha", "th"), ["tha"] = ("tha", "th"),
        ["ti"] = ("tir", "ti"), ["tir"] = ("tir", "ti"),
        ["to"] = ("ton", "to"), ["ton"] = ("ton", "to"),
        ["tn"] = ("tsn", "tn"), ["tsn"] = ("tsn", "tn"),
        ["ts"] = ("tso", "ts"), ["tso"] = ("tso", "ts"),
        ["tk"] = ("tuk", "tk"), ["tuk"] = ("tuk", "tk"),
        ["tr"] = ("tur", "tr"), ["tur"] = ("tur", "tr"),
        ["tw"] = ("twi", "tw"), ["twi"] = ("twi", "tw"),
        ["ug"] = ("uig", "ug"), ["uig"] = ("uig", "ug"),
        ["uk"] = ("ukr", "uk"), ["ukr"] = ("ukr", "uk"),
        ["ur"] = ("urd", "ur"), ["urd"] = ("urd", "ur"),
        ["uz"] = ("uzb", "uz"), ["uzb"] = ("uzb", "uz"),
        ["ve"] = ("ven", "ve"), ["ven"] = ("ven", "ve"),
        ["vi"] = ("vie", "vi"), ["vie"] = ("vie", "vi"),
        ["vo"] = ("vol", "vo"), ["vol"] = ("vol", "vo"),
        ["wa"] = ("wln", "wa"), ["wln"] = ("wln", "wa"),
        ["wo"] = ("wol", "wo"), ["wol"] = ("wol", "wo"),
        ["xh"] = ("xho", "xh"), ["xho"] = ("xho", "xh"),
        ["yi"] = ("yid", "yi"), ["yid"] = ("yid", "yi"),
        ["yo"] = ("yor", "yo"), ["yor"] = ("yor", "yo"),
        ["za"] = ("zha", "za"), ["zha"] = ("zha", "za"),
        ["zh"] = ("zho", "zh"), ["zho"] = ("zho", "zh"), ["chi"] = ("zho", "zh"),
        ["zu"] = ("zul", "zu"), ["zul"] = ("zul", "zu"),
        ["und"] = ("und", null),
        ["auto"] = ("auto", null)
    };

    /// <summary>True when <paramref name="code"/> is a valid 2- or 3-letter ASCII language tag (or "auto" when allowed).</summary>
    public static bool IsValidLanguageTag(string? code, bool allowAuto = false)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }
        string trimmed = code.Trim().ToLowerInvariant();
        if (trimmed == "auto")
        {
            return allowAuto;
        }
        return trimmed.Length is 2 or 3 && trimmed.All(c => c is >= 'a' and <= 'z');
    }

    /// <summary>Normalizes a 2- or 3-letter language code to canonical lowercase ISO 639-2 (e.g. "en" -> "eng", "fre" -> "fra").</summary>
    public static string NormalizeIso639Part2(string? code, string fallback = "eng")
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return fallback;
        }
        string trimmed = code.Trim().ToLowerInvariant();
        if (Map.TryGetValue(trimmed, out var entry))
        {
            return entry.Part2;
        }
        if (trimmed.Length == 3 && trimmed.All(c => c is >= 'a' and <= 'z'))
        {
            return trimmed;
        }
        if (trimmed.Length == 2 && trimmed.All(c => c is >= 'a' and <= 'z'))
        {
            try
            {
                string three = CultureInfo.GetCultureInfo(trimmed).ThreeLetterISOLanguageName.ToLowerInvariant();
                if (three.Length == 3 && three.All(c => c is >= 'a' and <= 'z'))
                {
                    return three;
                }
            }
            catch (CultureNotFoundException)
            {
            }
        }
        return fallback;
    }

    /// <summary>Converts an ISO 639-2 or 639-1 tag to the 2-letter ISO 639-1 ASCII code required by DVD IFO headers, or null for undetermined.</summary>
    public static string? ToIso639Part1(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }
        string trimmed = code.Trim().ToLowerInvariant();
        if (Map.TryGetValue(trimmed, out var entry))
        {
            return entry.Part1;
        }
        if (trimmed.Length == 2 && trimmed.All(c => c is >= 'a' and <= 'z'))
        {
            return trimmed;
        }
        return null;
    }

    /// <summary>
    /// Parses a <c>&lt;lang&gt;:&lt;path&gt;</c> CLI track specification (preserving Windows drive colons such as <c>eng:C:\a.ac3</c>).
    /// </summary>
    public static bool TryParseTrackSpec(string spec, out string language, out string path)
    {
        language = "eng";
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        string trimmed = spec.Trim();
        int colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        // A colon at index 1 (e.g. "C:\track.ac3") is a drive letter, not "<lang>:<path>".
        if (colon == 1 && char.IsLetter(trimmed[0]))
        {
            return false;
        }

        string langPart = trimmed[..colon].Trim();
        string pathPart = trimmed[(colon + 1)..].Trim();
        if (!IsValidLanguageTag(langPart) || string.IsNullOrWhiteSpace(pathPart))
        {
            return false;
        }

        language = NormalizeIso639Part2(langPart);
        path = pathPart;
        return true;
    }
}

/// <summary>Per-title audio stream specification for DVD-Video authoring (up to 8 streams per title, 0x80..0x87).</summary>
public sealed record DvdAudioTrackSpec(
    string? SourcePath = null,
    string Language = "eng",
    int Channels = 2,
    int BitrateKbps = 0,
    DvdAudioContentKind ContentKind = DvdAudioContentKind.Normal,
    int StreamIndex = 0,
    string? Label = null)
{
    /// <summary>Effective AC-3 target bitrate in kbps when encoding this track.</summary>
    public int EffectiveBitrateKbps(int defaultStereoKbps = 192) =>
        BitrateKbps > 0 ? BitrateKbps : (Channels >= 6 ? 448 : defaultStereoKbps);

    /// <summary>Normalized ISO 639-2 three-letter language code.</summary>
    public string NormalizedLanguage => DvdLanguageCodes.NormalizeIso639Part2(Language);

    /// <summary>Converts this track specification to its IFO attribute entry.</summary>
    public DvdAudioStreamInfo ToIfoInfo() => new(NormalizedLanguage, Channels, ContentKind);
}

/// <summary>Per-title subtitle stream specification for DVD-Video authoring (up to 8 streams per title, 0x20..0x27).</summary>
public sealed record DvdSubtitleTrackSpec(
    string? SourcePath = null,
    string Language = "eng",
    bool GenerateWithWhisper = false,
    byte CodeExtension = 0x01,
    string? Label = null)
{
    /// <summary>Normalized ISO 639-2 three-letter language code (or "auto" when speech recognition guesses).</summary>
    public string NormalizedLanguage => string.Equals(Language?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
        ? "auto"
        : DvdLanguageCodes.NormalizeIso639Part2(Language);

    /// <summary>Converts this track specification to its IFO attribute entry.</summary>
    public DvdSubtitleStreamInfo ToIfoInfo() => new(
        NormalizedLanguage == "auto" ? "und" : NormalizedLanguage,
        CodeExtension);
}

/// <summary>One title in a <see cref="DvdVideoProject"/> with up to 8 audio and up to 8 subtitle streams.</summary>
public sealed class DvdVideoTitle
{
    public DvdVideoTitle(string moviePath, string? titleName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moviePath);
        MoviePath = moviePath;
        TitleName = titleName;
    }

    /// <summary>Source movie file path.</summary>
    public string MoviePath { get; }

    /// <summary>Optional menu display title.</summary>
    public string? TitleName { get; set; }

    /// <summary>Audio streams for this title (max <see cref="DvdVideoProject.MaxAudioTracksPerTitle"/> = 8).</summary>
    public IList<DvdAudioTrackSpec> AudioTracks { get; } = new List<DvdAudioTrackSpec>();

    /// <summary>Subtitle streams for this title (max <see cref="DvdVideoProject.MaxSubtitleTracksPerTitle"/> = 8).</summary>
    public IList<DvdSubtitleTrackSpec> SubtitleTracks { get; } = new List<DvdSubtitleTrackSpec>();

    public DvdVideoTitle AddAudioTrack(DvdAudioTrackSpec track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (AudioTracks.Count >= DvdVideoProject.MaxAudioTracksPerTitle)
        {
            throw new InvalidOperationException(
                $"DVD-Video allows at most {DvdVideoProject.MaxAudioTracksPerTitle} audio streams per title.");
        }
        AudioTracks.Add(track);
        return this;
    }

    public DvdVideoTitle AddSubtitleTrack(DvdSubtitleTrackSpec track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (SubtitleTracks.Count >= DvdVideoProject.MaxSubtitleTracksPerTitle)
        {
            throw new InvalidOperationException(
                $"DVD-Video allows at most {DvdVideoProject.MaxSubtitleTracksPerTitle} subtitle streams per title.");
        }
        SubtitleTracks.Add(track);
        return this;
    }
}

/// <summary>
/// Declarative multi-title DVD-Video authoring project supporting up to 8 audio streams and up to 8 subtitle
/// streams per title with ISO 639-2 language metadata.
/// </summary>
public sealed class DvdVideoProject
{
    public const int MaxAudioTracksPerTitle = 8;
    public const int MaxSubtitleTracksPerTitle = 8;

    public DvdVideoProject(DvdAuthoringOptions? options = null)
    {
        Options = options ?? new DvdAuthoringOptions();
    }

    /// <summary>Global disc authoring options.</summary>
    public DvdAuthoringOptions Options { get; }

    /// <summary>Ordered titles on the disc (up to 99).</summary>
    public IList<DvdVideoTitle> Titles { get; } = new List<DvdVideoTitle>();

    public DvdVideoTitle AddTitle(string moviePath, string? titleName = null)
    {
        var title = new DvdVideoTitle(moviePath, titleName);
        Titles.Add(title);
        return title;
    }

    /// <summary>Materializes per-title audio/subtitle tracks and title names into <see cref="Options"/>.</summary>
    public DvdAuthoringOptions ToAuthoringOptions()
    {
        foreach (var title in Titles)
        {
            if (!string.IsNullOrWhiteSpace(title.TitleName))
            {
                Options.TitleNames[title.MoviePath] = title.TitleName!;
            }
            if (title.AudioTracks.Count > 0)
            {
                Options.TitleAudioTracks[title.MoviePath] = title.AudioTracks.Take(MaxAudioTracksPerTitle).ToList();
            }
            if (title.SubtitleTracks.Count > 0)
            {
                Options.TitleSubtitleTracks[title.MoviePath] = title.SubtitleTracks.Take(MaxSubtitleTracksPerTitle).ToList();
            }
        }
        return Options;
    }
}
