using System.Globalization;
using System.Text;

namespace LunaDrome.Core.Services;

/// <summary>
/// Typo-tolerant matching for the search bar. Navidrome's search3 only finds exact substrings,
/// so "iron maden" or "metalica" find nothing; this picks the library names (artists, albums,
/// song titles) closest to such a query, which are then searched for instead.
/// </summary>
public static class FuzzySearch
{
    /// <summary>The names closest to the query, best first. Every query word has to match a word
    /// of the name within a few typos (more allowed for longer words); an exact or prefix word
    /// match counts as perfect, so "maiden" still finds "Iron Maiden".</summary>
    public static List<string> BestMatches(string query, IEnumerable<string> names, int max = 3)
    {
        var queryWords = Words(query);
        if (queryWords.Count == 0)
            return [];

        var scored = new List<(string Name, double Score)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var key = Normalize(name);
            if (!seen.Add(key))
                continue;
            var score = Score(queryWords, Words(name));
            if (score > 0)
                scored.Add((name, score));
        }

        // Only names about as close as the best one: "slipnot" is Slipknot, not also Slint.
        var best = scored.Count > 0 ? scored.Max(s => s.Score) : 0;
        return scored
            .Where(s => s.Score >= best - 0.1)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Name.Length)
            .Take(max)
            .Select(s => s.Name)
            .ToList();
    }

    // 0 = no match; otherwise up to 1, higher is closer. Each query word takes its best word of
    // the name; the name's words nobody used lower the score a little, so "Iron Maiden" beats
    // "Iron Maiden Tribute Orchestra" for "iron maden".
    private static double Score(List<string> queryWords, List<string> nameWords)
    {
        if (nameWords.Count == 0)
            return 0;

        double total = 0;
        var used = new bool[nameWords.Count];
        foreach (var q in queryWords)
        {
            double best = 0;
            var bestIndex = -1;
            for (var i = 0; i < nameWords.Count; i++)
            {
                var s = WordSimilarity(q, nameWords[i]);
                if (s > best || (s == best && bestIndex >= 0 && used[bestIndex] && !used[i]))
                {
                    best = s;
                    bestIndex = i;
                }
            }
            if (best == 0)
                return 0;
            total += best;
            used[bestIndex] = true;
        }

        var unused = used.Count(u => !u);
        return total / queryWords.Count - 0.05 * unused;
    }

    private static double WordSimilarity(string q, string w)
    {
        if (q == w)
            return 1;
        // A word typed half-way ("maid" for "maiden"), but not single letters matching everything.
        if (q.Length >= 3 && w.StartsWith(q, StringComparison.Ordinal))
            return 0.95;

        var allowed = q.Length <= 3 ? 0 : q.Length <= 5 ? 1 : q.Length <= 9 ? 2 : 3;
        if (allowed == 0 || Math.Abs(q.Length - w.Length) > allowed)
            return 0;
        var distance = Distance(q, w, allowed);
        return distance > allowed ? 0 : 0.9 - 0.15 * distance;
    }

    /// <summary>Damerau-Levenshtein distance (a swapped pair of letters is one typo), giving up
    /// early once it is past <paramref name="limit"/>.</summary>
    public static int Distance(string a, string b, int limit = int.MaxValue)
    {
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var d = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d = Math.Min(d, prev2[j - 2] + 1);
                cur[j] = d;
                rowMin = Math.Min(rowMin, d);
            }
            if (rowMin > limit)
                return limit + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }

    // Lowercase, accents dropped ("Beyoncé" = "beyonce"), apostrophes removed ("don't" = "dont"),
    // anything else that isn't a letter or digit splits words.
    private static List<string> Words(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’')
                continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString();
    }
}
