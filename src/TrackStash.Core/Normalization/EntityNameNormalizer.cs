using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TrackStash.Core.Normalization;

public enum DuplicateResolutionAction
{
    ReuseByExternalReference = 0,
    ReuseByStrictNormalization = 1,
    ReviewRequired = 2,
    CreateNewCanonicalEntity = 3,
}

public static partial class EntityNameNormalizer
{
    private static readonly HashSet<string> LooseFillerTokens = new(StringComparer.Ordinal)
    {
        "records",
        "recordings",
        "music",
        "ltd",
        "limited",
        "catalogue",
    };

    [GeneratedRegex("\\s+")]
    private static partial Regex MultiWhitespaceRegex();

    public static string NormalizeStrict(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var canonical = FoldToCanonicalAscii(value);
        var builder = new StringBuilder(canonical.Length);

        foreach (var ch in canonical)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch))
            {
                builder.Append(' ');
            }
            else if (IsStylisticSeparator(ch))
            {
                // Drop stylistic separators so punctuation variants normalize together.
            }
            else
            {
                builder.Append(' ');
            }
        }

        var collapsed = MultiWhitespaceRegex().Replace(builder.ToString(), " ").Trim();
        return collapsed.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public static string NormalizeLoose(string? value)
    {
        var strict = NormalizeStrict(value);
        if (string.IsNullOrEmpty(strict))
            return string.Empty;

        // Build a loose key from the canonicalized token stream when tokens are available.
        var tokenSource = TokenizeForLoose(value);
        if (tokenSource.Count == 0)
            return strict;

        var filtered = tokenSource
            .Where(token => !LooseFillerTokens.Contains(token))
            .ToArray();

        if (filtered.Length == 0)
            return strict;

        return string.Concat(filtered);
    }

    public static IReadOnlyList<string> SplitCompoundValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var parts = value
            .Split(['\n', '/', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return parts;
    }

    public static DuplicateResolutionAction DecideDuplicateResolutionAction(
        bool hasExternalReferenceMatch,
        int strictMatchCount,
        int looseMatchCount)
    {
        if (hasExternalReferenceMatch)
            return DuplicateResolutionAction.ReuseByExternalReference;

        if (strictMatchCount == 1)
            return DuplicateResolutionAction.ReuseByStrictNormalization;

        if (strictMatchCount > 1 || looseMatchCount > 0)
            return DuplicateResolutionAction.ReviewRequired;

        return DuplicateResolutionAction.CreateNewCanonicalEntity;
    }

    private static List<string> TokenizeForLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var canonical = FoldToCanonicalAscii(value).ToLowerInvariant();
        var builder = new StringBuilder(canonical.Length);

        foreach (var ch in canonical)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
            else
                builder.Append(' ');
        }

        return MultiWhitespaceRegex()
            .Replace(builder.ToString(), " ")
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static bool IsStylisticSeparator(char ch)
    {
        return ch is '\'' or '’' or '‘' or '"' or '“' or '”' or '.' or ':' or '•' or '-' or '_';
    }

    private static string FoldToCanonicalAscii(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC)
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('“', '"')
            .Replace('”', '"');

        var decomposed = normalized.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
