namespace CodeNav.Tests;

/// <summary>
/// Pins the cross-agent TermAl review contract. These are intentionally content-level tests:
/// the command bodies are executable agent policy, and a wording drift can remove an
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
    public void ParentPinsDualSpawnAndDurableFanInWithoutHashIdentity()
    {
        string text = Read(".claude", "commands", "review-with-delegate.md");

        Assert.Contains("Attempt exactly two reviewer session spawns", text);
        Assert.Contains("Call `termal_spawn_session` exactly twice", text);
        Assert.Contains("Never retry either slot", text);
        Assert.Contains("Review-policy, instruction, and tracked Beads configuration files are ordinary review targets", text);
        Assert.DoesNotContain("this self-hosted gate cannot certify", text);
        Assert.Contains("git --no-optional-locks status --short", text);
        Assert.DoesNotContain("git ls-files --others --ignored --exclude-standard", text);
        Assert.Contains("If any inventory command fails or its path output is truncated or malformed, return INCONCLUSIVE", text);
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
        Assert.DoesNotContain("Confirm reintroduction-test evidence", text);
        Assert.Contains("Run `dotnet build PhoenixCodeNav.sln -c Release --no-restore`", text);
        Assert.Contains("Run `pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1`", text);
        Assert.Contains("Missing submodules, mismatched external commits, missing reusable indexes, or any harness failure stop the review gate", text);
        Assert.Contains(":(exclude).beads/interactions.jsonl", text);
        Assert.Contains(":(exclude).beads/issues.jsonl", text);
        Assert.Contains(":(exclude).beads/events.jsonl", text);
        Assert.DoesNotContain("target identity", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git hash-object", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patch hashes", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --check", text);
        Assert.Contains("After validation, restart all of Step 1 from its path-only inventories", text);
        Assert.Contains("Reapply no-follow containment before any diff check, content read, or spawn", text);
        Assert.Contains("If the sorted implementation path inventory changed, repeat Step 2 against the new inventory", text);
        Assert.Contains("Do not require reviewer-computed hashes or identities", text);
        Assert.DoesNotContain("then invoke `/review-with-delegate` again", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Generated `.beads/interactions.jsonl`, `.beads/issues.jsonl`, and `.beads/events.jsonl` changes are tracker bookkeeping", text);
        Assert.Contains("do not hash, prefix-validate, parse, or compare those generated ledgers", text);
        Assert.Contains("Generated-ledger-only changes preserve the review verdict", text);
        Assert.Contains("Medium and Low findings are allowed when they are reconciled in Beads", text);
        Assert.Contains("`NOT CLEAN` is reserved for unresolved Critical or High findings", text);

        int solutionTests = text.IndexOf(
            "Run `dotnet test PhoenixCodeNav.sln -c Release --no-build --no-restore`",
            StringComparison.Ordinal);
        int externalIntegration = text.IndexOf(
            "Run `pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1`",
            StringComparison.Ordinal);
        int postValidationInventory = text.IndexOf(
            "After validation, restart all of Step 1 from its path-only inventories",
            StringComparison.Ordinal);
        Assert.True(solutionTests >= 0 && solutionTests < externalIntegration &&
            externalIntegration < postValidationInventory,
            "The external MCP integration harness must run after solution tests and before delegation inventories restart.");

        int pathInventory = text.IndexOf("git ls-files --others --exclude-standard", StringComparison.Ordinal);
        int containmentCheck = text.IndexOf("Before diffing or opening target content", StringComparison.Ordinal);
        int diffCheck = text.IndexOf("git --no-pager diff --no-ext-diff --no-textconv --no-color --check", StringComparison.Ordinal);
        Assert.True(pathInventory >= 0 && pathInventory < containmentCheck &&
            containmentCheck < diffCheck,
            "The parent inventory and containment boundaries must run before content-bearing diff checks.");
    }

    [Fact]
    public void LocalCommandIsAParserCompatibleNonNestingLeaf()
    {
        string text = Read(".claude", "commands", "review-local.md");

        Assert.Contains("You are a delegated child session for TermAl delegation", text);
        Assert.Contains("Review-policy, instruction, and tracked Beads configuration files are ordinary review targets", text);
        Assert.Contains("inspect their changed content instead of refusing the review", text);
        Assert.DoesNotContain("this dirty command/lens set is reviewing its own trust policy", text);
        Assert.Contains("git --no-optional-locks status --short", text);
        Assert.DoesNotContain("git ls-files --others --ignored --exclude-standard", text);
        Assert.Contains("If any inventory command fails or its path output is truncated or malformed", text);
        Assert.Contains("`Review verdict: INCONCLUSIVE` and stop before reading target content", text);
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
        Assert.DoesNotContain("Historical red-run/reintroduction evidence is owned and reported by the parent validation gate", text);
        Assert.Contains("Regression and contract tests structurally exercise the changed behavior", text);
        Assert.Contains("git --no-pager diff --binary --no-ext-diff --no-textconv --no-color", text);
        Assert.Contains("git --no-pager diff --cached --binary --no-ext-diff --no-textconv --no-color", text);
        Assert.Contains(":(exclude).beads/interactions.jsonl", text);
        Assert.Contains(":(exclude).beads/issues.jsonl", text);
        Assert.Contains(":(exclude).beads/events.jsonl", text);
        Assert.Contains("Reviewed paths: ...", text);
        Assert.Contains("CLEAN when there is no actionable Critical or High finding", text);
        Assert.Contains("Medium and Low findings are allowed", text);
        Assert.DoesNotContain("Target identity:", text);
        Assert.DoesNotContain("git hash-object", text, StringComparison.OrdinalIgnoreCase);

        int pathInventory = text.IndexOf("git ls-files --others --exclude-standard", StringComparison.Ordinal);
        int containmentCheck = text.IndexOf("Before diffing or opening target content", StringComparison.Ordinal);
        int contentDiff = text.IndexOf("git --no-pager diff --binary --no-ext-diff --no-textconv --no-color", StringComparison.Ordinal);
        int instructionRead = text.IndexOf("Read the current `CLAUDE.md` and `AGENTS.md`", StringComparison.Ordinal);
        Assert.True(pathInventory >= 0 && pathInventory < containmentCheck &&
            containmentCheck < instructionRead && instructionRead < contentDiff,
            "Current instructions and containment checks must precede patch-content reads.");
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
        Assert.Contains("Generated `.beads/interactions.jsonl`, `.beads/issues.jsonl`, and `.beads/events.jsonl`", agents);
        Assert.Contains("Other tracked `.beads`", agents);
        Assert.Contains("Reviewers do not compute or report Git-object, patch, or content hashes", agents);
        Assert.Contains("Critical and High findings block check-in", agents);
        Assert.Contains("Medium and Low findings must be reconciled in", agents);
        Assert.Contains("Beads but do not block check-in", agents);
        const string externalIntegration = "pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1";
        Assert.Contains(externalIntegration, agents);
        Assert.Contains(externalIntegration, claude);

        string allReviewAssets = claude + "\n" + agents + "\n" + string.Join("\n",
            Directory.GetFiles(Path.Combine(Root(), ".claude"), "*.md", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("fit_friends", allReviewAssets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fit Friends", allReviewAssets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Flutter", allReviewAssets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Supabase", allReviewAssets, StringComparison.OrdinalIgnoreCase);
    }
}
