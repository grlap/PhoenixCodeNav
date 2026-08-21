using System.Diagnostics;

namespace CodeNav.Core.Discovery;

public enum GlobMatchOutcome
{
    NotMatched,
    Matched,
    BudgetExhausted,
}

/// <summary>
/// A caller-owned cumulative budget for one or more MSBuild glob matches. The budget is sticky:
/// once any segment, operation, or wall-clock limit is reached, every later match reports
/// <see cref="GlobMatchOutcome.BudgetExhausted"/>.
/// </summary>
public sealed class GlobMatchBudget
{
    public const int MaximumSupportedSegments = 1_024;

    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly long _deadline;
    private long _lastActivity;
    private bool _exhausted;

    public GlobMatchBudget(int segmentLimit, long operationLimit, TimeSpan timeLimit)
    {
        if (segmentLimit is < 1 or > MaximumSupportedSegments)
            throw new ArgumentOutOfRangeException(nameof(segmentLimit));
        if (operationLimit < 1) throw new ArgumentOutOfRangeException(nameof(operationLimit));
        if (timeLimit < TimeSpan.Zero && timeLimit != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeLimit));

        SegmentLimit = segmentLimit;
        OperationLimit = operationLimit;
        TimeLimit = timeLimit;
        _lastActivity = _started;
        if (timeLimit == Timeout.InfiniteTimeSpan)
        {
            _deadline = long.MaxValue;
        }
        else
        {
            double timestampTicks = Math.Ceiling(timeLimit.TotalSeconds * Stopwatch.Frequency);
            _deadline = timestampTicks >= long.MaxValue - _started
                ? long.MaxValue
                : _started + Math.Max(0, (long)timestampTicks);
        }
    }

    public int SegmentLimit { get; }
    public long OperationLimit { get; }
    public TimeSpan TimeLimit { get; }
    public long Operations { get; private set; }
    public long ElapsedMilliseconds =>
        (long)Math.Ceiling(Stopwatch.GetElapsedTime(_started, _lastActivity).TotalMilliseconds);

    public bool IsExhausted => _exhausted;

    /// <summary>Checks the cumulative deadline without consuming an operation.</summary>
    public bool TryContinue() => TryCharge(0);

    internal bool TryPrepare(string path, string pattern)
    {
        if (!TryCharge((long)path.Length + pattern.Length)) return false;
        if (!TryCountSegments(path) || !TryCountSegments(pattern))
        {
            _exhausted = true;
            return false;
        }
        return true;
    }

    internal bool TryCharge(long operations = 1)
    {
        if (operations < 0) throw new ArgumentOutOfRangeException(nameof(operations));
        long now = Stopwatch.GetTimestamp();
        _lastActivity = now;
        CheckDeadline(now);
        if (_exhausted) return false;
        if (operations > OperationLimit - Operations)
        {
            _exhausted = true;
            return false;
        }
        Operations += operations;
        return true;
    }

    private bool TryCountSegments(string value)
    {
        int count = 1;
        foreach (char character in value)
        {
            if (character != '/') continue;
            if (count >= SegmentLimit) return false;
            count++;
        }
        return count <= SegmentLimit;
    }

    private void CheckDeadline(long now)
    {
        if (!_exhausted && _deadline != long.MaxValue &&
            now >= _deadline)
        {
            _exhausted = true;
        }
    }
}

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
    private const int DefaultSegmentLimit = GlobMatchBudget.MaximumSupportedSegments;
    private const long DefaultOperationLimit = 4_000_000;
    private const int DefaultMilliseconds = 2_000;

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

    public static bool IsMatch(string path, string pattern) =>
        IsMatch(path, pattern, ignoreCase: true);

    /// <summary>Matches with host-appropriate path casing when a caller needs proof rather than
    /// the indexer's deliberately Windows-compatible over-approximation.</summary>
    public static bool IsMatch(string path, string pattern, bool ignoreCase)
    {
        var budget = new GlobMatchBudget(DefaultSegmentLimit, DefaultOperationLimit,
            TimeSpan.FromMilliseconds(DefaultMilliseconds));
        return Match(path, pattern, ignoreCase, budget) switch
        {
            GlobMatchOutcome.Matched => true,
            GlobMatchOutcome.NotMatched => false,
            _ => throw new InvalidOperationException(
                "MSBuild glob matching exceeded its bounded segment, operation, or time limit."),
        };
    }

    /// <summary>
    /// Stack-safe, memoized wildcard matching. A supplied budget may be shared across an entire
    /// ownership proof; exhaustion is returned explicitly and never conflated with a non-match.
    /// </summary>
    public static GlobMatchOutcome Match(string path, string pattern, bool ignoreCase,
        GlobMatchBudget budget)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryPrepare(path, pattern))
            return GlobMatchOutcome.BudgetExhausted;

        string[] pathSegments = path.Split('/');
        string[] globSegments = CollapseConsecutiveDoubleStars(pattern.Split('/'));
        long stateSlots = (long)(pathSegments.Length + 1) * (globSegments.Length + 1);

        int globWidth = globSegments.Length + 1;
        var visited = new bool[(int)stateSlots];
        var pending = new Queue<int>();
        if (!TryEnqueue(0, 0, globWidth, visited, pending, budget))
            return GlobMatchOutcome.BudgetExhausted;

        while (pending.Count > 0)
        {
            if (!budget.TryCharge()) return GlobMatchOutcome.BudgetExhausted;
            int state = pending.Dequeue();
            int pathIndex = state / globWidth;
            int globIndex = state % globWidth;
            if (pathIndex == pathSegments.Length && globIndex == globSegments.Length)
                return GlobMatchOutcome.Matched;
            if (globIndex >= globSegments.Length) continue;

            string segment = globSegments[globIndex];
            if (segment == "**")
            {
                if (!TryEnqueue(pathIndex, globIndex + 1, globWidth, visited, pending, budget))
                    return GlobMatchOutcome.BudgetExhausted;
                if (pathIndex < pathSegments.Length &&
                    !TryEnqueue(pathIndex + 1, globIndex, globWidth, visited, pending, budget))
                    return GlobMatchOutcome.BudgetExhausted;
                continue;
            }

            if (pathIndex >= pathSegments.Length) continue;
            GlobMatchOutcome segmentOutcome = MatchSegment(pathSegments[pathIndex], segment,
                ignoreCase, budget);
            if (segmentOutcome == GlobMatchOutcome.BudgetExhausted)
                return segmentOutcome;
            if (segmentOutcome == GlobMatchOutcome.Matched &&
                !TryEnqueue(pathIndex + 1, globIndex + 1, globWidth, visited, pending, budget))
                return GlobMatchOutcome.BudgetExhausted;
        }
        return GlobMatchOutcome.NotMatched;
    }

    private static bool TryEnqueue(int pathIndex, int globIndex, int globWidth, bool[] visited,
        Queue<int> pending, GlobMatchBudget budget)
    {
        if (!budget.TryCharge()) return false;
        int state = checked(pathIndex * globWidth + globIndex);
        if (visited[state]) return true;
        visited[state] = true;
        pending.Enqueue(state);
        return true;
    }

    private static string[] CollapseConsecutiveDoubleStars(string[] segments)
    {
        int write = 0;
        foreach (string segment in segments)
        {
            if (segment == "**" && write > 0 && segments[write - 1] == "**") continue;
            segments[write++] = segment;
        }
        if (write == segments.Length) return segments;
        Array.Resize(ref segments, write);
        return segments;
    }

    /// <summary>Single-segment wildcard match ('*' any run, '?' one char).</summary>
    private static GlobMatchOutcome MatchSegment(string text, string pattern, bool ignoreCase,
        GlobMatchBudget budget)
    {
        // Iterative backtracking wildcard match. Every retry consumes the caller's cumulative
        // operation budget so large single-segment patterns cannot bypass the state-machine cap.
        int textIndex = 0, patternIndex = 0, starPattern = -1, starText = -1;
        while (textIndex < text.Length)
        {
            if (!budget.TryCharge()) return GlobMatchOutcome.BudgetExhausted;
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == '?' || CharactersEqual(pattern[patternIndex],
                    text[textIndex], ignoreCase)))
            {
                textIndex++;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starPattern = patternIndex++;
                starText = textIndex;
            }
            else if (starPattern >= 0)
            {
                patternIndex = starPattern + 1;
                textIndex = ++starText;
            }
            else
            {
                return GlobMatchOutcome.NotMatched;
            }
        }
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            if (!budget.TryCharge()) return GlobMatchOutcome.BudgetExhausted;
            patternIndex++;
        }
        return patternIndex == pattern.Length
            ? GlobMatchOutcome.Matched
            : GlobMatchOutcome.NotMatched;
    }

    private static bool CharactersEqual(char left, char right, bool ignoreCase) =>
        ignoreCase ? char.ToUpperInvariant(left) == char.ToUpperInvariant(right) : left == right;
}
