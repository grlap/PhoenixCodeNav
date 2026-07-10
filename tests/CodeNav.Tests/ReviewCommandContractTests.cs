namespace CodeNav.Tests;

/// <summary>
/// Pins the cross-agent TermAl review contract. These are intentionally content-level tests:
/// the command bodies are executable agent policy, and a wording drift can remove an exact
/// orchestration guarantee just as surely as a code change can remove a branch.
/// </summary>
public class ReviewCommandContractTests
{
    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhoenixCodeNav.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    [Theory]
    [InlineData("review-local")]
    [InlineData("review-with-delegate")]
    public void CommandsHaveTermAlDiscoverableFrontmatter(string name)
    {
        string text = Read(".claude", "commands", name + ".md");
        Assert.StartsWith(
            $"---\nname: {name}\ndescription: ",
            text,
            StringComparison.Ordinal);
        Assert.Contains("\nmetadata:\n  termal:\n    title:\n      strategy: default\n---\n", text);
    }

    [Fact]
    public void ParentPinsExactDualSpawnAndDurableFanIn()
    {
        string text = Read(".claude", "commands", "review-with-delegate.md");

        Assert.Contains("Attempt exactly two reviewer session spawns", text);
        Assert.Contains("Call `termal_spawn_session` exactly twice", text);
        Assert.Contains("Never retry either slot", text);
        Assert.Contains("Normalize every repository-relative target path to `/` separators", text);
        Assert.Contains("case-insensitive Windows matching semantics", text);
        Assert.Contains("this self-hosted gate cannot certify the change: return INCONCLUSIVE and do not spawn reviewers", text);
        Assert.Contains("At any depth, treat a basename of `AGENTS.md`, `AGENTS.override.md`, `CLAUDE.md`, `CLAUDE.local.md`, or `.mcp.json`", text);
        Assert.Contains("any path containing an `.agents`, `.claude`, or `.codex` directory segment", text);
        Assert.Contains("A dirty self-review may be supplemental evidence only, never the required CLEAN gate", text);
        Assert.Contains("git --no-optional-locks status --short", text);
        Assert.Contains("git ls-files --others --ignored --exclude-standard", text);
        Assert.Contains("If any inventory command fails or its path output is truncated or malformed, return INCONCLUSIVE", text);
        Assert.Contains("':(icase,glob)**/AGENTS.md'", text);
        Assert.Contains("':(icase,glob)**/AGENTS.override.md'", text);
        Assert.Contains("':(icase,glob)**/CLAUDE.md'", text);
        Assert.Contains("':(icase,glob)**/CLAUDE.local.md'", text);
        Assert.Contains("':(icase,glob)**/.mcp.json'", text);
        Assert.Contains("':(icase,glob)**/.agents'", text);
        Assert.Contains("':(icase,glob)**/.agents/**'", text);
        Assert.Contains("':(icase,glob)**/.claude'", text);
        Assert.Contains("':(icase,glob)**/.claude/**'", text);
        Assert.Contains("':(icase,glob)**/.codex'", text);
        Assert.Contains("':(icase,glob)**/.codex/**'", text);
        Assert.Contains("':(icase,literal)tests/CodeNav.Tests/ReviewCommandContractTests.cs'", text);
        Assert.DoesNotContain("':(glob)", text);
        Assert.Contains("inspect changed-entry metadata without following links or reparse points", text);
        Assert.Contains("Treat tracked symlinks as Git link metadata and never dereference them", text);
        Assert.Contains("if an untracked symlink/junction/reparse point or any resolved path can escape the root, return INCONCLUSIVE without reading it", text);
        Assert.Equal(1, Count(text, "- `agent`: `Codex`"));
        Assert.Equal(1, Count(text, "- `agent`: `Claude`"));
        Assert.Equal(2, Count(text, "- `prompt`: `/review-local`"));
        Assert.Equal(2, Count(text, "- `writePolicy`: exactly `{\"kind\":\"readOnly\"}`"));
        Assert.Contains("termal_resume_after_delegations", text);
        Assert.Contains("Success requires a successful tool call containing a non-empty `wait.id`", text);
        Assert.Contains("`resumePromptQueued` and `resumeDispatchRequested` may both legitimately be `false`", text);
        Assert.Contains("stop this turn immediately", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not poll with `termal_wait_delegations`", text);
        Assert.Contains("complete non-truncated packets", text);
        Assert.Contains("Confirm reintroduction-test evidence for behavior changes or bug fixes", text);
        Assert.Contains("target identity", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git --no-pager diff --binary --no-ext-diff --no-textconv --no-color", text);
        Assert.Contains("git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --check", text);
        Assert.Contains("After validation, restart all of Step 1 from its path-only ordinary and ignored inventories", text);
        Assert.Contains("Reapply bootstrap trust matching and no-follow containment before any diff check, content hash, or spawn", text);
        Assert.Contains("If validation changed the identity, repeat Step 2 against the new identity and then restart all of Step 1 again", text);
        Assert.Contains("recompute the current parent identity", text);
        Assert.DoesNotContain("then invoke `/review-with-delegate` again", text, StringComparison.OrdinalIgnoreCase);

        int ignoredTrustInventory = text.IndexOf("git ls-files --others --ignored --exclude-standard", StringComparison.Ordinal);
        int bootstrapCheck = text.IndexOf("If any target matches, this self-hosted gate cannot certify", StringComparison.Ordinal);
        int containmentCheck = text.IndexOf("Before hashing, diffing, or opening target content", StringComparison.Ordinal);
        int diffCheck = text.IndexOf("git --no-pager diff --no-ext-diff --no-textconv --no-color --check", StringComparison.Ordinal);
        int contentHash = text.IndexOf("a hash of `git --no-pager diff --binary --no-ext-diff --no-textconv --no-color`", StringComparison.Ordinal);
        Assert.True(ignoredTrustInventory >= 0 && ignoredTrustInventory < bootstrapCheck &&
            bootstrapCheck < containmentCheck &&
            containmentCheck < diffCheck && containmentCheck < contentHash,
            "The parent bootstrap and containment boundaries must run before content-bearing diff checks or hashing.");
    }

    [Fact]
    public void LocalCommandIsAParserCompatibleNonNestingLeaf()
    {
        string text = Read(".claude", "commands", "review-local.md");

        Assert.Contains("You are a delegated child session for TermAl delegation", text);
        Assert.Contains("Normalize repository-relative target paths to `/` separators", text);
        Assert.Contains("use case-insensitive matching on Windows", text);
        Assert.Contains("this dirty command/lens set is reviewing its own trust policy", text);
        Assert.Contains("At any depth, a basename of `AGENTS.md`, `AGENTS.override.md`, `CLAUDE.md`, `CLAUDE.local.md`, or `.mcp.json`", text);
        Assert.Contains("any `.agents`, `.claude`, or `.codex` directory segment", text);
        Assert.Contains("emit a lifecycle `Status: completed` packet with `Review verdict: INCONCLUSIVE`", text);
        Assert.Contains("Do not open target-file content until the bootstrap check below is complete", text);
        Assert.Contains("stop before the broad scan or loading any reviewer lens or changed trust-surface content", text);
        Assert.Contains("Do not attempt advisory inspection with dirty instructions", text);
        Assert.Contains("git --no-optional-locks status --short", text);
        Assert.Contains("git ls-files --others --ignored --exclude-standard", text);
        Assert.Contains("If any inventory command fails or its path output is truncated or malformed", text);
        Assert.Contains("`Review verdict: INCONCLUSIVE` and stop before reading target content", text);
        Assert.Contains("':(icase,glob)**/AGENTS.md'", text);
        Assert.Contains("':(icase,glob)**/AGENTS.override.md'", text);
        Assert.Contains("':(icase,glob)**/CLAUDE.md'", text);
        Assert.Contains("':(icase,glob)**/CLAUDE.local.md'", text);
        Assert.Contains("':(icase,glob)**/.mcp.json'", text);
        Assert.Contains("':(icase,glob)**/.agents'", text);
        Assert.Contains("':(icase,glob)**/.agents/**'", text);
        Assert.Contains("':(icase,glob)**/.claude'", text);
        Assert.Contains("':(icase,glob)**/.claude/**'", text);
        Assert.Contains("':(icase,glob)**/.codex'", text);
        Assert.Contains("':(icase,glob)**/.codex/**'", text);
        Assert.Contains("':(icase,literal)tests/CodeNav.Tests/ReviewCommandContractTests.cs'", text);
        Assert.DoesNotContain("':(glob)", text);
        Assert.Contains("inspect changed-entry metadata without following links or reparse points", text);
        Assert.Contains("apply the same lstat/containment check to the instruction files about to be read", text);
        Assert.Contains("Treat tracked symlinks as Git link metadata and never dereference them", text);
        Assert.Contains("if an untracked symlink/junction/reparse point or any resolved path can escape the root, return `Review verdict: INCONCLUSIVE` without reading it", text);
        Assert.Contains("Claude Task agents", text);
        Assert.Contains("Codex collaboration agents", text);
        Assert.Contains("Status: completed", text);
        Assert.DoesNotContain("Status: INCONCLUSIVE", text);
        Assert.Contains("Review verdict: CLEAN | NOT CLEAN | INCONCLUSIVE", text);
        Assert.Contains("- High `file:line` - Description and evidence.", text);
        Assert.Contains("Findings:\n- None", text);
        Assert.Contains("Every actionable finding must be one physical list line", text);
        Assert.DoesNotContain("**[High]**", text);
        Assert.DoesNotContain("\n  - Reproduction:", text);
        Assert.Contains("Commands Run:", text);
        Assert.Contains("Files Inspected:", text);
        Assert.Contains("Historical red-run/reintroduction evidence is owned and reported by the parent validation gate", text);
        Assert.Contains("git --no-pager diff --binary --no-ext-diff --no-textconv --no-color", text);
        Assert.Contains("git --no-pager diff --cached --binary --no-ext-diff --no-textconv --no-color", text);

        int ignoredTrustInventory = text.IndexOf("git ls-files --others --ignored --exclude-standard", StringComparison.Ordinal);
        int bootstrapCheck = text.IndexOf("If any target matches, this dirty command/lens set", StringComparison.Ordinal);
        int containmentCheck = text.IndexOf("Before hashing, diffing, or opening target content", StringComparison.Ordinal);
        int contentDiff = text.IndexOf("git --no-pager diff --binary --no-ext-diff --no-textconv --no-color", StringComparison.Ordinal);
        int instructionRead = text.IndexOf("Read the now-unchanged `CLAUDE.md` and `AGENTS.md`", StringComparison.Ordinal);
        Assert.True(ignoredTrustInventory >= 0 && ignoredTrustInventory < bootstrapCheck &&
            bootstrapCheck < containmentCheck &&
            containmentCheck < instructionRead && instructionRead < contentDiff,
            "Trusted instructions and containment checks must precede patch-content reads.");
    }

    [Fact]
    public void AllRequiredPhoenixReviewerLensesExistAndAreRequired()
    {
        string[] expected =
        {
            "architecture.md",
            "index-freshness.md",
            "semantic-correctness.md",
            "mcp-contract.md",
            "security.md",
            "performance.md",
            "testing.md",
        };
        string reviewerDir = Path.Combine(Root(), ".claude", "reviewers");
        string[] actual = Directory.GetFiles(reviewerDir, "*.md")
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        foreach (string file in expected)
        {
            Assert.Contains(file, actual);
        }

        string local = Read(".claude", "commands", "review-local.md");
        Assert.Contains("Discover and read every `.claude/reviewers/*.md` file", local);
        Assert.Contains("Apply each checklist inline against the same complete change set", local);
        Assert.Contains("Do not spawn agents for individual lenses", local);
        Assert.Contains("If the lens directory is absent, empty, or any lens is unreadable, return `INCONCLUSIVE`", local);
        Assert.Contains("Require every listed file to exist and be readable. Extra reviewer files are allowed and must also be applied.", local);

        foreach (string file in expected)
        {
            Assert.Contains($"`.claude/reviewers/{file}`", local);

            string lens = Read(".claude", "reviewers", file);
            Assert.StartsWith("# ", lens, StringComparison.Ordinal);
            Assert.Contains("\n## What to check\n", lens);
            Assert.Contains("\n## What NOT to flag\n", lens);
            Assert.True(lens.Length >= 1_000, $"Reviewer lens {file} is unexpectedly empty or truncated.");
        }
    }

    [Fact]
    public void ReviewPolicyIsMirroredAndContainsNoSourceProjectLeakage()
    {
        const string marker = "## Review System - TermAl (Codex + Claude)";
        string claude = Read("CLAUDE.md");
        string agents = Read("AGENTS.md");
        Assert.Contains("## Commit Discipline - NEVER check in without review", agents);
        Assert.Equal(claude[claude.IndexOf(marker, StringComparison.Ordinal)..],
            agents[agents.IndexOf(marker, StringComparison.Ordinal)..]);

        string allReviewAssets = claude + "\n" + agents + "\n" + string.Join("\n",
            Directory.GetFiles(Path.Combine(Root(), ".claude"), "*.md", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("fit_friends", allReviewAssets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fit Friends", allReviewAssets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Flutter", allReviewAssets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Supabase", allReviewAssets, StringComparison.OrdinalIgnoreCase);
    }
}
