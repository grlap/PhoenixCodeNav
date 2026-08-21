namespace CodeNav.Mcp;

/// <summary>
/// Owns: alternate token-form suggestions for dead-end text searches — the "Mode4" vs "Mode 4" class
/// of miss (field evidence: an agent searched Mode4, the comments say "Mode 4", got a correct 0 and
/// fell back to grep). Split inserts spaces at case/digit boundaries inside tokens; Join removes the
/// spaces of a multi-token query. Variants are PROBED and surfaced as didYouMean — never silently
/// substituted (the no-silent-fallback principle from the search grading work).
/// Does not own: running the probes (SearchText's dead-end path) or tokenization (IndexQueries).
/// </summary>
internal static class QueryVariants
{
    /// <summary>"Mode4" -> "Mode 4", "AsyncAPI" -> "Async API". Null when there is nothing to split.</summary>
    public static string? SplitVariant(string query)
    {
        var sb = new System.Text.StringBuilder(query.Length + 4);
        bool changed = false;
        for (int i = 0; i < query.Length; i++)
        {
            char c = query[i];
            if (i > 0)
            {
                char p = query[i - 1];
                bool boundary =
                    (char.IsLetter(p) && char.IsDigit(c)) ||
                    (char.IsDigit(p) && char.IsLetter(c)) ||
                    (char.IsLower(p) && char.IsUpper(c));
                if (boundary) { sb.Append(' '); changed = true; }
            }
            sb.Append(c);
        }
        return changed ? sb.ToString() : null;
    }

    /// <summary>"Mode 4" -> "Mode4" (the joined single-token form). Splits on ANY whitespace (tabs
    /// and NBSP appear in pasted code/log fragments). Null for single-token queries.</summary>
    public static string? JoinVariant(string query)
    {
        var parts = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? string.Concat(parts) : null;
    }
}
