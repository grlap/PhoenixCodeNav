namespace CodeNav.Core.Discovery;

/// <summary>
/// Owns: matching MSBuild item wildcard patterns (&lt;Compile Include/Remove&gt;) against workspace-
/// relative file paths, in memory — '**' spans zero or more directory segments, '*' and '?' match
/// within a segment, comparison is case-insensitive (Windows item semantics). Patterns arrive
/// already normalized (forward slashes, workspace-relative via ProjectFileParser.NormalizeRelative).
/// Does not own: reading csproj files (ProjectFileParser) or deciding ownership precedence
/// (CompileItemResolver). Conditions are deliberately ignored upstream — pragmatic over-inclusion.
/// </summary>
public static class MsBuildGlob
{
    public static bool ContainsWildcard(string pattern) =>
        pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0;

    /// <summary>The pattern's literal path prefix up to (and excluding) the first wildcard segment —
    /// used to range-scan sorted file paths instead of testing every file. "" when the pattern starts
    /// with a wildcard segment.</summary>
    public static string LiteralPrefix(string pattern)
    {
        int wild = pattern.IndexOfAny(new[] { '*', '?' });
        if (wild < 0) return pattern;
        int lastSlash = pattern.LastIndexOf('/', wild == 0 ? 0 : wild - 1);
        return lastSlash < 0 ? "" : pattern[..(lastSlash + 1)];
    }

    public static bool IsMatch(string path, string pattern)
    {
        var p = path.Split('/');
        var g = pattern.Split('/');
        return MatchSegments(p, 0, g, 0);
    }

    private static bool MatchSegments(string[] path, int pi, string[] glob, int gi)
    {
        while (gi < glob.Length)
        {
            string seg = glob[gi];
            if (seg == "**")
            {
                // '**' matches zero or more whole segments; try every suffix.
                for (int skip = pi; skip <= path.Length; skip++)
                {
                    if (MatchSegments(path, skip, glob, gi + 1)) return true;
                }
                return false;
            }
            if (pi >= path.Length) return false;
            if (!MatchSegment(path[pi], seg)) return false;
            pi++;
            gi++;
        }
        return pi == path.Length;
    }

    /// <summary>Single-segment wildcard match ('*' any run, '?' one char), case-insensitive.</summary>
    private static bool MatchSegment(string text, string pattern)
    {
        // Iterative backtracking wildcard match.
        int t = 0, p = 0, starP = -1, starT = -1;
        while (t < text.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
