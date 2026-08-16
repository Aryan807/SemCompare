using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace SemCompare.Services;

/// <summary>
/// Git repository access. Supports both local paths and remote GitHub URLs.
/// For GitHub URLs the caller must supply an OAuth access token so LibGit2Sharp
/// can authenticate with HTTPS credential injection.
/// </summary>
public class GitService
{
    // ── Remote / GitHub helpers ───────────────────────────────────────────────

    /// <summary>
    /// Clones (or fetches) a GitHub repo into a deterministic temp directory.
    /// Returns the local path. Safe to call repeatedly — will fetch instead of re-cloning.
    /// </summary>
    public string EnsureLocalClone(string cloneUrl, string accessToken)
    {
        var localPath = GetLocalPath(cloneUrl);

        var creds = BuildCredentialHandler(accessToken);

        if (Directory.Exists(Path.Combine(localPath, ".git")))
        {
            // Already cloned — fetch latest
            using var repo = new Repository(localPath);
            var remote  = repo.Network.Remotes["origin"];
            var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
            Commands.Fetch(repo, "origin", refSpecs, new FetchOptions { CredentialsProvider = creds }, null);
        }
        else
        {
            Directory.CreateDirectory(localPath);
            var cloneOptions = new CloneOptions();
            cloneOptions.FetchOptions.CredentialsProvider = creds;
            Repository.Clone(cloneUrl, localPath, cloneOptions);
        }

        return localPath;
    }

    /// <summary>Deterministic local path for a clone URL — same URL always maps to the same dir.</summary>
    public static string GetLocalPath(string cloneUrl)
    {
        var slug = cloneUrl
            .Replace("https://github.com/", "")
            .Replace("/", "_")
            .Replace(".git", "")
            .Trim('_');
        return Path.Combine(Path.GetTempPath(), "semanticdiff_repos", slug);
    }

    // ── Core diff operations ──────────────────────────────────────────────────

    /// <summary>Lists all C# files that changed between two refs.</summary>
    public List<string> GetChangedCSharpFiles(string repoPath, string fromRef, string toRef)
    {
        using var repo = new Repository(repoPath);
        var fromCommit = ResolveCommit(repo, fromRef);
        var toCommit   = ResolveCommit(repo, toRef);

        var diff = repo.Diff.Compare<TreeChanges>(fromCommit.Tree, toCommit.Tree);
        return diff
            .Where(c => c.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Path)
            .ToList();
    }

    /// <summary>Reads the content of a file at a specific commit.</summary>
    public string? ReadFileAtCommit(string repoPath, string commitRef, string filePath)
    {
        using var repo = new Repository(repoPath);
        var commit = ResolveCommit(repo, commitRef);
        var entry  = commit[filePath];
        if (entry?.Target is not Blob blob) return null;
        return blob.GetContentText();
    }

    /// <summary>Returns branches available in the local clone.</summary>
    public List<string> GetLocalBranches(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Branches
            .Where(b => !b.IsRemote || b.FriendlyName.StartsWith("origin/"))
            .Select(b => b.IsRemote
                ? b.FriendlyName.Replace("origin/", "")
                : b.FriendlyName)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>Returns the last N commits on a branch from the local clone.</summary>
    public List<LocalCommitInfo> GetRecentCommits(string repoPath, string branchName, int count = 30)
    {
        using var repo = new Repository(repoPath);

        // Try local branch first, then remote tracking
        var branch = repo.Branches[branchName]
                  ?? repo.Branches[$"origin/{branchName}"];

        if (branch == null) return new();

        return branch.Commits
            .Take(count)
            .Select(c => new LocalCommitInfo(
                c.Sha,
                c.Sha[..7],
                c.MessageShort,
                c.Author.Name,
                c.Author.When.DateTime
            ))
            .ToList();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static CredentialsHandler BuildCredentialHandler(string accessToken) =>
        (_, _, _) => new UsernamePasswordCredentials
        {
            Username = "x-oauth-token",
            Password = accessToken
        };

    private static Commit ResolveCommit(Repository repo, string reference)
    {
        var obj = repo.Lookup(reference)
               ?? repo.Lookup(repo.Branches[reference]?.Tip.Sha ?? reference)
               ?? repo.Lookup(repo.Branches[$"origin/{reference}"]?.Tip.Sha ?? reference);

        return obj as Commit
            ?? throw new InvalidOperationException($"Could not resolve '{reference}' to a commit.");
    }
}

public record LocalCommitInfo(
    string   Sha,
    string   ShortSha,
    string   Message,
    string   AuthorName,
    DateTime Date
);
