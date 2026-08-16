using Microsoft.AspNetCore.Authentication;
using SemCompare.Data;
using SemCompare.Models;

namespace SemCompare.Services;

/// <summary>
/// Reads the GitHub OAuth access token and user identity from the HTTP context.
/// </summary>
public class AuthStateService
{
    private readonly IHttpContextAccessor _http;
    private readonly DiffDbContext        _db;
    private readonly GitHubService        _github;

    public AuthStateService(IHttpContextAccessor http, DiffDbContext db, GitHubService github)
    {
        _http   = http;
        _db     = db;
        _github = github;
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        var ctx = _http.HttpContext;
        if (ctx == null) return null;
        return await ctx.GetTokenAsync("access_token");
    }

    public async Task<AppUser?> GetCurrentUserAsync()
    {
        var ctx = _http.HttpContext;
        if (ctx?.User?.Identity?.IsAuthenticated != true) return null;

        var claims   = ctx.User;
        var githubId = claims.FindFirst("urn:github:id")?.Value
                    ?? claims.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? "";
        var login    = claims.FindFirst("urn:github:login")?.Value
                    ?? claims.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                    ?? "";
        var name      = claims.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? login;
        var avatarUrl = claims.FindFirst("urn:github:avatar")?.Value ?? "";

        if (string.IsNullOrEmpty(githubId)) return null;
        return await _github.UpsertUserAsync(_db, githubId, login, name, avatarUrl);
    }

    public bool IsAuthenticated =>
        _http.HttpContext?.User?.Identity?.IsAuthenticated == true;
}
