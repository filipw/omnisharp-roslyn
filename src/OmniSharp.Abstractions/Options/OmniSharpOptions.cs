using System;
using System.Composition;

namespace OmniSharp.Options
{
    public class OmniSharpOptions
    {
        public CodeActionOptions CodeActions { get; set; }
        public FormattingOptions FormattingOptions { get; }

        public OmniSharpOptions() : this(new FormattingOptions()) { }

        public OmniSharpOptions(FormattingOptions options)
        {
            FormattingOptions = options ?? throw new ArgumentNullException(nameof(options));
        }
    }

    public class CodeActionOptions
    {
        public string FolderPath { get; set; }
    }
}
