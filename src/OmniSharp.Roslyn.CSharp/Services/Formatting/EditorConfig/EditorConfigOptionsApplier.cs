// adapted from https://github.com/dotnet/format/blob/master/src/Utilities/EditorConfigOptionsApplier.cs
// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.VisualStudio.CodingConventions;

namespace OmniSharp.Roslyn.CSharp.Services.Formatting.EditorConfig
{
    internal class EditorConfigOptionsApplier
    {
        private static readonly List<(IOption, OptionStorageLocation, MethodInfo)> _optionsWithStorage;

        static EditorConfigOptionsApplier()
        {
            _optionsWithStorage = new List<(IOption, OptionStorageLocation, MethodInfo)>();
            _optionsWithStorage.AddRange(GetPropertyBasedOptionsWithStorageFromTypes(typeof(FormattingOptions), typeof(CSharpFormattingOptions), typeof(SimplificationOptions)));
            _optionsWithStorage.AddRange(GetFieldBasedOptionsWithStorageFromTypes(typeof(CodeStyleOptions), typeof(CSharpFormattingOptions).Assembly.GetType("Microsoft.CodeAnalysis.CSharp.CodeStyle.CSharpCodeStyleOptions")));
        }

        public OptionSet ApplyConventions(OptionSet optionSet, ICodingConventionsSnapshot codingConventions, string languageName)
        {
            foreach (var optionWithStorage in _optionsWithStorage)
            {
                if (TryGetConventionValue(optionWithStorage, codingConventions, out var value))
                {
                    var option = optionWithStorage.Item1;
                    var optionKey = new OptionKey(option, option.IsPerLanguage ? languageName : null);
                    optionSet = optionSet.WithChangedOption(optionKey, value);
                }
            }

            return optionSet;
        }

        internal static IEnumerable<(IOption, OptionStorageLocation, MethodInfo)> GetPropertyBasedOptionsWithStorageFromTypes(params Type[] types)
            => types
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.GetProperty))
                .Where(p => typeof(IOption).IsAssignableFrom(p.PropertyType)).Select(p => (IOption)p.GetValue(null))
                .Select(GetOptionWithStorage).Where(ows => ows.Item2 != null);

        internal static IEnumerable<(IOption, OptionStorageLocation, MethodInfo)> GetFieldBasedOptionsWithStorageFromTypes(params Type[] types)
            => types
                .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(p => typeof(IOption).IsAssignableFrom(p.FieldType)).Select(p => (IOption)p.GetValue(null))
                .Select(GetOptionWithStorage).Where(ows => ows.Item2 != null);

        internal static (IOption, OptionStorageLocation, MethodInfo) GetOptionWithStorage(IOption option)
        {
            var editorConfigStorage = !option.StorageLocations.IsDefaultOrEmpty
                ? option.StorageLocations.FirstOrDefault(IsEditorConfigStorage)
                : null;

            var tryGetOptionMethod = editorConfigStorage?.GetType().GetMethod("TryGetOption");
            return (option, editorConfigStorage, tryGetOptionMethod);
        }

        internal static bool IsEditorConfigStorage(OptionStorageLocation storageLocation)
            => storageLocation.GetType().FullName.StartsWith("Microsoft.CodeAnalysis.Options.EditorConfigStorageLocation") ||
               storageLocation.GetType().FullName.StartsWith("Microsoft.CodeAnalysis.Options.NamingStylePreferenceEditorConfigStorageLocation");

        internal static bool TryGetConventionValue((IOption, OptionStorageLocation, MethodInfo) optionWithStorage, ICodingConventionsSnapshot codingConventions, out object value)
        {
            var (option, editorConfigStorage, tryGetOptionMethod) = optionWithStorage;
            value = null;

            var adjustedConventions = codingConventions.AllRawConventions.ToDictionary(kvp => kvp.Key, kvp => (string)kvp.Value);
            var args = new object[] { option, adjustedConventions, option.Type, value };

            var isOptionPresent = (bool)tryGetOptionMethod.Invoke(editorConfigStorage, args);
            value = args[3];

            return isOptionPresent;
        }
    }
}
