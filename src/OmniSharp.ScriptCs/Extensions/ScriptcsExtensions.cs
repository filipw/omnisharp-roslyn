using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OmniSharp.ScriptCs.Extensions
{
    internal static class ScriptcsExtensions
    {
        internal static IEnumerable<MetadataReference> MakeMetadataReferences(this ScriptServices scriptServices, IEnumerable<string> referencesPaths)
        {
            var listOfReferences = new List<MetadataReference>();
            foreach (var importedReference in referencesPaths.Where(x => !x.ToLowerInvariant().Contains("scriptcs.contracts")))
            {
                var result = scriptServices.MetadataFileReferenceCache.GetMetadataReference(importedReference);
                listOfReferences.Add(result);
            }

            return listOfReferences;
        }
    }
}
