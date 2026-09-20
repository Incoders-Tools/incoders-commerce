using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Commerce.Integration;

/// <summary>
/// Covers the commerce-cicd-path-filtering change's Phase 4 structural
/// regression test (design.md Testing Strategy "Unit (structural)" row;
/// Success Criteria "railway.json byte-identical"). Asserts, by parsing
/// text rather than executing a workflow:
/// 1. <c>.github/workflows/release.yml</c> carries no <c>paths:</c>/
///    <c>paths-ignore:</c> trigger key anywhere (that key reproduces the
///    "Pending forever" required-check footgun design.md rejects).
/// 2. The <c>ci-gate</c> job's only <c>if:</c> predicate is <c>always()</c>
///    (design.md: "NO path condition and NO event condition may ever be
///    added to this job").
/// 3. <c>railway.json</c> and <c>.github/release-authorization.yml</c> are
///    byte-identical to their pre-change contents (non-goal boundary /
///    Decision 4 — this change never touches either file).
/// </summary>
public sealed class CiPipelineGatingStructuralTests
{
    // Recorded before this change touched anything (sha256sum railway.json
    // .github/release-authorization.yml). If either file is ever
    // intentionally modified by a later change, this hash must be
    // regenerated as part of that change, not silently bypassed.
    private const string RailwayJsonSha256 =
        "dc18b863c5a51edc1403d52b1ba8fcd5d202b1ffd447cda21c1ce3bd73041854";

    private const string ReleaseAuthorizationSha256 =
        "fa7e269a6d460964b3aceb6353b6817af966df7864097fc5ecfe3dcc34eb6260";

    private static DirectoryInfo ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (Commerce.sln) from " + AppContext.BaseDirectory);
        }

        return dir;
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var repoRoot = ResolveRepoRoot();
        var path = Path.Combine(new[] { repoRoot.FullName }.Concat(relativeParts).ToArray());
        return File.ReadAllText(path);
    }

    private static string Sha256Of(string relativePath)
    {
        var repoRoot = ResolveRepoRoot();
        var fullPath = Path.Combine(repoRoot.FullName, relativePath);
        using var stream = File.OpenRead(fullPath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void ReleaseWorkflow_HasNoPathsOrPathsIgnoreKey()
    {
        var yaml = ReadRepoFile(".github", "workflows", "release.yml");

        // Match a YAML mapping key named `paths:` or `paths-ignore:` at any
        // indentation level (the footgun applies regardless of nesting
        // depth: workflow-level `on.pull_request.paths` and job-level
        // `paths` both reproduce it).
        var pathsKey = new Regex(@"^\s*paths(-ignore)?\s*:", RegexOptions.Multiline);

        Assert.False(
            pathsKey.IsMatch(yaml),
            "release.yml must never contain a `paths:`/`paths-ignore:` key — a path-filtered workflow/job never starts, so its required check hangs Pending forever (design.md Filter mechanism decision).");
    }

    [Fact]
    public void CiGateJob_HasNoIfPredicateOtherThanAlways()
    {
        var yaml = ReadRepoFile(".github", "workflows", "release.yml");

        var ciGateMatch = Regex.Match(
            yaml,
            @"^  ci-gate:\r?\n(?<body>(?:^ {4}.*\r?\n?)*)",
            RegexOptions.Multiline);

        Assert.True(ciGateMatch.Success, "release.yml must contain a top-level `ci-gate:` job.");

        var body = ciGateMatch.Groups["body"].Value;
        var ifLines = Regex.Matches(body, @"^\s*if\s*:\s*(.+?)\s*$", RegexOptions.Multiline);

        // ci-gate scopes to pull_request events only (see release.yml's own
        // comment on this job): pushing to a branch with an open PR fires
        // both a push and a pull_request event for the same commit, and the
        // push-triggered build always fails its by-design Publication gate
        // step (ADR-004), which produced a second, irrelevant ci-gate
        // failure that confused required-check merge gating. `always()`
        // stays load-bearing so a skipped/failed `needs` entry can't skip
        // ci-gate itself within a pull_request run.
        Assert.True(ifLines.Count > 0, "ci-gate must declare an `if:` predicate.");
        Assert.All(ifLines, m => Assert.Equal(
            "always() && github.event_name == 'pull_request'", m.Groups[1].Value.Trim()));
    }

    [Fact]
    public void RailwayJson_IsByteIdenticalToPreChangeContents()
    {
        Assert.Equal(RailwayJsonSha256, Sha256Of("railway.json"));
    }

    [Fact]
    public void ReleaseAuthorizationYml_IsByteIdenticalToPreChangeContents()
    {
        Assert.Equal(ReleaseAuthorizationSha256, Sha256Of(Path.Combine(".github", "release-authorization.yml")));
    }
}
