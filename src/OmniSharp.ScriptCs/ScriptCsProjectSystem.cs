using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using OmniSharp.Models.v1;
using OmniSharp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OmniSharp.ScriptCs
{
    [Export(typeof(IProjectSystem))]
    public class ScriptCsProjectSystem : IProjectSystem
    {
        public static readonly string[] DefaultNamespaces =
        {
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "System.Threading.Tasks",
            "System.IO",
            "System.Net.Http",
            "System.Dynamic"
        };

        private static readonly string BaseAssemblyPath = Path.GetDirectoryName(typeof(object).GetTypeInfo().Assembly.Location);
        private readonly OmnisharpWorkspace _workspace;
        private readonly IOmnisharpEnvironment _env;
        private readonly ScriptCsContext _scriptCsContext;
        private readonly ILogger _logger;

        [ImportingConstructor]
        public ScriptCsProjectSystem(OmnisharpWorkspace workspace, IOmnisharpEnvironment env, ILoggerFactory loggerFactory, ScriptCsContext scriptCsContext)
        {
            _workspace = workspace;
            _env = env;
            _scriptCsContext = scriptCsContext;
            _logger = loggerFactory.CreateLogger<ScriptCsProjectSystem>();
        }

        public string Key { get { return "ScriptCs"; } }
        public string Language { get { return LanguageNames.CSharp; } }
        public IEnumerable<string> Extensions { get; } = new[] { ".csx" };

        public void Initalize(IConfiguration configuration)
        {
            _logger.LogInformation($"Detecting CSX files in '{_env.Path}'.");

            var allCsxFiles = Directory.GetFiles(_env.Path, "*.csx", SearchOption.TopDirectoryOnly);

            if (allCsxFiles.Length == 0)
            {
                _logger.LogInformation("Could not find any CSX files");
                return;
            }

            _scriptCsContext.Path = _env.Path;
            _logger.LogInformation($"Found {allCsxFiles.Length} CSX files.");

            var mscorlib = MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location);
            var systemCore = MetadataReference.CreateFromFile(typeof(Enumerable).GetTypeInfo().Assembly.Location);

            var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp6, DocumentationMode.Parse, SourceCodeKind.Script);

            var fs = new FileSystem();
            var preProcessor = new FilePreProcessor(fs, new ILineProcessor[] { new UsingLineProcessor(), new LoadLineProcessor(fs), new ReferenceLineProcessor(fs) });

            foreach (var csxPath in allCsxFiles)
            {
                try
                {
                    _scriptCsContext.CsxFiles.Add(csxPath);
                    var processResult = preProcessor.ProcessFile(csxPath);

                    var references = new List<MetadataReference> { mscorlib, systemCore };
                    var usings = new List<string>(DefaultNamespaces);

                    //default references
                    //ImportReferences(fs, references, DefaultReferences);

                    //file usings
                    usings.AddRange(processResult.Namespaces);

                    //#r references
                    ImportReferences(fs, references, processResult.References);

                    //nuget references
                    //ImportReferences(references, assemblyPaths);

                    //script packs
                    /*if (scriptPacks != null && scriptPacks.Any())
                    {
                        var scriptPackSession = new ScriptPackSession(scriptPacks, new string[0]);
                        scriptPackSession.InitializePacks();

                        //script pack references
                        ImportReferences(references, scriptPackSession.References);

                        //script pack usings
                        usings.AddRange(scriptPackSession.Namespaces);

                        _scriptCsContext.ScriptPacks.UnionWith(scriptPackSession.Contexts.Select(pack => pack.GetType().ToString()));
                    }*/

                    _scriptCsContext.References.UnionWith(references.Select(x => x.Display));
                    _scriptCsContext.Usings.UnionWith(usings);

                    var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: usings.Distinct());

                    var fileName = Path.GetFileName(csxPath);

                    var projectId = ProjectId.CreateNewId(Guid.NewGuid().ToString());
                    var project = ProjectInfo.Create(projectId, VersionStamp.Create(), fileName, $"{fileName}.dll", LanguageNames.CSharp, null, null,
                                                            compilationOptions, parseOptions, null, null, references, null, null, true, hostObjectType: null);

                    _workspace.AddProject(project);
                    AddFile(csxPath, projectId);

                    foreach (var filePath in processResult.LoadedScripts.Distinct().Except(new[] { csxPath }))
                    {
                        _scriptCsContext.CsxFiles.Add(filePath);
                        var loadedFileName = Path.GetFileName(filePath);

                        var loadedFileProjectId = ProjectId.CreateNewId(Guid.NewGuid().ToString());
                        var loadedFileSubmissionProject = ProjectInfo.Create(loadedFileProjectId, VersionStamp.Create(),
                            $"{loadedFileName}-LoadedFrom-{fileName}", $"{loadedFileName}-LoadedFrom-{fileName}.dll", LanguageNames.CSharp, null, null,
                                            compilationOptions, parseOptions, null, null, references, null, null, true, hostObjectType: null);

                        _workspace.AddProject(loadedFileSubmissionProject);
                        AddFile(filePath, loadedFileProjectId);
                        _workspace.AddProjectReference(projectId, new ProjectReference(loadedFileProjectId));
                    }

                }
                catch (Exception ex)
                {
                    _logger.LogError($"{csxPath} will be ignored due to the following error:", ex);
                }
            }
        }

        private void ImportReferences(IFileSystem fs, List<MetadataReference> listOfReferences, IEnumerable<string> referencesToImport)
        {
            foreach (var importedReference in referencesToImport.Where(x => !x.ToLowerInvariant().Contains("scriptcs.contracts")))
            {
                if (fs.IsPathRooted(importedReference))
                {
                    if (fs.FileExists(importedReference))
                        listOfReferences.Add(MetadataReference.CreateFromFile(importedReference));
                }
                else
                {
                    listOfReferences.Add(MetadataReference.CreateFromFile(Path.Combine(BaseAssemblyPath, importedReference.ToLower().EndsWith(".dll") ? importedReference : importedReference + ".dll")));
                }
            }
        }

        private void AddFile(string filePath, ProjectId projectId)
        {
            using (var stream = File.OpenRead(filePath))
            using (var reader = new StreamReader(stream))
            {
                var fileName = Path.GetFileName(filePath);
                var csxFile = reader.ReadToEnd();

                var documentId = DocumentId.CreateNewId(projectId, fileName);
                var documentInfo = DocumentInfo.Create(documentId, fileName, null, SourceCodeKind.Script, null, filePath)
                    .WithSourceCodeKind(SourceCodeKind.Script)
                    .WithTextLoader(TextLoader.From(TextAndVersion.Create(SourceText.From(csxFile), VersionStamp.Create())));
                _workspace.AddDocument(documentInfo);
            }
        }

        Task<object> IProjectSystem.GetProjectModel(string path)
        {
            return Task.FromResult<object>(null);
        }

        Task<object> IProjectSystem.GetInformationModel(WorkspaceInformationRequest request)
        {
            return Task.FromResult<object>(_scriptCsContext);
        }
    }
}
