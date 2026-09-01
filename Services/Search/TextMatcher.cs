using System.Text.RegularExpressions;

namespace ScoreCap.Services.Search;

public enum MatchMode { Plain, Regex, Wildcard }

public enum SearchTarget { Title, Uploader }

/// <summary>Matches a title/uploader string against a plain-substring, regex, or wildcard (*, ?) pattern.</summary>
public static class TextMatcher
{
    public static bool IsMatch(string? input, string pattern, MatchMode mode)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        input ??= string.Empty;

        try
        {
            return mode switch
            {
                MatchMode.Plain => input.Contains(pattern, StringComparison.OrdinalIgnoreCase),
                MatchMode.Regex => Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase),
                MatchMode.Wildcard => Regex.IsMatch(input, WildcardToRegexPattern(pattern), RegexOptions.IgnoreCase),
                _ => true,
            };
        }
        catch (ArgumentException)
        {
            // Pattern is an incomplete/invalid regex (e.g. still being typed) — treat as "no match" rather than crash.
            return false;
        }
    }

    private static string WildcardToRegexPattern(string wildcard)
    {
        var escaped = Regex.Escape(wildcard).Replace(@"\*", ".*").Replace(@"\?", ".");
        return "^" + escaped + "$";
    }

    /// <summary>Strips regex/wildcard metacharacters so the pattern can double as a plain-keyword YouTube search seed.</summary>
    public static string ExtractPlainSeed(string pattern)
    {
        var cleaned = Regex.Replace(pattern, @"[\^\$\.\*\+\?\(\)\[\]\{\}\|\\]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? pattern : cleaned;
    }
}
