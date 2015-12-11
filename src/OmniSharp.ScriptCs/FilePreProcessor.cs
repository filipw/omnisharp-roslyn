using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OmniSharp.ScriptCs
{
    public class ReferenceLineProcessor : DirectiveLineProcessor
    {
        private readonly IFileSystem _fileSystem;

        public ReferenceLineProcessor(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        protected override string DirectiveName
        {
            get { return "r"; }
        }

        protected override BehaviorAfterCode BehaviorAfterCode
        {
            get { return BehaviorAfterCode.Throw; }
        }

        protected override bool ProcessLine(IFileParser parser, FileParserContext context, string line)
        {
            var argument = GetDirectiveArgument(line);
            var assemblyPath = Environment.ExpandEnvironmentVariables(argument);

            var referencePath = _fileSystem.GetFullPath(assemblyPath);
            var referencePathOrName = _fileSystem.FileExists(referencePath) ? referencePath : argument;

            if (!string.IsNullOrWhiteSpace(referencePathOrName) && !context.References.Contains(referencePathOrName))
            {
                context.References.Add(referencePathOrName);
            }

            return true;
        }
    }


    public interface ILineProcessor
    {
        bool ProcessLine(IFileParser parser, FileParserContext context, string line, bool isBeforeCode);
    }

    public interface IDirectiveLineProcessor : ILineProcessor
    {
        bool Matches(string line);
    }

    public class FilePreProcessorResult
    {
        public FilePreProcessorResult()
        {
            Namespaces = new List<string>();
            LoadedScripts = new List<string>();
            References = new List<string>();
        }

        public List<string> Namespaces { get; set; }

        public List<string> LoadedScripts { get; set; }

        public List<string> References { get; set; }

        public string Code { get; set; }
    }

    public class FileParserContext
    {
        public FileParserContext()
        {
            Namespaces = new List<string>();
            References = new List<string>();
            LoadedScripts = new List<string>();
            BodyLines = new List<string>();
        }

        public List<string> Namespaces { get; private set; }

        public List<string> References { get; private set; }

        public List<string> LoadedScripts { get; private set; }

        public List<string> BodyLines { get; private set; }
    }

    public interface IFileParser
    {
        void ParseFile(string path, FileParserContext context);

        void ParseScript(List<string> scriptLines, FileParserContext context);
    }

    public class UsingLineProcessor : ILineProcessor
    {
        private const string UsingString = "using ";

        public bool ProcessLine(IFileParser parser, FileParserContext context, string line, bool isBeforeCode)
        {
            if (!IsUsingLine(line))
            {
                return false;
            }

            var @namespace = GetNamespace(line);
            if (!context.Namespaces.Contains(@namespace))
            {
                context.Namespaces.Add(@namespace);
            }

            return true;
        }

        private static bool IsUsingLine(string line)
        {
            return line.Trim(' ').StartsWith(UsingString) && !line.Contains("{") && line.Contains(";") && !line.Contains("=");
        }

        private static string GetNamespace(string line)
        {
            return line.Trim(' ')
                .Replace(UsingString, string.Empty)
                .Replace("\"", string.Empty)
                .Replace(";", string.Empty);
        }
    }

    public class LoadLineProcessor : DirectiveLineProcessor
    {
        private readonly IFileSystem _fileSystem;

        public LoadLineProcessor(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        protected override string DirectiveName
        {
            get { return "load"; }
        }

        protected override BehaviorAfterCode BehaviorAfterCode
        {
            get { return BehaviorAfterCode.Throw; }
        }

        protected override bool ProcessLine(IFileParser parser, FileParserContext context, string line)
        {
            var argument = GetDirectiveArgument(line);
            var filePath = Environment.ExpandEnvironmentVariables(argument);

            var fullPath = _fileSystem.GetFullPath(filePath);
            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                parser.ParseFile(fullPath, context);
            }

            return true;
        }
    }

    public enum BehaviorAfterCode
    {
        Allow,
        Ignore,
        Throw
    }

    public abstract class DirectiveLineProcessor : IDirectiveLineProcessor
    {
        protected virtual BehaviorAfterCode BehaviorAfterCode
        {
            get { return BehaviorAfterCode.Ignore; }
        }

        protected abstract string DirectiveName { get; }

        private string DirectiveString
        {
            get { return string.Format("#{0}", DirectiveName); }
        }

        public bool ProcessLine(IFileParser parser, FileParserContext context, string line, bool isBeforeCode)
        {
            if (!Matches(line))
            {
                return false;
            }

            if (!isBeforeCode)
            {
                if (BehaviorAfterCode == Contracts.BehaviorAfterCode.Throw)
                {
                    throw new InvalidDirectiveUseException(string.Format("Encountered directive '{0}' after the start of code. Please move this directive to the beginning of the file.", DirectiveString));
                }
                else if (BehaviorAfterCode == Contracts.BehaviorAfterCode.Ignore)
                {
                    return true;
                }
            }

            return ProcessLine(parser, context, line);
        }

        protected string GetDirectiveArgument(string line)
        {
            return line.Replace(DirectiveString, string.Empty)
                .Trim()
                .Replace("\"", string.Empty)
                .Replace(";", string.Empty);
        }

        protected abstract bool ProcessLine(IFileParser parser, FileParserContext context, string line);

        public bool Matches(string line)
        {
            var tokens = line.Split();
            return tokens[0] == DirectiveString;
        }
    }

    public class InvalidDirectiveUseException : Exception
    {
        public InvalidDirectiveUseException(string message)
            : base(message)
        {
        }

        public InvalidDirectiveUseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public interface IFilePreProcessor : IFileParser
    {
        FilePreProcessorResult ProcessFile(string path);

        FilePreProcessorResult ProcessScript(string script);
    }

    public class FilePreProcessor : IFilePreProcessor
    {
        private readonly IEnumerable<ILineProcessor> _lineProcessors;

        private readonly IFileSystem _fileSystem;

        public FilePreProcessor(IFileSystem fileSystem, IEnumerable<ILineProcessor> lineProcessors)
        {
            _fileSystem = fileSystem;
            _lineProcessors = lineProcessors;
        }

        public virtual FilePreProcessorResult ProcessFile(string path)
        {
            return Process(context => ParseFile(path, context));
        }

        public virtual FilePreProcessorResult ProcessScript(string script)
        {
            var scriptLines = _fileSystem.SplitLines(script).ToList();
            return Process(context => ParseScript(scriptLines, context));
        }

        protected virtual FilePreProcessorResult Process(Action<FileParserContext> parseAction)
        {
            var context = new FileParserContext();
            parseAction(context);

            var code = GenerateCode(context);

            return new FilePreProcessorResult
            {
                Namespaces = context.Namespaces,
                LoadedScripts = context.LoadedScripts,
                References = context.References,
                Code = code
            };
        }

        protected virtual string GenerateCode(FileParserContext context)
        {
            return string.Join(_fileSystem.NewLine, context.BodyLines);
        }

        public virtual void ParseFile(string path, FileParserContext context)
        {
            var fullPath = _fileSystem.GetFullPath(path);
            var filename = Path.GetFileName(path);

            if (context.LoadedScripts.Contains(fullPath))
            {
                return;
            }

            // Add script to loaded collection before parsing to avoid loop.
            context.LoadedScripts.Add(fullPath);

            var scriptLines = _fileSystem.ReadFileLines(fullPath).ToList();

            InsertLineDirective(fullPath, scriptLines);
            InDirectory(fullPath, () => ParseScript(scriptLines, context));
        }

        public virtual void ParseScript(List<string> scriptLines, FileParserContext context)
        {
            var codeIndex = scriptLines.FindIndex(IsNonDirectiveLine);

            for (var index = 0; index < scriptLines.Count; index++)
            {
                var line = scriptLines[index];
                var isBeforeCode = index < codeIndex || codeIndex < 0;

                var wasProcessed = _lineProcessors.Any(x => x.ProcessLine(this, context, line, isBeforeCode));

                if (!wasProcessed)
                {
                    context.BodyLines.Add(line);
                }
            }
        }

        protected virtual void InsertLineDirective(string path, List<string> fileLines)
        {
            var bodyIndex = fileLines.FindIndex(line => IsNonDirectiveLine(line) && !IsUsingLine(line));
            if (bodyIndex == -1)
            {
                return;
            }

            var directiveLine = string.Format("#line {0} \"{1}\"", bodyIndex + 1, path);
            fileLines.Insert(bodyIndex, directiveLine);
        }

        private void InDirectory(string path, Action action)
        {
            var oldCurrentDirectory = _fileSystem.CurrentDirectory;
            _fileSystem.CurrentDirectory = _fileSystem.GetWorkingDirectory(path);

            action();

            _fileSystem.CurrentDirectory = oldCurrentDirectory;
        }

        private bool IsNonDirectiveLine(string line)
        {
            var directiveLineProcessors =
                _lineProcessors.OfType<IDirectiveLineProcessor>();

            return line.Trim() != string.Empty && !directiveLineProcessors.Any(lp => lp.Matches(line));
        }

        private static bool IsUsingLine(string line)
        {
            return line.TrimStart(' ').StartsWith("using ") && !line.Contains("{") && line.Contains(";");
        }
    }

    public interface IFileSystem
    {
        IEnumerable<string> EnumerateFiles(string dir, string search);

        IEnumerable<string> EnumerateDirectories(string dir, string searchPattern);

        IEnumerable<string> EnumerateFilesAndDirectories(string dir, string searchPattern);

        void Copy(string source, string dest, bool overwrite);

        void CopyDirectory(string source, string dest, bool overwrite);

        bool DirectoryExists(string path);

        void CreateDirectory(string path, bool hidden = false);

        void DeleteDirectory(string path);

        string ReadFile(string path);

        string[] ReadFileLines(string path);

        DateTime GetLastWriteTime(string file);

        bool IsPathRooted(string path);

        string GetFullPath(string path);

        string CurrentDirectory { get; set; }

        string NewLine { get; }

        string GetWorkingDirectory(string path);

        void Move(string source, string dest);

        void MoveDirectory(string source, string dest);

        bool FileExists(string path);

        void FileDelete(string path);

        IEnumerable<string> SplitLines(string value);

        void WriteToFile(string path, string text);

        //Stream CreateFileStream(string filePath, FileMode mode);

        void WriteAllBytes(string filePath, byte[] bytes);

        //string GlobalFolder { get; }

        //string HostBin { get; }

        string BinFolder { get; }

        string DllCacheFolder { get; }

        string PackagesFile { get; }

        string PackagesFolder { get; }

        string NugetFile { get; }

        //string GlobalOptsFile { get; }
    }

    public class FileSystem : IFileSystem
    {
        public virtual IEnumerable<string> EnumerateFiles(string dir, string searchPattern)
        {
            return Directory.EnumerateFiles(dir, searchPattern);
        }

        public virtual IEnumerable<string> EnumerateDirectories(string dir, string searchPattern)
        {
            return Directory.EnumerateDirectories(dir, searchPattern);
        }

        public virtual IEnumerable<string> EnumerateFilesAndDirectories(string dir, string searchPattern)
        {
            return Directory.EnumerateFileSystemEntries(dir, searchPattern);
        }

        public virtual void Copy(string source, string dest, bool overwrite)
        {
            File.Copy(source, dest, overwrite);
        }

        public virtual void CopyDirectory(string source, string dest, bool overwrite)
        {
            if (!Directory.Exists(dest))
            {
                Directory.CreateDirectory(dest);
            }

            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite);
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(dest, Path.GetFileName(directory)), overwrite);
            }
        }

        public virtual bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        public virtual void CreateDirectory(string path, bool hidden)
        {
            var directory = Directory.CreateDirectory(path);

            if (hidden)
            {
                directory.Attributes = FileAttributes.Directory | FileAttributes.Hidden;
            }
        }

        public virtual void DeleteDirectory(string path)
        {
            Directory.Delete(path, true);
        }

        public virtual string ReadFile(string path)
        {
            return File.ReadAllText(path);
        }

        public virtual string[] ReadFileLines(string path)
        {
            return File.ReadAllLines(path);
        }

        public virtual bool IsPathRooted(string path)
        {
            return Path.IsPathRooted(path);
        }

        public virtual string CurrentDirectory
        {
            get { return Directory.GetCurrentDirectory(); }
            set { Directory.SetCurrentDirectory(value); }
        }

        public virtual string NewLine
        {
            get { return Environment.NewLine; }
        }

        public virtual DateTime GetLastWriteTime(string file)
        {
            return File.GetLastWriteTime(file);
        }

        public virtual void Move(string source, string dest)
        {
            File.Move(source, dest);
        }

        public virtual void MoveDirectory(string source, string dest)
        {
            Directory.Move(source, dest);
        }

        public virtual bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public virtual void FileDelete(string path)
        {
            File.Delete(path);
        }

        public virtual IEnumerable<string> SplitLines(string value)
        {
            return value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        public virtual void WriteToFile(string path, string text)
        {
            File.WriteAllText(path, text);
        }

        public virtual Stream CreateFileStream(string filePath, FileMode mode)
        {
            return new FileStream(filePath, mode);
        }

        public virtual void WriteAllBytes(string filePath, byte[] bytes)
        {
            File.WriteAllBytes(filePath, bytes);
        }

        //public virtual string GlobalFolder
        //{
        //    get
        //    {
        //        return Path.Combine(
        //            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "scriptcs");
        //    }
        //}

        public virtual string GetWorkingDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return CurrentDirectory;
            }

            var realPath = GetFullPath(path);

            if (FileExists(realPath) || DirectoryExists(realPath))
            {
                if ((File.GetAttributes(realPath) & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    return realPath;
                }

                return Path.GetDirectoryName(realPath);
            }

            return Path.GetDirectoryName(realPath);
        }

        public virtual string GetFullPath(string path)
        {
            return Path.GetFullPath(path);
        }

        //public virtual string HostBin
        //{
        //    get { return AppDomain.CurrentDomain.BaseDirectory; }
        //}

        public virtual string BinFolder
        {
            get { return "scriptcs_bin"; }
        }

        public virtual string DllCacheFolder
        {
            get { return ".scriptcs_cache"; }
        }

        public virtual string PackagesFile
        {
            get { return "scriptcs_packages.config"; }
        }

        public virtual string PackagesFolder
        {
            get { return "scriptcs_packages"; }
        }

        public virtual string NugetFile
        {
            get { return "scriptcs_nuget.config"; }
        }

        //public virtual string GlobalOptsFile
        //{
        //    get { return Path.Combine(GlobalFolder, Constants.ConfigFilename); }
        //}
    }
}
