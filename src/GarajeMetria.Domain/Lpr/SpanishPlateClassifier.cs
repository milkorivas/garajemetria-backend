using System.Text.RegularExpressions;

namespace GarajeMetria.Domain.Lpr;

public sealed class SpanishPlateClassification
{
    internal SpanishPlateClassification(string original, string normalized, string? format)
    {
        Original = original;
        Normalized = normalized;
        Format = format;
    }

    public string Original { get; }
    public string Normalized { get; }
    public bool IsValid => Format is not null;
    public string? Format { get; }
    public string RulesVersion => SpanishPlateClassifier.RulesVersion;
    public override string ToString() => $"Plate classification: {(IsValid ? "valid" : "invalid")}, {RulesVersion}";
}

public static class SpanishPlateClassifier
{
    public const string RulesVersion = "es-v1";
    private const string National = "[0-9]{4}[BCDFGHJKLMNPRSTVWXYZ]{3}";

    public static IReadOnlyList<string> ProvincialPrefixes { get; } = Array.AsReadOnly(
        "A AB AL AV B BA BI BU C CA CC CE CO CR CS CU GC GI GR GU H HU IB J L LE LO LU M MA ML MU NA O OU P PO S SA SE SG SO SS T TE TF TO V VA VI Z ZA PM GE OR".Split(' '));
    public static IReadOnlyList<string> HistoricalNumericPrefixes { get; } = Array.AsReadOnly(
        "ALB CAC CAS SEG TER".Split(' '));
    public static IReadOnlyList<string> StatePrefixes { get; } = Array.AsReadOnly(
        "ET FN EA MF MMA PME CNP PGC FAE".Split(' '));

    private static readonly (string Format, Regex Pattern)[] Rules =
    [
        Rule("spanish_national", National),
        Rule("spanish_special", "E" + National),
        Rule("spanish_trailer", "R" + National),
        Rule("spanish_moped", "C" + National),
        Rule("spanish_historic", "H" + National),
        Rule("spanish_tourist", "T" + National),
        Rule("spanish_temporary", "[PSV]" + National),
        Rule("spanish_diplomatic", "(?:CD|CC|OI|TA)[0-9]{2,6}"),
        Rule("spanish_state", "(?:" + string.Join('|', StatePrefixes) + ")(?:VE)?[0-9]{1,8}[A-Z]{0,3}"),
        Rule("spanish_regional", "(?:E[0-9]{1,8}|CGPC[0-9]{1,8}[A-Z]{0,3})"),
        Rule("spanish_provincial_numeric", "(?:" + string.Join('|', ProvincialPrefixes.Concat(HistoricalNumericPrefixes)) + ")[0-9]{1,6}"),
        Rule("spanish_provincial_alphanumeric", "(?:" + string.Join('|', ProvincialPrefixes) + ")[0-9]{4}[A-Z]{1,2}")
    ];

    public static SpanishPlateClassification Classify(string original)
    {
        LprText.Require(original, 32);
        var normalized = string.Concat(original.Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();
        if (normalized.Length == 0) throw new ArgumentException("A plate value is required.", nameof(original));
        string? format = null;
        foreach (var rule in Rules)
        {
            if (!rule.Pattern.IsMatch(normalized)) continue;
            if (format is not null) return new(original, normalized, "spanish_ambiguous");
            format = rule.Format;
        }
        return new(original, normalized, format);
    }

    private static (string, Regex) Rule(string format, string pattern) =>
        (format, new Regex("\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100)));
}
