using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OmniSharp.Roslyn.CSharp.Services.Intellisense
{
    internal static class Extensions
    {
        private static readonly ImmutableArray<string> s_kindTags = ImmutableArray.Create(
           WellKnownTags.Class,
           WellKnownTags.Constant,
           WellKnownTags.Delegate,
           WellKnownTags.Enum,
           WellKnownTags.EnumMember,
           WellKnownTags.Event,
           WellKnownTags.ExtensionMethod,
           WellKnownTags.Field,
           WellKnownTags.Interface,
           WellKnownTags.Intrinsic,
           WellKnownTags.Keyword,
           WellKnownTags.Label,
           WellKnownTags.Local,
           WellKnownTags.Method,
           WellKnownTags.Module,
           WellKnownTags.Namespace,
           WellKnownTags.Operator,
           WellKnownTags.Parameter,
           WellKnownTags.Property,
           WellKnownTags.RangeVariable,
           WellKnownTags.Reference,
           WellKnownTags.Structure,
           WellKnownTags.TypeParameter);

        public static string GetKind(this CompletionItem completionItem)
        {
            foreach (var tag in s_kindTags)
            {
                if (completionItem.Tags.Contains(tag))
                {
                    return tag;
                }
            }

            return null;
        }
    }
}
