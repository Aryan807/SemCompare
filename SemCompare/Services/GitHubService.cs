using Octokit;
using SemCompare.Models;

namespace SemCompare.Services;

/// <summary>
/// Wraps Octokit to provide GitHub API access scoped to the authenticated user's token.
/// All methods accept the OAuth access token obtained after login so they work per-user.
/// </summary>
public class GitHubService
{
    private GitHubClient BuildClient(string accessToken)
    {
        var client = new GitHubClient(new ProductHeaderValue("SemCompare"));
        client.Credentials = new Credentials(accessToken);
        return client;
    }

    /// <summary>
    /// Returns all repos the authenticated user can access (own + org member + collaborator).
    /// Sorted by pushed_at descending so most-recently-active repos appear first.
    /// </summary>
    public async Task<List<GitHubRepoInfo>> GetAccessibleReposAsync(string accessToken)
    {
        var client = BuildClient(accessToken);
        var request = new RepositoryRequest
        {
            Type       = RepositoryType.All,
            Sort       = RepositorySort.Pushed,
            Direction  = SortDirection.Descending
        };

        var options = new ApiOptions
        {
            PageSize  = 100,
            PageCount = 1
        };

        var repos = await client.Repository.GetAllForCurrent(request, options);
        return repos
            .Where(r => string.Equals(r.Language, "C#", StringComparison.OrdinalIgnoreCase))
            .Select(r => new GitHubRepoInfo(
            r.Id,
            r.FullName,
            r.CloneUrl,
            r.Private,
            r.Description ?? "",
            r.Language   ?? "",
            r.UpdatedAt.DateTime
        )).ToList();
    }

    /// <summary>
    /// Returns all branches for a repo. Remote names are stripped (origin/main → main).
    /// </summary>
    public async Task<List<string>> GetBranchesAsync(string accessToken, string owner, string repo)
    {
        var client   = BuildClient(accessToken);
        var branches = await client.Repository.Branch.GetAll(owner, repo);
        return branches.Select(b => b.Name).OrderBy(n => n).ToList();
    }

    /// <summary>
    /// Returns the last N commits on a branch, as lightweight summary records.
    /// </summary>
    public async Task<List<GitHubCommitInfo>> GetCommitsAsync(
        string accessToken, string owner, string repo, string branch, int count = 30)
    {
        var client  = BuildClient(accessToken);
        var request = new CommitRequest { Sha = branch };
        var options = new ApiOptions { PageSize = count, PageCount = 1 };

        var commits = await client.Repository.Commit.GetAll(owner, repo, request, options);
        return commits.Select(c => new GitHubCommitInfo(
            c.Sha,
            c.Sha[..7],
            c.Commit.Message.Split('\n')[0],   // first line only
            c.Commit.Author.Name,
            c.Commit.Author.Date.DateTime
        )).ToList();
    }

    /// <summary>
    /// Looks up or creates an AppUser record from GitHub identity claims.
    /// Updates display name / avatar on each login.
    /// </summary>
    public async Task<AppUser> UpsertUserAsync(
        SemCompare.Data.DiffDbContext db,
        string githubId, string login, string displayName, string avatarUrl)
    {
        var user = db.AppUsers.FirstOrDefault(u => u.GitHubId == githubId);
        if (user == null)
        {
            user = new AppUser
            {
                GitHubId    = githubId,
                Login       = login,
                DisplayName = displayName,
                AvatarUrl   = avatarUrl
            };
            db.AppUsers.Add(user);
        }
        else
        {
            user.Login       = login;
            user.DisplayName = displayName;
            user.AvatarUrl   = avatarUrl;
            user.LastSeenAt  = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return user;
    }
}

// ── Lightweight DTOs (no Octokit types leak outside this service) ─────────────

public record GitHubRepoInfo(
    long   Id,
    string FullName,
    string CloneUrl,
    bool   IsPrivate,
    string Description,
    string Language,
    DateTime UpdatedAt
);

public record GitHubCommitInfo(
    string Sha,
    string ShortSha,
    string Message,
    string AuthorName,
    DateTime Date
);
