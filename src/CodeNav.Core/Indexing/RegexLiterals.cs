namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: extracting the literal substrings that EVERY match of a regex must contain AS WHOLE FTS
/// TOKENS, so the regex search can pre-narrow candidate files via FTS instead of scanning the whole
/// workspace.
/// Does NOT own: running the regex, reading content, or ranking (that is IndexQueries.SearchRegex).
/// Correctness contract (critical): FTS matches WHOLE TOKENS, not substrings — so a literal is only
/// safe to AND into an FTS query if it is guaranteed to appear as a whole token in every match. A run
/// therefore qualifies only when it is REQUIRED (not inside a top-level alternation or an optional
/// construct) AND bounded on both sides by a REAL SUBJECT-SIDE separator (\s, \b, \W, an escaped-
/// punctuation literal, or a literal whitespace/punctuation char). Pattern edges and ^/$ do NOT count:
/// the match is unanchored (substring), so `foo` matches inside `foobar` and `public\s+static` inside
/// `xmypublic static`. So bare `foo`, `public\s+static`, `Get\w+Client`, `foo.*bar`, `(a|b)c` all yield
/// nothing and fall back to a full scan; only fully-separated literals like `\bpublic\b` narrow.
/// Over-narrowing (a false negative) is the one thing this must never do.
/// </summary>
public static class RegexLiterals
{
    /// <summary>Lowercase whole-token literals (length >= 3) that every match must contain. Empty =>
    /// no safely-extractable anchor; the caller must scan.</summary>
    public static List<string> ExtractRequired(string pattern)
    {
        var tokens = new List<string>();
        var run = new System.Text.StringBuilder();
        bool runPrecededByBoundary = false; // pattern start is NOT a subject boundary — the match is unanchored
        bool topLevelAlt = false;
        int depth = 0;
        int i = 0, n = pattern.Length;

        // Consume an optional quantifier at position p; MinZero = the quantified element may be absent.
        (bool MinZero, int Next) Quant(int p)
        {
            if (p >= n) return (false, p);
            switch (pattern[p])
            {
                case '?':
                case '*': return (true, p + 1);
                case '+': return (false, p + 1);
                case '{':
                {
                    int j = p + 1;
                    var num = new System.Text.StringBuilder();
                    while (j < n && char.IsDigit(pattern[j])) num.Append(pattern[j++]);
                    bool mz = num.Length > 0 && long.TryParse(num.ToString(), out long m) && m == 0;
                    while (j < n && pattern[j] != '}') j++;
                    return (mz, j < n ? j + 1 : j);
                }
                default: return (false, p);
            }
        }

        void EndRun(bool followedByBoundary)
        {
            if (run.Length >= 3 && runPrecededByBoundary && followedByBoundary)
                tokens.Add(run.ToString().ToLowerInvariant());
            run.Clear();
        }

        while (i < n)
        {
            char ch = pattern[i];

            if (depth > 0)
            {
                // Inside (...): skip the interior (alternation/optionality make its literals unreliable),
                // tracking nesting, escapes and classes until we return to depth 0.
                if (ch == '\\') { i += 2; continue; }
                if (ch == '(') { depth++; i++; continue; }
                if (ch == ')')
                {
                    depth--;
                    if (depth == 0) { var (_, nxt) = Quant(i + 1); runPrecededByBoundary = false; i = nxt; }
                    else i++;
                    continue;
                }
                if (ch == '[') { int j = i + 1; while (j < n && pattern[j] != ']') { if (pattern[j] == '\\') j++; j++; } i = j < n ? j + 1 : j; continue; }
                i++;
                continue;
            }

            // depth == 0
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                var (_, nxt) = Quant(i + 1);
                if (nxt != i + 1) { run.Clear(); runPrecededByBoundary = false; i = nxt; continue; } // quantified char -> not a whole token
                run.Append(ch);
                i++;
                continue;
            }

            switch (ch)
            {
                case '(': EndRun(false); depth++; runPrecededByBoundary = false; i++; continue;
                case '[':
                {
                    EndRun(false);
                    int j = i + 1;
                    while (j < n && pattern[j] != ']') { if (pattern[j] == '\\') j++; j++; }
                    var (_, nxt) = Quant(j < n ? j + 1 : j);
                    runPrecededByBoundary = false;
                    i = nxt;
                    continue;
                }
                case '|': EndRun(false); topLevelAlt = true; runPrecededByBoundary = false; i++; continue;
                case '.':
                {
                    EndRun(false);
                    var (_, nxt) = Quant(i + 1);
                    runPrecededByBoundary = false;
                    i = nxt;
                    continue;
                }
                case '^':
                case '$': EndRun(false); runPrecededByBoundary = false; i++; continue; // anchors line position, not a token boundary
                case '\\':
                {
                    char e = i + 1 < n ? pattern[i + 1] : '\0';
                    var (mz, nxt) = Quant(i + 2);
                    bool boundary = EscapeIsBoundary(e) && !mz; // \s+ is a boundary; \s* is not (may be absent)
                    EndRun(boundary);
                    runPrecededByBoundary = boundary;
                    i = nxt;
                    continue;
                }
                default:
                {
                    // A literal non-word char in the pattern (space, ',', ';', ':', '/', '-', ...): a
                    // required occurrence is a hard boundary; an optional one (`,?`) is not.
                    var (mz, nxt) = Quant(i + 1);
                    bool boundary = !mz;
                    EndRun(boundary);
                    runPrecededByBoundary = boundary;
                    i = nxt;
                    continue;
                }
            }
        }
        EndRun(false); // pattern end is NOT a subject boundary — an unanchored match can extend into more word chars
        return topLevelAlt ? new List<string>() : tokens.Distinct().ToList();
    }

    // \s whitespace, \b/\B word boundary, \W non-word => a boundary. \w \d \S \D might be a word char.
    // Any other escaped char is a literal: punctuation is a boundary, a letter/digit is not.
    private static bool EscapeIsBoundary(char e) => e switch
    {
        's' or 'b' or 'W' => true,                 // \s whitespace, \b word boundary, \W non-word char
        'w' or 'd' or 'D' or 'S' or 'B' => false,  // \B asserts NOT a boundary (the neighbor IS a word char)
        _ => !char.IsLetterOrDigit(e),
    };
}
