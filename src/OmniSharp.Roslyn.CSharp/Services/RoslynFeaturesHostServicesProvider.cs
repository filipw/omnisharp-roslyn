using System.Collections.Immutable;
using System.Composition;
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

            var path = "C:\\omnisharp-ext\\RemoveRegionAnalyzerAndCodeFix.dll";
            Assembly regions = null;
#if NET451
            regions = Assembly.LoadFrom(path);
#else
            regions = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
#endif

            builder.AddRange(loader.Load(Features, CSharpFeatures).Union(new[] { regions }));

            this.Assemblies = builder.ToImmutable();
        }
    }
}
