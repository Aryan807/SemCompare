using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SemCompare.Models;

namespace SemCompare.Services;

public static class RoslynExtractor
{
    /// <summary>
    /// Extracts all method signatures from C# source code.
    /// Also computes a body hash so we can detect logic changes without signature changes.
    /// </summary>
    public static List<MethodSignature> ExtractFrom(string sourceCode)
    {
        var tree    = CSharpSyntaxTree.ParseText(sourceCode);
        var root    = tree.GetCompilationUnitRoot();
        var results = new List<MethodSignature>();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var className = classDecl.Identifier.Text;

            foreach (var method in classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var parameters = method.ParameterList.Parameters
                    .Select(p => $"{p.Type} {p.Identifier}".Trim())
                    .ToList();

                var isPublic = method.Modifiers.Any(m => m.Text == "public");
                var bodyHash = ComputeBodyHash(method);

                results.Add(new MethodSignature(
                    ClassName:  className,
                    MethodName: method.Identifier.Text,
                    ReturnType: method.ReturnType.ToString().Trim(),
                    Parameters: parameters,
                    IsPublic:   isPublic,
                    BodyHash:   bodyHash
                ));
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts all public and private field declarations from C# source code.
    /// </summary>
    public static List<FieldSignature> ExtractFieldsFrom(string sourceCode)
    {
        var tree    = CSharpSyntaxTree.ParseText(sourceCode);
        var root    = tree.GetCompilationUnitRoot();
        var results = new List<FieldSignature>();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var className = classDecl.Identifier.Text;

            foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                var isPublic  = field.Modifiers.Any(m => m.Text == "public");
                var isPrivate = field.Modifiers.Any(m => m.Text == "private");
                if (!isPublic && !isPrivate) continue;

                var typeName = field.Declaration.Type.ToString().Trim();

                foreach (var declarator in field.Declaration.Variables)
                {
                    var initializer = declarator.Initializer?.Value.ToString().Trim();

                    results.Add(new FieldSignature(
                        ClassName:   className,
                        FieldName:   declarator.Identifier.Text,
                        FieldType:   typeName,
                        Initializer: initializer,
                        IsPublic:    isPublic
                    ));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Computes an MD5 hash of the method body after normalising whitespace.
    /// Formatting changes (tabs vs spaces, blank lines) do NOT change the hash.
    /// </summary>
    private static string ComputeBodyHash(MethodDeclarationSyntax method)
    {
        var bodyText = method.Body?.ToFullString()
                    ?? method.ExpressionBody?.ToFullString()
                    ?? "";

        bodyText = RemoveWhitespace(bodyText);

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(bodyText));
        return Convert.ToHexString(bytes);
    }

    private static string RemoveWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }
}
