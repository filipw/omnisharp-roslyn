using Microsoft.AspNet.Mvc;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis;
using OmniSharp.Models;
using System.Threading.Tasks;

namespace OmniSharp
{
	public partial class OmnisharpController
	{
		[HttpPost("codeformat")]
		public async Task<IActionResult> FormatDocument([FromBody]CodeFormatRequest request)
		{
			_workspace.EnsureBufferUpdated(request);

			var documentId = _workspace.GetDocumentId(request.FileName);
			if (documentId == null)
			{
				return new HttpNotFoundResult();
			}

			var document = _workspace.CurrentSolution.GetDocument(documentId);
            var options = _workspace.Options
//				.WithChangedOption<string>(FormattingOptions.NewLine, LanguageNames.CSharp, "\n")
                .WithChangedOption<bool>(FormattingOptions.UseTabs, LanguageNames.CSharp, request.ExpandTab);
			var newDocument = await Formatter.FormatAsync(document, options);

			return new ObjectResult(new
			{
				Buffer = (await newDocument.GetTextAsync()).ToString()
			});
		}
	}
}