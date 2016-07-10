using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using OmniSharp.Services;

namespace OmniSharp.Roslyn.CSharp.Services
{
    [Export(typeof(IHostServicesProvider))]
    [Export(typeof(RoslynFeaturesHostServicesProvider))]
    public class RoslynFeaturesHostServicesProvider : IHostServicesProvider
    {
        public ImmutableArray<Assembly> Assemblies { get; }

        [ImportingConstructor]
        public RoslynFeaturesHostServicesProvider(IOmnisharpAssemblyLoader loader)
        {
            var builder = ImmutableArray.CreateBuilder<Assembly>();

            var Features = Configuration.GetRoslynAssemblyFullName("Microsoft.CodeAnalysis.Features");
            var CSharpFeatures = Configuration.GetRoslynAssemblyFullName("Microsoft.CodeAnalysis.CSharp.Features");
            var extensionPaths = Directory.EnumerateFiles("C:\\omnisharp-ext", "*.dll", SearchOption.AllDirectories);
            var extensionAssemblies = new List<Assembly>();
            foreach (var extensionPath in extensionPaths)
            {
                try
                {
                    Assembly extensionAssembly = null;
#if NET451
                    extensionAssembly = Assembly.LoadFrom(extensionPath);
#else
                    extensionAssembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(extensionPath);
#endif
                    extensionAssemblies.Add(extensionAssembly);
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                }
            }

            builder.AddRange(loader.Load(Features, CSharpFeatures).Union(extensionAssemblies));
            this.Assemblies = builder.ToImmutable();
        }
    }
}
