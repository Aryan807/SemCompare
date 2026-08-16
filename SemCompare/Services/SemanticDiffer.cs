using SemCompare.Models;

namespace SemCompare.Services;

public static class SemanticDiffer
{
    private const double RenameThreshold = 0.6;

    /// <summary>
    /// Full three-pass diff:
    ///   Pass 1 — exact name match (same class + method name)
    ///   Pass 2 — fuzzy rename detection (Jaccard on param types)
    ///   Pass 3 — move detection (same signature, different class)
    /// Also detects body-only changes (hash differs, signature same).
    /// </summary>
    public static List<DiffResult> Diff(
        List<MethodSignature> before,
        List<MethodSignature> after)
    {
        var results   = new List<DiffResult>();
        var unmatched = after.ToList();

        // Pass 1 & 2: within-class matching
        foreach (var b in before)
        {
            var exact = unmatched.FirstOrDefault(a =>
                a.ClassName  == b.ClassName &&
                a.MethodName == b.MethodName);

            if (exact != null)
            {
                unmatched.Remove(exact);
                var (kind, paramDiffs) = ClassifyMatch(b, exact);
                results.Add(new DiffResult(kind, b, exact, paramDiffs));
                continue;
            }

            // Fuzzy rename: same class, similar signature
            var best = unmatched
                .Select(a => (Method: a, Score: Similarity(b, a)))
                .Where(x => x.Score >= RenameThreshold)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (best.Method != null)
            {
                unmatched.Remove(best.Method);
                var paramDiffs = DiffParameters(b, best.Method);
                results.Add(new DiffResult(ChangeKind.Renamed, b, best.Method, paramDiffs));
                continue;
            }

            results.Add(new DiffResult(ChangeKind.Removed, b, null));
        }

        // Collect methods that appear removed so far (for move detection)
        var removed = results
            .Where(r => r.Kind == ChangeKind.Removed && r.Before != null)
            .Select(r => r.Before!)
            .ToList();

        // Pass 3: move detection across classes
        var moves = DetectMoves(removed, unmatched);
        foreach (var move in moves)
        {
            // Replace the Removed entry with a Moved entry
            var removedEntry = results.FindIndex(r =>
                r.Kind == ChangeKind.Removed &&
                r.Before?.MethodName == move.Before?.MethodName &&
                r.Before?.ClassName  == move.Before?.ClassName);

            if (removedEntry >= 0)
                results[removedEntry] = move;

            unmatched.Remove(move.After!);
        }

        // Remaining unmatched after entries = Added
        results.AddRange(unmatched.Select(a => new DiffResult(ChangeKind.Added, null, a)));
        return results;
    }

    /// <summary>
    /// Diffs two lists of field signatures using exact then fuzzy (Levenshtein) matching.
    /// </summary>
    public static List<FieldDiffResult> DiffFields(
        List<FieldSignature> before,
        List<FieldSignature> after)
    {
        var results   = new List<FieldDiffResult>();
        var unmatched = after.ToList();

        foreach (var b in before)
        {
            var exact = unmatched.FirstOrDefault(a =>
                a.ClassName == b.ClassName &&
                a.FieldName == b.FieldName);

            if (exact != null)
            {
                unmatched.Remove(exact);
                var kind = (b.FieldType == exact.FieldType, b.Initializer == exact.Initializer) switch
                {
                    (true,  true)  => ChangeKind.Unchanged,
                    (false, _)     => ChangeKind.TypeChanged,
                    (true,  false) => ChangeKind.InitializerChanged
                };
                results.Add(new FieldDiffResult(kind, b, exact));
                continue;
            }

            var best = unmatched
                .Where(a =>
                    a.ClassName == b.ClassName &&
                    a.FieldType == b.FieldType &&
                    a.IsPublic  == b.IsPublic)
                .Select(a => (Field: a, Distance: LevenshteinDistance(b.FieldName, a.FieldName)))
                .Where(x => x.Distance <= 4)
                .OrderBy(x => x.Distance)
                .FirstOrDefault();

            if (best.Field != null)
            {
                unmatched.Remove(best.Field);
                results.Add(new FieldDiffResult(ChangeKind.Renamed, b, best.Field));
                continue;
            }

            results.Add(new FieldDiffResult(ChangeKind.Removed, b, null));
        }

        results.AddRange(unmatched.Select(a => new FieldDiffResult(ChangeKind.Added, null, a)));
        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// For two matched methods, determines the change kind and computes param diffs.
    /// Priority: SignatureChanged > BodyModified > Unchanged
    /// </summary>
    private static (ChangeKind Kind, IReadOnlyList<ParamDiff>? ParamDiffs) ClassifyMatch(
        MethodSignature b, MethodSignature a)
    {
        if (!SignaturesEqual(b, a))
        {
            var paramDiffs = DiffParameters(b, a);
            return (ChangeKind.SignatureChanged, paramDiffs);
        }

        // Same signature — check if body logic changed
        if (!string.IsNullOrEmpty(b.BodyHash) &&
            !string.IsNullOrEmpty(a.BodyHash) &&
            b.BodyHash != a.BodyHash)
        {
            return (ChangeKind.BodyModified, null);
        }

        return (ChangeKind.Unchanged, null);
    }

    /// <summary>
    /// Produces a structured list of what changed between two parameter lists.
    /// Handles: type changes (positional), additions, and removals.
    /// </summary>
    private static List<ParamDiff> DiffParameters(MethodSignature before, MethodSignature after)
    {
        var diffs = new List<ParamDiff>();
        var bp    = before.Parameters.Select(ParseParam).ToList();
        var ap    = after.Parameters.Select(ParseParam).ToList();

        int maxLen = Math.Max(bp.Count, ap.Count);
        for (int i = 0; i < maxLen; i++)
        {
            if (i >= bp.Count)
            {
                diffs.Add(new ParamDiff("Added", ap[i].Name, null, ap[i].Type));
            }
            else if (i >= ap.Count)
            {
                diffs.Add(new ParamDiff("Removed", bp[i].Name, bp[i].Type, null));
            }
            else if (bp[i].Type != ap[i].Type)
            {
                diffs.Add(new ParamDiff("TypeChanged", ap[i].Name, bp[i].Type, ap[i].Type));
            }
            // Name-only changes are not tracked — param names are not part of the public contract
        }

        return diffs;
    }

    /// <summary>
    /// Pass 3: finds methods that moved to a different class.
    /// Match condition: identical name + full signature, different ClassName.
    /// If body hash also matches → confident move. If only signature → still flagged as Move.
    /// </summary>
    private static List<DiffResult> DetectMoves(
        List<MethodSignature> removed,
        List<MethodSignature> unmatched)
    {
        var moves = new List<DiffResult>();

        foreach (var r in removed)
        {
            var match = unmatched.FirstOrDefault(a =>
                a.ClassName      != r.ClassName &&
                a.MethodName     == r.MethodName &&
                a.FullSignature  == r.FullSignature);

            if (match != null)
            {
                moves.Add(new DiffResult(ChangeKind.Moved, r, match));
                unmatched.Remove(match);
            }
        }

        return moves;
    }

    private static double Similarity(MethodSignature a, MethodSignature b)
    {
        if (a.ClassName != b.ClassName) return 0;

        var aTypes = a.Parameters.Select(ParamType).ToHashSet();
        var bTypes = b.Parameters.Select(ParamType).ToHashSet();

        int total = aTypes.Union(bTypes).Count();
        double paramScore = total > 0
            ? aTypes.Intersect(bTypes).Count() / (double)total
            : 1.0;

        double returnScore = a.ReturnType == b.ReturnType ? 1.0 : 0.0;
        return (0.6 * paramScore) + (0.4 * returnScore);
    }

    private static bool SignaturesEqual(MethodSignature a, MethodSignature b) =>
        a.ReturnType == b.ReturnType && a.Parameters.SequenceEqual(b.Parameters);

    private static string ParamType(string param) => param.Split(' ').First();

    private static (string Type, string Name) ParseParam(string param)
    {
        var parts = param.Trim().Split(' ', 2);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (parts[0], "");
    }

    private static int LevenshteinDistance(string a, string b)
    {
        int[,] dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1]
                    : 1 + Math.Min(dp[i - 1, j - 1],
                          Math.Min(dp[i - 1, j], dp[i, j - 1]));

        return dp[a.Length, b.Length];
    }
}
