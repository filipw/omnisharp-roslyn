using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Recommendations;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using OmniSharp.Extensions;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Models.AutoComplete;
using OmniSharp.Models.V2;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.Documentation;

namespace OmniSharp.Roslyn.CSharp.Services.Intellisense
{
    public class TextEdit
    {
        public Range Range { get; set; }
        public string NewText { get; set; }
    }

    public class CharacterSetModificationRule
    {
        /// <summary>
        /// The kind of modification.
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter), /*camelCaseText*/ true)]
        public CharacterSetModificationRuleKind Kind { get; set; }

        /// <summary>
        /// One or more characters.
        /// </summary>
        public char[] Characters { get; set; }
    }

    public enum CharacterSetModificationRuleKind
    {
        Add,
        Remove,
        Replace
    }

    [OmniSharpEndpoint(OmniSharpEndpoints.AutoCompleteResolve, typeof(CompletionItemResolveRequest), typeof(CompletionItemResolveResponse))]
    public class CompletionItemResolveRequest : SimpleFileRequest
    {
        public int ItemIndex { get; set; }

        public string DisplayText { get; set; }
    }

    public class CompletionItemResolveResponse
    {
        public CompletionItemInfo Item { get; set; }
    }

    public class CompletionItemInfo
    {
        public string DisplayText { get; set; }
        public string Kind { get; set; }
        public string FilterText { get; set; }
        public string SortText { get; set; }
        public CharacterSetModificationRule[] CommitCharacterRules { get; set; }
        public string Description { get; set; }
        public TextEdit TextEdit { get; set; }
    }

    class OmniSharpCompletionContext
    {
        public OmniSharpCompletionContext(string fileName, CompletionList completionList)
        {
            FileName = fileName;
            CompletionList = completionList;
        }

        public string FileName { get; }
        public CompletionList CompletionList { get; }
    }

    [Shared]
    [OmniSharpHandler(OmniSharpEndpoints.AutoComplete, LanguageNames.CSharp)]
    [OmniSharpHandler(OmniSharpEndpoints.AutoCompleteResolve, LanguageNames.CSharp)]
    public class IntellisenseService :
        IRequestHandler<AutoCompleteRequest, IEnumerable<AutoCompleteResponse>>,
        IRequestHandler<CompletionItemResolveRequest, CompletionItemResolveResponse>
    {
        private OmniSharpCompletionContext _previousCompletionContext;

        private readonly OmniSharpWorkspace _workspace;
        private readonly FormattingOptions _formattingOptions;

        [ImportingConstructor]
        public IntellisenseService(OmniSharpWorkspace workspace, FormattingOptions formattingOptions)
        {
            _workspace = workspace;
            _formattingOptions = formattingOptions;
        }

        public async Task<CompletionItemResolveResponse> Handle(CompletionItemResolveRequest request)
        {
            if (_previousCompletionContext == null || _previousCompletionContext.FileName == null || _previousCompletionContext.CompletionList == null)
            {
                throw new InvalidOperationException($"{OmniSharpEndpoints.AutoCompleteResolve} end point cannot be called before {OmniSharpEndpoints.AutoComplete}");
            }

            if (!string.Equals(_previousCompletionContext.FileName, request.FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Cannot resolve completion item from '{request.FileName}' because the last {OmniSharpEndpoints.AutoComplete} request was for '{_previousCompletionContext.FileName}'");
            }

            if (request.ItemIndex < 0 || request.ItemIndex >= _previousCompletionContext.CompletionList.Items.Length)
            {
                throw new ArgumentOutOfRangeException($"Invalid item index: {request.ItemIndex}. Should be within range 0 to {_previousCompletionContext.CompletionList.Items.Length}");
            }

            var previousItem = _previousCompletionContext.CompletionList.Items[request.ItemIndex];
            if (!string.Equals(previousItem.DisplayText, request.DisplayText))
            {
                throw new ArgumentException($"Cannot resolve completion item. Display text does not match. Expected '{previousItem.DisplayText}' but was '{request.DisplayText}'");
            }

            var document = _workspace.GetDocument(request.FileName);
            var service = CompletionService.GetService(document);

            var description = await service.GetDescriptionAsync(document, previousItem);

            var getChangeAsyncWithUnimportedNamespaces = service.GetType().GetMethod("GetChangeAsync", BindingFlags.NonPublic | BindingFlags.Instance, Type.DefaultBinder, new[] { typeof(Document), typeof(CompletionItem), typeof(TextSpan), typeof(char?), typeof(CancellationToken) }, null);

            var changeTask = (Task<CompletionChange>)getChangeAsyncWithUnimportedNamespaces.Invoke(service, new object[] { document, previousItem, _previousCompletionContext.CompletionList.Span, null, CancellationToken.None });
            var change = await changeTask;
            var text = await document.GetTextAsync();

            var startTextLine = text.Lines.GetLineFromPosition(change.TextChange.Span.Start);
            var startPoint = new Point
            {
                Line = startTextLine.LineNumber,
                Column = change.TextChange.Span.Start - startTextLine.Start
            };

            var endTextLine = text.Lines.GetLineFromPosition(change.TextChange.Span.End);
            var endPoint = new Point
            {
                Line = endTextLine.LineNumber,
                Column = change.TextChange.Span.End - endTextLine.Start
            };

            return new CompletionItemResolveResponse
            {
                Item = new CompletionItemInfo
                {
                    DisplayText = previousItem.DisplayText,
                    Kind = previousItem.GetKind(),
                    FilterText = previousItem.FilterText,
                    SortText = previousItem.SortText,
                    CommitCharacterRules = GetCommitCharacterRulesModels(previousItem.Rules.CommitCharacterRules),
                    Description = description.Text,
                    TextEdit = new TextEdit
                    {
                        NewText = change.TextChange.NewText,
                        Range = new Range
                        {
                            Start = startPoint,
                            End = endPoint
                        }
                    }
                }
            };
        }

        public async Task<IEnumerable<AutoCompleteResponse>> Handle(AutoCompleteRequest request)
        {
            var showItemsFromUnimportedNamespaces = typeof(CompletionService).Assembly.GetType("Microsoft.CodeAnalysis.Completion.CompletionOptions").GetField("ShowItemsFromUnimportedNamespaces", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)?.GetValue(null);
            var cast = showItemsFromUnimportedNamespaces as PerLanguageOption<bool?>;
            _workspace.Options = _workspace.CurrentSolution.Options.WithChangedOption(cast, LanguageNames.CSharp, true);

            var documents = _workspace.GetDocuments(request.FileName);
            var wordToComplete = request.WordToComplete;
            var completions = new HashSet<AutoCompleteResponse>();

            foreach (var document in documents)
            {
                var sourceText = await document.GetTextAsync();
                var position = sourceText.Lines.GetPosition(new LinePosition(request.Line, request.Column));
                var service = CompletionService.GetService(document);
                var completionList = await service.GetCompletionsAsync(document, position);

                if (completionList != null)
                {
                    _previousCompletionContext = new OmniSharpCompletionContext(request.FileName, completionList);

                    // Only trigger on space if Roslyn has object creation items
                    if (request.TriggerCharacter == " " && !completionList.Items.Any(i => i.IsObjectCreationCompletionItem()))
                    {
                        return completions;
                    }

                    // get recommended symbols to match them up later with SymbolCompletionProvider
                    var semanticModel = await document.GetSemanticModelAsync();
                    var recommendedSymbols = await Recommender.GetRecommendedSymbolsAtPositionAsync(semanticModel, position, _workspace);

                    var itemsWithProviders = completionList.Items
                        .ToDictionary(x =>
                        x.DisplayText + " " + x.GetType().GetProperty("ProviderName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(x) + " " + Guid.NewGuid().ToString(),
                        x => service.GetChangeAsync(document, x).GetAwaiter().GetResult()
                    );

                    var isSuggestionMode = completionList.SuggestionModeItem != null;
                    for (int i = 0; i < completionList.Items.Length; i++)
                    {
                        var item = completionList.Items[i];
                        var completionText = item.DisplayText;
                        var preselect = item.Rules.MatchPriority == MatchPriority.Preselect;
                        if (completionText.IsValidCompletionFor(wordToComplete))
                        {
                            var symbols = await item.GetCompletionSymbolsAsync(recommendedSymbols, document);
                            if (symbols.Any())
                            {
                                foreach (var symbol in symbols)
                                {
                                    if (item.UseDisplayTextAsCompletionText())
                                    {
                                        completionText = item.DisplayText;
                                    }
                                    else if (item.TryGetInsertionText(out var insertionText))
                                    {
                                        completionText = insertionText;
                                    }
                                    else
                                    {
                                        completionText = symbol.Name;
                                    }

                                    if (symbol != null)
                                    {
                                        if (request.WantSnippet)
                                        {
                                            foreach (var completion in MakeSnippetedResponses(request, symbol, completionText, preselect, isSuggestionMode))
                                            {
                                                completion.CompletionIndex = i;
                                                completions.Add(completion);
                                            }
                                        }
                                        else
                                        {
                                            var completion = MakeAutoCompleteResponse(request, symbol, completionText, preselect, isSuggestionMode);
                                            completion.CompletionIndex = i;
                                            completions.Add(completion);
                                        }
                                    }
                                }

                                // if we had any symbols from the completion, we can continue, otherwise it means
                                // the completion didn't have an associated symbol so we'll add it manually
                            }
                            else
                            {
                                // for other completions, i.e. keywords, create a simple AutoCompleteResponse
                                // we'll just assume that the completion text is the same
                                // as the display text.
                                var response = new AutoCompleteResponse()
                                {
                                    CompletionText = item.DisplayText,
                                    DisplayText = item.DisplayText,
                                    Snippet = item.DisplayText,
                                    Kind = request.WantKind ? item.Tags.First() : null,
                                    IsSuggestionMode = isSuggestionMode,
                                    Preselect = preselect,
                                    CompletionIndex = i
                                };

                                completions.Add(response);
                            }
                        }
                    }
                }
            }

            return completions
                .OrderByDescending(c => c.CompletionText.IsValidCompletionStartsWithExactCase(wordToComplete))
                .ThenByDescending(c => c.CompletionText.IsValidCompletionStartsWithIgnoreCase(wordToComplete))
                .ThenByDescending(c => c.CompletionText.IsCamelCaseMatch(wordToComplete))
                .ThenByDescending(c => c.CompletionText.IsSubsequenceMatch(wordToComplete))
                .ThenBy(c => c.DisplayText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CompletionText, StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<AutoCompleteResponse> MakeSnippetedResponses(AutoCompleteRequest request, ISymbol symbol, string completionText, bool preselect, bool isSuggestionMode)
        {
            switch (symbol)
            {
                case IMethodSymbol methodSymbol:
                    return MakeSnippetedResponses(request, methodSymbol, completionText, preselect, isSuggestionMode);
                case INamedTypeSymbol typeSymbol:
                    return MakeSnippetedResponses(request, typeSymbol, completionText, preselect, isSuggestionMode);

                default:
                    return new[] { MakeAutoCompleteResponse(request, symbol, completionText, preselect, isSuggestionMode) };
            }
        }

        private IEnumerable<AutoCompleteResponse> MakeSnippetedResponses(AutoCompleteRequest request, IMethodSymbol methodSymbol, string completionText, bool preselect, bool isSuggestionMode)
        {
            var completions = new List<AutoCompleteResponse>();

            if (methodSymbol.Parameters.Any(p => p.IsOptional))
            {
                completions.Add(MakeAutoCompleteResponse(request, methodSymbol, completionText, preselect, isSuggestionMode, includeOptionalParams: false));
            }

            completions.Add(MakeAutoCompleteResponse(request, methodSymbol, completionText, preselect, isSuggestionMode));

            return completions;
        }

        private IEnumerable<AutoCompleteResponse> MakeSnippetedResponses(AutoCompleteRequest request, INamedTypeSymbol typeSymbol, string completionText, bool preselect, bool isSuggestionMode)
        {
            var completions = new List<AutoCompleteResponse>
            {
                MakeAutoCompleteResponse(request, typeSymbol, completionText, preselect, isSuggestionMode)
            };

            if (typeSymbol.TypeKind != TypeKind.Enum)
            {
                foreach (var ctor in typeSymbol.InstanceConstructors)
                {
                    completions.Add(MakeAutoCompleteResponse(request, ctor, completionText, preselect, isSuggestionMode));
                }
            }

            return completions;
        }

        private AutoCompleteResponse MakeAutoCompleteResponse(AutoCompleteRequest request, ISymbol symbol, string completionText, bool preselect, bool isSuggestionMode, bool includeOptionalParams = true)
        {
            var displayNameGenerator = new SnippetGenerator();
            displayNameGenerator.IncludeMarkers = false;
            displayNameGenerator.IncludeOptionalParameters = includeOptionalParams;

            var response = new AutoCompleteResponse();
            response.CompletionText = completionText;

            // TODO: Do something more intelligent here
            response.DisplayText = displayNameGenerator.Generate(symbol);

            response.IsSuggestionMode = isSuggestionMode;

            if (request.WantDocumentationForEveryCompletionResult)
            {
                response.Description = DocumentationConverter.ConvertDocumentation(symbol.GetDocumentationCommentXml(), _formattingOptions.NewLine);
            }

            if (request.WantReturnType)
            {
                response.ReturnType = ReturnTypeFormatter.GetReturnType(symbol);
            }

            if (request.WantKind)
            {
                response.Kind = symbol.GetKind();
            }

            if (request.WantSnippet)
            {
                var snippetGenerator = new SnippetGenerator();
                snippetGenerator.IncludeMarkers = true;
                snippetGenerator.IncludeOptionalParameters = includeOptionalParams;
                response.Snippet = snippetGenerator.Generate(symbol);
            }

            if (request.WantMethodHeader)
            {
                response.MethodHeader = displayNameGenerator.Generate(symbol);
            }

            response.Preselect = preselect;

            return response;
        }

        private static CharacterSetModificationRule[] GetCommitCharacterRulesModels(ImmutableArray<Microsoft.CodeAnalysis.Completion.CharacterSetModificationRule> commitCharacterRules)
        {
            var result = commitCharacterRules.Length > 0
                ? new CharacterSetModificationRule[commitCharacterRules.Length]
                : Array.Empty<CharacterSetModificationRule>();

            for (int i = 0; i < commitCharacterRules.Length; i++)
            {
                var rule = commitCharacterRules[i];
                result[i] = new CharacterSetModificationRule
                {
                    Characters = rule.Characters.ToArray(),
                    Kind = (CharacterSetModificationRuleKind)rule.Kind
                };
            }

            return result;
        }
    }
}
