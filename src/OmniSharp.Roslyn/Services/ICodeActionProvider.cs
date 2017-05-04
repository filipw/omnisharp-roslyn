using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;

namespace OmniSharp.Services
{
    public interface ICodeActionProvider
    {
        ImmutableArray<CodeRefactoringProvider> Refactorings { get; }
        ImmutableArray<CodeFixProvider> CodeFixes { get; }
        ImmutableArray<Assembly> Assemblies { get; }
        ImmutableArray<DiagnosticAnalyzer> Analyzers { get; }

    }
}
