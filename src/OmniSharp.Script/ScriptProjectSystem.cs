using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dotnet.Script.DependencyModel.Compilation;
using Dotnet.Script.DependencyModel.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;
using OmniSharp.Models.WorkspaceInformation;
using OmniSharp.Services;
using OmniSharp.Roslyn.Utilities;
using LogLevel = Dotnet.Script.DependencyModel.Logging.LogLevel;

namespace OmniSharp.Script
{
    [Export(typeof(IProjectSystem)), Shared]
    public class ScriptProjectSystem : IProjectSystem
    {
        private const string CsxExtension = ".csx";

        // used for tracking purposes only
        private readonly HashSet<string> _assemblyReferences = new HashSet<string>();
        private readonly Dictionary<string, ProjectInfo> _projects;
        private readonly OmniSharpWorkspace _workspace;
        private readonly IOmniSharpEnvironment _env;
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly CompilationDependencyResolver _compilationDependencyResolver;
        private readonly MetadataFileReferenceCache _metadataFileReferenceCache;

        [ImportingConstructor]
        public ScriptProjectSystem(OmniSharpWorkspace workspace, IOmniSharpEnvironment env, ILoggerFactory loggerFactory, 
            MetadataFileReferenceCache metadataFileReferenceCache)
        {
            _metadataFileReferenceCache = metadataFileReferenceCache;
            _workspace = workspace;
            _env = env;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ScriptProjectSystem>();
            _projects = new Dictionary<string, ProjectInfo>();

            _compilationDependencyResolver = new CompilationDependencyResolver(type =>
            {
                // Prefix with "OmniSharp" so that we make it through the log filter.
                var categoryName = $"OmniSharp.Script.{type.FullName}";
                var dependencyResolverLogger = loggerFactory.CreateLogger(categoryName);
                return ((level, message) =>
                {
                    if (level == LogLevel.Debug)
                    {
                        dependencyResolverLogger.LogDebug(message);
                    }
                    if (level == LogLevel.Info)
                    {
                        dependencyResolverLogger.LogInformation(message);
                    }
                });
            });
        }

        public string Key => "Script";
        public string Language => LanguageNames.CSharp;
        public IEnumerable<string> Extensions { get; } = new[] { CsxExtension };

        public void Initalize(IConfiguration configuration)
        {
            var scriptOptions = new ScriptOptions();
            ConfigurationBinder.Bind(configuration, scriptOptions);

            var scriptHelper = new ScriptHelper(scriptOptions, _env, _loggerFactory);
            _logger.LogInformation($"Detecting CSX files in '{_env.TargetDirectory}'.");

            // Nothing to do if there are no CSX files
            var allCsxFiles = Directory.GetFiles(_env.TargetDirectory, "*.csx", SearchOption.AllDirectories);
            if (allCsxFiles.Length == 0)
            {
                _logger.LogInformation("Could not find any CSX files");
                return;
            }

            _logger.LogInformation($"Found {allCsxFiles.Length} CSX files.");

            // explicitly inherit scripting library references to all global script object (CommandLineScriptGlobals) to be recognized
            var inheritedCompileLibraries = DependencyContext.Default.CompileLibraries.Where(x =>
                x.Name.ToLowerInvariant().StartsWith("microsoft.codeanalysis")).ToList();

            // explicitly include System.ValueTuple
            inheritedCompileLibraries.AddRange(DependencyContext.Default.CompileLibraries.Where(x =>
                x.Name.ToLowerInvariant().StartsWith("system.valuetuple")));

            var commonReferences = new HashSet<MetadataReference>(MetadataReferenceEqualityComparer.Instance);
            var compilationDependencies = TryGetCompilationDependencies(scriptOptions.EnableScriptNuGetReferences);

            // if we have no compilation dependencies
            // we will assume desktop framework
            // and add default CLR references
            // same applies for having a context that is not a .NET Core app
            if (!compilationDependencies.Any())
            {
                _logger.LogInformation("Unable to find dependency context for CSX files. Will default to non-context usage (Desktop CLR scripts).");
                AddDefaultClrMetadataReferences(commonReferences);
            }
            else
            {
                HashSet<string> loadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var compilationAssembly in compilationDependencies.SelectMany(cd => cd.AssemblyPaths).Distinct())
                {
                    if (loadedFiles.Add(Path.GetFileName(compilationAssembly)))
                    {
                        _logger.LogDebug("Discovered script compilation assembly reference: " + compilationAssembly);
                        AddMetadataReference(commonReferences, compilationAssembly);
                    }
                }
            }

            // inject all inherited assemblies
            foreach (var inheritedCompileLib in inheritedCompileLibraries.SelectMany(x => x.ResolveReferencePaths()))
            {
                _logger.LogDebug("Adding implicit reference: " + inheritedCompileLib);
                AddMetadataReference(commonReferences, inheritedCompileLib);
            }

            // Each .CSX file becomes an entry point for it's own project
            // Every #loaded file will be part of the project too
            foreach (var csxPath in allCsxFiles)
            {
                try
                {
                    var csxFileName = Path.GetFileName(csxPath);
                    var project = scriptHelper.CreateProject(csxFileName, commonReferences);

                    if (scriptOptions.EnableScriptNuGetReferences)
                    {
                        var scriptMap = compilationDependencies.ToDictionary(rdt => rdt.Name, rdt => rdt.Scripts);
                        var options = project.CompilationOptions.WithSourceReferenceResolver(
                            new NuGetSourceReferenceResolver(ScriptSourceResolver.Default,
                                scriptMap));
                        project = project.WithCompilationOptions(options);
                    }

                    // add CSX project to workspace
                    _workspace.AddProject(project);
                    _workspace.AddDocument(project.Id, csxPath, SourceCodeKind.Script);
                    _projects[csxPath] = project;
                    _logger.LogInformation($"Added CSX project '{csxPath}' to the workspace.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{csxPath} will be ignored due to an following error");
                }
            }
        }

        private CompilationDependency[] TryGetCompilationDependencies(bool enableScriptNuGetReferences)
        {
            try
            {
                return _compilationDependencyResolver.GetDependencies(_env.TargetDirectory, enableScriptNuGetReferences).ToArray();
            }
            catch (Exception e)
            {
                _logger.LogError("Failed to resolve compilation dependencies", e);
                return Array.Empty<CompilationDependency>();
            }
        }

        private void AddDefaultClrMetadataReferences(HashSet<MetadataReference> commonReferences)
        {
            var assemblies = new[]
            {
                typeof(object).GetTypeInfo().Assembly,
                typeof(Enumerable).GetTypeInfo().Assembly,
                typeof(Stack<>).GetTypeInfo().Assembly,
                typeof(Lazy<,>).GetTypeInfo().Assembly,
                FromName("System.Runtime"),
                FromName("mscorlib")
            };

            var references = assemblies
                .Where(a => a != null)
                .Select(a => a.Location)
                .Distinct()
                .Select(l => _metadataFileReferenceCache.GetMetadataReference(l));

            foreach (var reference in references)
            {
                commonReferences.Add(reference);
            }

            Assembly FromName(string assemblyName)
            {
                try
                {
                    return Assembly.Load(new AssemblyName(assemblyName));
                }
                catch
                {
                    return null;
                }
            }
        }

        private void AddMetadataReference(ISet<MetadataReference> referenceCollection, string fileReference)
        {
            if (!File.Exists(fileReference))
            {
                _logger.LogWarning($"Couldn't add reference to '{fileReference}' because the file was not found.");
                return;
            }

            var metadataReference = _metadataFileReferenceCache.GetMetadataReference(fileReference);
            if (metadataReference == null)
            {
                _logger.LogWarning($"Couldn't add reference to '{fileReference}' because the loaded metadata reference was null.");
                return;
            }

            referenceCollection.Add(metadataReference);
            _assemblyReferences.Add(fileReference);
            _logger.LogDebug($"Added reference to '{fileReference}'");
        }

        private ProjectInfo GetProjectFileInfo(string path)
        {
            if (!_projects.TryGetValue(path, out ProjectInfo projectFileInfo))
            {
                return null;
            }

            return projectFileInfo;
        }

        Task<object> IProjectSystem.GetProjectModelAsync(string filePath)
        {
            // only react to .CSX file paths
            if (!filePath.EndsWith(CsxExtension, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<object>(null);
            }

            var document = _workspace.GetDocument(filePath);
            var projectFilePath = document != null
                ? document.Project.FilePath
                : filePath;

            var projectInfo = GetProjectFileInfo(projectFilePath);
            if (projectInfo == null)
            {
                _logger.LogDebug($"Could not locate project for '{projectFilePath}'");
                return Task.FromResult<object>(null);
            }

            return Task.FromResult<object>(new ScriptContextModel(filePath, projectInfo, _assemblyReferences));
        }

        Task<object> IProjectSystem.GetWorkspaceModelAsync(WorkspaceInformationRequest request)
        {
            var scriptContextModels = new List<ScriptContextModel>();
            foreach (var project in _projects)
            {
                scriptContextModels.Add(new ScriptContextModel(project.Key, project.Value, _assemblyReferences));
            }
            return Task.FromResult<object>(new ScriptContextModelCollection(scriptContextModels));
        }
    }
}
