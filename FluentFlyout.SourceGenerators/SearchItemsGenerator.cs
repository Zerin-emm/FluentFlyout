using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace FluentFlyout.SourceGenerators
{
    [Generator]
    public class SearchItemsGenerator : ISourceGenerator
    {
        /// <summary>
        /// Reported when a page cannot be parsed at all. Previously the exception was swallowed, which
        /// silently produced a search index that was missing every entry of that page.
        /// </summary>
        private static readonly DiagnosticDescriptor PageParseFailed = new(
            id: "FFSG0001",
            title: "Failed to parse a page for the settings search index",
            messageFormat: "Could not parse '{0}' while building the settings search index: {1}",
            category: "FluentFlyout.SourceGenerators",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// Reported when an element is tagged as indexable but does not yield a search entry, so that a
        /// forgotten DynamicResource or Name shows up as a build warning instead of a missing entry.
        /// </summary>
        private static readonly DiagnosticDescriptor IndexableElementIgnored = new(
            id: "FFSG0002",
            title: "An indexable element produced no settings search entry",
            messageFormat: "The element '{0}' in '{1}' is tagged Indexable but has no Name and/or no Text bound with DynamicResource, so it was left out of the settings search index",
            category: "FluentFlyout.SourceGenerators",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public void Initialize(GeneratorInitializationContext context) { }

        public void Execute(GeneratorExecutionContext context)
        {
            var searchItems = new List<(string pageType, string resourceKey, string elementId)>();

            foreach (var file in context.AdditionalFiles.Where(f => f.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && f.Path.Replace('\\', '/').IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var pageName = Path.GetFileNameWithoutExtension(file.Path);

                try
                {
                    var text = file.GetText(context.CancellationToken)?.ToString();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var doc = XDocument.Parse(text);
                    foreach (var element in doc.Descendants())
                    {
                        var tagAttr = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Tag");
                        if (tagAttr != null && tagAttr.Value == "Indexable")
                        {
                            // Find the first TextBlock (or Run) with a DynamicResource binding. A Run is
                            // needed because a Hyperlink carries its localizable text in a Run, and only
                            // looking at TextBlock made every tagged Hyperlink unindexable.
                            var textBlocks = element.Descendants().Where(e => e.Name.LocalName is "TextBlock" or "Run");
                            string? resourceKey = null;
                            foreach (var tb in textBlocks)
                            {
                                var textAttr = tb.Attributes().FirstOrDefault(a => a.Name.LocalName == "Text");
                                if (textAttr != null && textAttr.Value.StartsWith("{DynamicResource ") && textAttr.Value.EndsWith("}"))
                                {
                                    resourceKey = textAttr.Value.Substring("{DynamicResource ".Length, textAttr.Value.Length - "{DynamicResource ".Length - 1).Trim();
                                    break;
                                }
                            }

                            // If no TextBlock, maybe the element itself has a Text property
                            if (resourceKey == null)
                            {
                                var textAttr = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Text");
                                if (textAttr != null && textAttr.Value.StartsWith("{DynamicResource ") && textAttr.Value.EndsWith("}"))
                                {
                                    resourceKey = textAttr.Value.Substring("{DynamicResource ".Length, textAttr.Value.Length - "{DynamicResource ".Length - 1).Trim();
                                }
                            }

                            var nameAttr = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name");

                            if (nameAttr != null && resourceKey != null)
                            {
                                searchItems.Add((pageName, resourceKey, nameAttr.Value));
                            }
                            else
                            {
                                context.ReportDiagnostic(Diagnostic.Create(
                                    IndexableElementIgnored,
                                    Location.None,
                                    element.Name.LocalName,
                                    file.Path));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Parsing a single page must not take the whole generator down, but it must also not
                    // disappear: a page that fails to parse silently loses every one of its search entries.
                    context.ReportDiagnostic(Diagnostic.Create(PageParseFailed, Location.None, file.Path, ex.Message));
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("using System;");
            sb.AppendLine("namespace FluentFlyoutWPF");
            sb.AppendLine("{");
            sb.AppendLine("    public partial class SettingsWindow");
            sb.AppendLine("    {");
            sb.AppendLine("        public static readonly (Type TargetPageType, string ResourceKey, string TargetElementId)[] SearchItems = new (Type, string, string)[]");
            sb.AppendLine("        {");

            foreach (var item in searchItems)
            {
                sb.AppendLine($"            (typeof(Pages.{item.pageType}), \"{EscapeString(item.resourceKey)}\", \"{EscapeString(item.elementId)}\"),");
            }

            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource("SearchItems.g.cs", sb.ToString());
        }

        /// <summary>
        /// Builds the body of a C# string literal for a value that comes from a XAML file.
        /// </summary>
        /// <remarks>
        /// The values are interpolated into generated source code, so a quote or a backslash in a resource
        /// key or element name would otherwise produce generated code that does not compile - or, worse,
        /// generated code that compiles into something other than the intended string.
        /// </remarks>
        private static string EscapeString(string value)
        {
            var sb = new StringBuilder(value.Length + 8);

            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\0':
                        sb.Append("\\0");
                        break;
                    default:
                        if (char.IsControl(c))
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
