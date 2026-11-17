using System.Text.RegularExpressions;

namespace Tasky.Helpers;

/// <summary>A web address found in a description: the full address and a short one-line label.</summary>
public sealed record DescriptionLink(string Url, string Label);

/// <summary>
/// Finds web addresses in a task description, so the card can show them as clickable link chips
/// (one line each) instead of long addresses wrapping over several lines.
/// </summary>
public static partial class DescriptionLinks
{
    // http(s)://… or www.…, up to the next whitespace. Trailing punctuation is trimmed below.
    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"'];

    /// <summary>Every distinct address in the text, in order.</summary>
    public static IReadOnlyList<DescriptionLink> Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var links = new List<DescriptionLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in UrlRegex().Matches(text))
        {
            var raw = TrimTrailing(match.Value);
            var url = raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + raw : raw;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) continue;
            if (seen.Add(url)) links.Add(new DescriptionLink(url, Label(uri)));
        }
        return links;
    }

    /// <summary>The text with the addresses taken out (they are shown as chips instead).</summary>
    public static string WithoutLinks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var stripped = UrlRegex().Replace(text, m =>
        {
            var trimmed = TrimTrailing(m.Value);
            return m.Value[trimmed.Length..];   // keep punctuation after the address, e.g. "see (link)."
        });
        // Tidy what the addresses leave behind: empty brackets, spaces before punctuation, blank lines.
        stripped = Regex.Replace(stripped, @"\(\s*\)|\[\s*\]", "");
        stripped = Regex.Replace(stripped, @"[ \t]+([.,;:!?])", "$1");
        stripped = Regex.Replace(stripped, @"[ \t]{2,}", " ");
        var lines = stripped.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && l.Any(char.IsLetterOrDigit));
        return string.Join("\n", lines);
    }

    private static string TrimTrailing(string value)
    {
        var trimmed = value.TrimEnd(TrailingPunctuation);
        // Keep a closing bracket that belongs to the address, e.g. Wikipedia's ..._(film)
        if (trimmed.Length < value.Length && value[trimmed.Length] == ')'
            && trimmed.Count(c => c == '(') > trimmed.Count(c => c == ')'))
            trimmed += ")";
        return trimmed;
    }

    /// <summary>"github.com/owner/repo/issues/12" style: no scheme, no "www.", no trailing slash.</summary>
    private static string Label(Uri uri)
    {
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        return host + path;
    }
}
