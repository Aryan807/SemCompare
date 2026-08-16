namespace SemCompare.Models;

public class AppUser
{
    public int      Id             { get; set; }
    public string   GitHubId       { get; set; } = "";   // GitHub numeric user ID
    public string   Login          { get; set; } = "";   // e.g. "octocat"
    public string   DisplayName    { get; set; } = "";
    public string   AvatarUrl      { get; set; } = "";
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt     { get; set; } = DateTime.UtcNow;
    public List<DiffRun> Runs      { get; set; } = new();
}

public class Repository
{
    public int      Id             { get; set; }
    public string   Path           { get; set; } = "";   // local clone path (temp dir on server)
    public string   GitHubUrl      { get; set; } = "";   // https://github.com/owner/repo
    public string   FullName       { get; set; } = "";   // "owner/repo"
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public List<DiffRun> Runs      { get; set; } = new();
}

public class DiffRun
{
    public int      Id             { get; set; }
    public int      RepositoryId   { get; set; }
    public int?     AppUserId      { get; set; }
    public string   CommitFrom     { get; set; } = "";
    public string   CommitTo       { get; set; } = "";
    public DateTime RanAt          { get; set; } = DateTime.UtcNow;
    public string?  AiSummary      { get; set; }

    public Repository         Repository   { get; set; } = null!;
    public AppUser?           AppUser      { get; set; }
    public List<MethodChange> Changes      { get; set; } = new();
    public List<FieldChange>  FieldChanges { get; set; } = new();
}

public class MethodChange
{
    public int     Id             { get; set; }
    public int     DiffRunId      { get; set; }
    public string  ClassName      { get; set; } = "";
    public string  MethodName     { get; set; } = "";
    public string  ChangeKind     { get; set; } = "";
    public string? BeforeSig      { get; set; }
    public string? AfterSig       { get; set; }
    public bool    IsBreaking     { get; set; }
    public string? AiExplanation  { get; set; }
    public DiffRun DiffRun        { get; set; } = null!;
}

public class FieldChange
{
    public int     Id             { get; set; }
    public int     DiffRunId      { get; set; }
    public string  ClassName      { get; set; } = "";
    public string  FieldName      { get; set; } = "";
    public string  ChangeKind     { get; set; } = "";
    public string? BeforeSig      { get; set; }
    public string? AfterSig       { get; set; }
    public bool    IsBreaking     { get; set; }
    public string? AiExplanation  { get; set; }
    public DiffRun DiffRun        { get; set; } = null!;
}
