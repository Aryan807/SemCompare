using Microsoft.EntityFrameworkCore;
using SemCompare.Data;
using SemCompare.Models;

namespace SemCompare.Services;

public class DiffService
{
    private const int AiCallSpacingMs = 1200;

    private readonly GitService    _git;
    private readonly DiffDbContext _db;
    private readonly AiService     _ai;

    public DiffService(GitService git, DiffDbContext db, AiService ai)
    {
        _git = git;
        _db  = db;
        _ai  = ai;
    }

    /// <summary>
    /// Full pipeline for a GitHub repo. Clones/fetches the repo first using the
    /// user's OAuth token, then runs the semantic diff.
    /// </summary>
    public async Task<DiffRunResult> RunDiffFromGitHubAsync(
        string cloneUrl, string fullName,
        string fromRef, string toRef,
        string accessToken, int? appUserId = null)
    {
        var localPath = _git.EnsureLocalClone(cloneUrl, accessToken);
        return await RunDiffCoreAsync(localPath, cloneUrl, fullName, fromRef, toRef, appUserId);
    }

    /// <summary>
    /// Full pipeline. git → roslyn → diff → breaking changes → AI → persist.
    /// </summary>
    private async Task<DiffRunResult> RunDiffCoreAsync(
        string repoPath, string cloneUrl, string fullName,
        string fromRef, string toRef, int? appUserId)
    {
        var changedFiles = _git.GetChangedCSharpFiles(repoPath, fromRef, toRef);
        var allMethods   = new List<DiffResult>();
        var allFields    = new List<FieldDiffResult>();

        foreach (var file in changedFiles)
        {
            var beforeSrc = _git.ReadFileAtCommit(repoPath, fromRef, file) ?? "";
            var afterSrc  = _git.ReadFileAtCommit(repoPath, toRef,   file) ?? "";

            allMethods.AddRange(SemanticDiffer.Diff(
                RoslynExtractor.ExtractFrom(beforeSrc),
                RoslynExtractor.ExtractFrom(afterSrc)));

            allFields.AddRange(SemanticDiffer.DiffFields(
                RoslynExtractor.ExtractFieldsFrom(beforeSrc),
                RoslynExtractor.ExtractFieldsFrom(afterSrc)));
        }

        var breaking = GetBreakingChanges(allMethods, allFields);
        var summary  = await _ai.SummarizeRunAsync(allMethods, allFields);

        var explanationCache = new Dictionary<string, string?>();

        for (var i = 0; i < breaking.Count; i++)
        {
            if (i > 0)
                await Task.Delay(AiCallSpacingMs);

            var cacheKey = BuildExplanationKey(breaking[i]);
            if (!explanationCache.TryGetValue(cacheKey, out var explanation))
            {
                explanation = await _ai.ExplainBreakingChangeAsync(breaking[i]);
                explanationCache[cacheKey] = explanation;
            }

            breaking[i] = breaking[i] with { AiExplanation = explanation };
        }

        await SaveRunAsync(repoPath, cloneUrl, fullName, fromRef, toRef,
                           allMethods, allFields, breaking, summary, appUserId);

        return new DiffRunResult(allMethods, allFields, breaking, summary);
    }

    public List<BreakingChange> GetBreakingChanges(
        List<DiffResult>      methodResults,
        List<FieldDiffResult> fieldResults)
    {
        var breaking = new List<BreakingChange>();

        foreach (var r in methodResults.Where(r => r.IsBreaking))
            breaking.Add(new BreakingChange(
                r.ClassName, r.MethodName, "Method",
                r.Kind, r.Before?.Display, r.After?.Display, r.ParamDiffs));

        foreach (var f in fieldResults.Where(f => f.IsBreaking))
            breaking.Add(new BreakingChange(
                f.ClassName, f.FieldName, "Field",
                f.Kind, f.Before?.Display, f.After?.Display));

        return breaking;
    }

    private static string BuildExplanationKey(BreakingChange bc)
    {
        var paramKey = bc.ParamDiffs == null
            ? ""
            : string.Join("|", bc.ParamDiffs.Select(p => $"{p.Kind}:{p.ParamName}:{p.OldType}:{p.NewType}"));

        return $"{bc.MemberKind}:{bc.ClassName}:{bc.MemberName}:{bc.Kind}:{bc.BeforeSig}:{bc.AfterSig}:{paramKey}";
    }

    // ── History / dashboard ───────────────────────────────────────────────────

    public async Task<List<DiffRun>> GetAllRunsAsync(int? appUserId = null)
    {
        var q = _db.DiffRuns
            .Include(r => r.Repository)
            .Include(r => r.Changes)
            .Include(r => r.FieldChanges)
            .AsQueryable();

        if (appUserId.HasValue)
            q = q.Where(r => r.AppUserId == appUserId);

        return await q.OrderByDescending(r => r.RanAt).ToListAsync();
    }

    public async Task<List<ChurnRow>> GetTopChurnAsync(int? appUserId = null)
    {
        var q = _db.MethodChanges
            .Where(c => c.ChangeKind != nameof(ChangeKind.Unchanged));

        if (appUserId.HasValue)
            q = q.Where(c => c.DiffRun.AppUserId == appUserId);

        var raw = await q
            .GroupBy(c => new { c.ClassName, c.MethodName })
            .Select(g => new { g.Key.ClassName, g.Key.MethodName, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync();

        return raw.Select(x => new ChurnRow(x.ClassName, x.MethodName, x.Count)).ToList();
    }

    public async Task<List<FieldChurnRow>> GetTopFieldChurnAsync(int? appUserId = null)
    {
        var raw = await _db.FieldChanges
            .Where(f => f.ChangeKind != nameof(ChangeKind.Unchanged))
            .GroupBy(f => new { f.ClassName, f.FieldName })
            .Select(g => new { g.Key.ClassName, g.Key.FieldName, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync();

        return raw.Select(x => new FieldChurnRow(x.ClassName, x.FieldName, x.Count)).ToList();
    }

    public async Task<Dictionary<string, int>> GetChangeBreakdownAsync(int? appUserId = null)
    {
        var raw = await _db.MethodChanges
            .GroupBy(c => c.ChangeKind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync();

        return raw.ToDictionary(x => x.Kind, x => x.Count);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task SaveRunAsync(
        string repoPath, string cloneUrl, string fullName,
        string fromRef, string toRef,
        List<DiffResult>      methodResults,
        List<FieldDiffResult> fieldResults,
        List<BreakingChange>  breakingChanges,
        string?               aiSummary,
        int?                  appUserId)
    {
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.GitHubUrl == cloneUrl);
        if (repo == null)
        {
            repo = new Repository { Path = repoPath, GitHubUrl = cloneUrl, FullName = fullName };
            _db.Repositories.Add(repo);
        }
        else
        {
            repo.Path = repoPath; // update if clone path changed
        }

        var run = new DiffRun
        {
            Repository = repo,
            CommitFrom = fromRef,
            CommitTo   = toRef,
            RanAt      = DateTime.UtcNow,
            AiSummary  = aiSummary,
            AppUserId  = appUserId
        };
        _db.DiffRuns.Add(run);

        var explanations = breakingChanges
            .Where(b => b.AiExplanation != null)
            .ToDictionary(b => $"{b.ClassName}.{b.MemberName}", b => b.AiExplanation);

        foreach (var r in methodResults.Where(r => r.Kind != ChangeKind.Unchanged))
        {
            _db.MethodChanges.Add(new MethodChange
            {
                DiffRun       = run,
                ClassName     = r.ClassName,
                MethodName    = r.MethodName,
                ChangeKind    = r.Kind.ToString(),
                BeforeSig     = r.Before?.Display,
                AfterSig      = r.After?.Display,
                IsBreaking    = r.IsBreaking,
                AiExplanation = explanations.GetValueOrDefault($"{r.ClassName}.{r.MethodName}")
            });
        }

        foreach (var f in fieldResults.Where(f => f.Kind != ChangeKind.Unchanged))
        {
            _db.FieldChanges.Add(new FieldChange
            {
                DiffRun       = run,
                ClassName     = f.ClassName,
                FieldName     = f.FieldName,
                ChangeKind    = f.Kind.ToString(),
                BeforeSig     = f.Before?.Display,
                AfterSig      = f.After?.Display,
                IsBreaking    = f.IsBreaking,
                AiExplanation = explanations.GetValueOrDefault($"{f.ClassName}.{f.FieldName}")
            });
        }

        await _db.SaveChangesAsync();
    }
    /// <summary>Local path variant — for CLI / console usage.</summary>
    public async Task<DiffRunResult> RunDiffAsync(
        string repoPath, string fromRef, string toRef, int? appUserId = null)
    {
        var fullName = System.IO.Path.GetFileName(repoPath.TrimEnd('/', '\\'));
        return await RunDiffCoreAsync(repoPath, "", fullName, fromRef, toRef, appUserId);
    }

}

public record DiffRunResult(
    List<DiffResult>      Methods,
    List<FieldDiffResult> Fields,
    List<BreakingChange>  BreakingChanges,
    string?               AiSummary
);

