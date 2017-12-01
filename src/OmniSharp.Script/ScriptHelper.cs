﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dotnet.Script.DependencyModel.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using OmniSharp.Helpers;
using System.IO;
using System.Linq;
using OmniSharp.Roslyn.Utilities;

namespace OmniSharp.Script
{
    public class ScriptHelper
    {
        private const string BinderFlagsType = "Microsoft.CodeAnalysis.CSharp.BinderFlags";
        private const string TopLevelBinderFlagsProperty = "TopLevelBinderFlags";
        private const string IgnoreCorLibraryDuplicatedTypesField = "IgnoreCorLibraryDuplicatedTypes";
        private const string RuntimeMetadataReferenceResolverType = "Microsoft.CodeAnalysis.Scripting.Hosting.RuntimeMetadataReferenceResolver";
        private const string ResolverField = "_resolver";
        private const string FileReferenceProviderField = "_fileReferenceProvider";

        private readonly IConfiguration _configuration;

        // aligned with CSI.exe
        // https://github.com/dotnet/roslyn/blob/version-2.0.0-rc3/src/Interactive/csi/csi.rsp
        internal static readonly IEnumerable<string> DefaultNamespaces = new[]
        {
            "System",
            "System.IO",
            "System.Collections.Generic",
            "System.Console",
            "System.Diagnostics",
            "System.Dynamic",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Text",
            "System.Threading.Tasks"
        };

        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Script);

        private readonly Lazy<CSharpCompilationOptions> _compilationOptions;
        private readonly Lazy<CSharpCommandLineArguments> _commandLineArgs;
        private readonly ScriptOptions _scriptOptions;
        private readonly IOmniSharpEnvironment _env;

        public ScriptHelper(ScriptOptions scriptOptions, IOmniSharpEnvironment env)
        {
            _scriptOptions = scriptOptions ?? throw new ArgumentNullException(nameof(scriptOptions));
            _env = env ?? throw new ArgumentNullException(nameof(env));

            _compilationOptions = new Lazy<CSharpCompilationOptions>(CreateCompilationOptions);
            _commandLineArgs = new Lazy<CSharpCommandLineArguments>(CreateCommandLineArguments);
            InjectXMLDocumentationProviderIntoRuntimeMetadataReferenceResolver();
        }

        private CSharpCommandLineArguments CreateCommandLineArguments()
        {
            var scriptRunnerParserProperty = typeof(CSharpCommandLineParser).GetProperty("ScriptRunner", BindingFlags.Static | BindingFlags.NonPublic);
            var scriptRunnerParser = scriptRunnerParserProperty?.GetValue(null) as CSharpCommandLineParser;

            if (scriptRunnerParser != null && !string.IsNullOrWhiteSpace(_scriptOptions.RspFilePath))
            {
                var rspFilePath = _scriptOptions.GetNormalizedRspFilePath(_env);
                if (rspFilePath != null)
                {
                    return scriptRunnerParser.Parse(new string[] { $"@{rspFilePath}" }, _env.TargetDirectory, _env.TargetDirectory);
                }
            }

            return null;
        }

        private CSharpCompilationOptions CreateCompilationOptions()
        {
            // if use specified usings via .rsp file, use those - otherwise fallback to default hardcoded set based on CSI
            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                usings: _commandLineArgs.Value.CompilationOptions != null && _commandLineArgs.Value.CompilationOptions.Usings.Any()
                        ? _commandLineArgs.Value.CompilationOptions.Usings
                        : DefaultNamespaces,
                allowUnsafe: true,
                metadataReferenceResolver: CreateMetadataReferenceResolver(),
                sourceReferenceResolver: ScriptSourceResolver.Default,
                assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default).
                    WithSpecificDiagnosticOptions(CompilationOptionsHelper.GetDefaultSuppressedDiagnosticOptions());

            var topLevelBinderFlagsProperty = typeof(CSharpCompilationOptions).GetProperty(TopLevelBinderFlagsProperty, BindingFlags.Instance | BindingFlags.NonPublic);
            var binderFlagsType = typeof(CSharpCompilationOptions).GetTypeInfo().Assembly.GetType(BinderFlagsType);

            var ignoreCorLibraryDuplicatedTypesMember = binderFlagsType?.GetField(IgnoreCorLibraryDuplicatedTypesField, BindingFlags.Static | BindingFlags.Public);
            var ignoreCorLibraryDuplicatedTypesValue = ignoreCorLibraryDuplicatedTypesMember?.GetValue(null);
            if (ignoreCorLibraryDuplicatedTypesValue != null)
            {
                topLevelBinderFlagsProperty?.SetValue(compilationOptions, ignoreCorLibraryDuplicatedTypesValue);
            }

            return compilationOptions;
        }

        private CachingScriptMetadataResolver CreateMetadataReferenceResolver()
        {
            return _scriptOptions.EnableScriptNuGetReferences
                ? new CachingScriptMetadataResolver(new NuGetMetadataReferenceResolver(ScriptMetadataResolver.Default.WithBaseDirectory(_env.TargetDirectory))) 
                : new CachingScriptMetadataResolver(ScriptMetadataResolver.Default.WithBaseDirectory(_env.TargetDirectory));
        }
 
        public ProjectInfo CreateProject(string csxFileName, IEnumerable<MetadataReference> references, IEnumerable<string> namespaces = null)
        {
            var project = ProjectInfo.Create(
                id: ProjectId.CreateNewId(),
                version: VersionStamp.Create(),
                name: csxFileName,
                assemblyName: $"{csxFileName}.dll",
                language: LanguageNames.CSharp,
                compilationOptions: namespaces == null
                    ? _compilationOptions.Value
                    : _compilationOptions.Value.WithUsings(namespaces),
                metadataReferences: _commandLineArgs.Value.MetadataReferences.Any()
                    ? _commandLineArgs.Value.ResolveMetadataReferences(_compilationOptions.Value.MetadataReferenceResolver).Union(references, MetadataReferenceEqualityComparer.Instance)
                    : references,
                parseOptions: ParseOptions,
                isSubmission: true,
                hostObjectType: typeof(CommandLineScriptGlobals));

            return project;
        }

        private void InjectXMLDocumentationProviderIntoRuntimeMetadataReferenceResolver()
        {
            var runtimeMetadataReferenceResolverField = typeof(ScriptMetadataResolver).GetField(ResolverField, BindingFlags.Instance | BindingFlags.NonPublic);
            var runtimeMetadataReferenceResolverValue = runtimeMetadataReferenceResolverField?.GetValue(_resolver);

            if (runtimeMetadataReferenceResolverValue != null)
            {
                var runtimeMetadataReferenceResolverType = typeof(CommandLineScriptGlobals).GetTypeInfo().Assembly.GetType(RuntimeMetadataReferenceResolverType);
                var fileReferenceProviderField = runtimeMetadataReferenceResolverType?.GetField(FileReferenceProviderField, BindingFlags.Instance | BindingFlags.NonPublic);
                fileReferenceProviderField.SetValue(runtimeMetadataReferenceResolverValue, new Func<string, MetadataReferenceProperties, PortableExecutableReference>((path, properties) =>
                {
                    var documentationFile = Path.ChangeExtension(path, ".xml");
                    var documentationProvider = File.Exists(documentationFile)
                        ? XmlDocumentationProvider.CreateFromFile(documentationFile)
                        : null;

                    return MetadataReference.CreateFromFile(path, properties, documentationProvider);
                }));
            }
        }
    }
}
