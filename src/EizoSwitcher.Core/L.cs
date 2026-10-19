using System.Globalization;
using System.Text.Json;

namespace EizoSwitcher;

/// <summary>
/// Localization. German and English live in the code (<c>L.T("deutsch", "english")</c>);
/// other languages are embedded JSON files (Lang/xx.json) keyed by the English text.
/// Missing translations fall back to English. Placeholders use string.Format syntax ({0}).
/// </summary>
public static class L
{
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("de", "Deutsch"), ("en", "English"), ("es", "Español"), ("fr", "Français"),
        ("it", "Italiano"), ("nl", "Nederlands"), ("pl", "Polski"), ("pt", "Português"),
    ];

    private static string current_ = Detect();
    private static Dictionary<string, string>? table_ = Load(current_);

    /// <summary>Active language code, e.g. "de".</summary>
    public static string Current => current_;

    /// <summary>Selects a language; null/empty = system language (override: EIZOSWITCHER_LANG).</summary>
    public static void Set(string? code)
    {
        current_ = !string.IsNullOrEmpty(code) && Languages.Any(l => l.Code == code) ? code : Detect();
        table_ = Load(current_);
    }

    public static string T(string de, string en) => current_ switch
    {
        "de" => de,
        "en" => en,
        _ => table_ != null && table_.TryGetValue(en, out var t) && t.Length > 0 ? t : en,
    };

    public static string T(string de, string en, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(de, en), args);

    private static string Detect()
    {
        string lang = Environment.GetEnvironmentVariable("EIZOSWITCHER_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        lang = lang.Length >= 2 ? lang[..2].ToLowerInvariant() : lang;
        return Languages.Any(l => l.Code == lang) ? lang : "en";
    }

    private static Dictionary<string, string>? Load(string code)
    {
        if (code is "de" or "en") return null;
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"Lang.{code}.json");
        if (stream == null) return null;
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var table = new Dictionary<string, string>();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) table[p.Name] = p.Value.GetString()!;
        return table;
    }
}
