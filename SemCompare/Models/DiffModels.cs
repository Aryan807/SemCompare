namespace SemCompare.Models;

public enum ChangeKind
{
    Unchanged,
    Added,
    Removed,
    Renamed,
    Moved,
    SignatureChanged,
    BodyModified,
    TypeChanged,
    InitializerChanged
}

public record ParamDiff(
    string Kind,
    string ParamName,
    string? OldType,
    string? NewType
);

public record MethodSignature(
    string ClassName,
    string MethodName,
    string ReturnType,
    IReadOnlyList<string> Parameters,
    bool IsPublic,
    string BodyHash = ""
)
{
    public string Display =>
        $"{(IsPublic ? "public" : "private")} {MethodName}({string.Join(", ", Parameters)}) : {ReturnType}";

    public string FullSignature =>
        $"{MethodName}({string.Join(", ", Parameters)}):{ReturnType}";
}

public record DiffResult(
    ChangeKind Kind,
    MethodSignature? Before,
    MethodSignature? After,
    IReadOnlyList<ParamDiff>? ParamDiffs = null
)
{
    public string ClassName  => Before?.ClassName  ?? After?.ClassName  ?? "";
    public string MethodName => Before?.MethodName ?? After?.MethodName ?? "";

    public bool IsBreaking =>
        (Before?.IsPublic == true) &&
        (Kind == ChangeKind.Removed ||
         Kind == ChangeKind.SignatureChanged ||
         Kind == ChangeKind.Moved);
}

public record FieldSignature(
    string ClassName,
    string FieldName,
    string FieldType,
    string? Initializer,
    bool IsPublic
)
{
    public string Display =>
        $"{(IsPublic ? "public" : "private")} {FieldType} {FieldName}" +
        (Initializer != null ? $" = {Initializer}" : "");
}

public record FieldDiffResult(
    ChangeKind Kind,
    FieldSignature? Before,
    FieldSignature? After
)
{
    public string ClassName => Before?.ClassName ?? After?.ClassName ?? "";
    public string FieldName => Before?.FieldName ?? After?.FieldName ?? "";

    public bool IsBreaking =>
        (Before?.IsPublic == true) &&
        (Kind == ChangeKind.Removed || Kind == ChangeKind.TypeChanged);
}

public record ChurnRow(string ClassName, string MethodName, int ChangeCount);
public record FieldChurnRow(string ClassName, string FieldName, int ChangeCount);

public record BreakingChange(
    string ClassName,
    string MemberName,
    string MemberKind,
    ChangeKind Kind,
    string? BeforeSig,
    string? AfterSig,
    IReadOnlyList<ParamDiff>? ParamDiffs = null
)
{
    public string? AiExplanation { get; init; }
}
