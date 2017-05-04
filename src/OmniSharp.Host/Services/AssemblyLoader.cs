using System;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OmniSharp.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OmniSharp.Host.Loader
{
    internal class AssemblyLoader : IAssemblyLoader
    {
        private readonly ILogger _logger;

        public AssemblyLoader(ILoggerFactory loggerFactory)
        {
            this._logger = loggerFactory.CreateLogger<AssemblyLoader>();
        }

        public Assembly Load(AssemblyName name)
        {
            Assembly result;
            try
            {
                result = Assembly.Load(name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load assembly: {name}");
                throw;
            }

            _logger.LogTrace($"Assembly loaded: {name}");
            return result;
        }

        public IEnumerable<Assembly> LoadAllFromFolder(string folderPath)
        {
            if (folderPath == null) return Enumerable.Empty<Assembly>();

            var assemblies = new List<Assembly>();
            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.dll"))
            {
#if NET46
             assemblies.Add(Assembly.LoadFrom(filePath));
#else
                assemblies.Add(System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(filePath));
#endif
            }

            return assemblies;
        }
    }
}
