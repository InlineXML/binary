using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using InlineXML.Modules.Routing;

namespace InlineXML.Modules.Roslyn
{
    /// <summary>
    /// Partial service responsible for diagnostic operations specifically tied to
    /// Language Server Protocol (LSP) completion requests.
    /// </summary>
    public partial class DiagnosticService
    {
        /// <summary>
        /// Registers the LSP completion route with the internal router.
        /// This method should only be called once from the main constructor
        /// of <see cref="DiagnosticService"/>.
        /// </summary>
        private void RegisterCompletionRoute()
        {
            _router.RegisterRoute("textDocument/completion", async request =>
            {
                try
                {
                    // If there are no parameters, return an empty response
                    if (request.Params is null)
                        return new LspResponse { Id = request.Id };

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    // Deserialize the incoming JSON LSP completion parameters
                    var completionParams = JsonSerializer.Deserialize<CompletionParams>(
                        request.Params.Value.GetRawText(), options);

                    if (completionParams is null)
                        return new LspResponse { Id = request.Id };

                    // Resolve completion suggestions for the given text document and position
                    var suggestions = await GetCompletionSuggestionsAsync(
                        completionParams.TextDocument.Uri,
                        completionParams.Position.Line,
                        completionParams.Position.Character);

                    // Convert each suggestion into a completion item for LSP
                    var items = suggestions.Select(name => new CompletionItem
                    {
                        Label = name,
                        InsertText = name,
                        Kind = CompletionItemKind.Variable
                    }).ToList();

                    return new LspResponse
                    {
                        Id = request.Id,
                        Result = new CompletionList
                        {
                            IsIncomplete = false,
                            Items = items
                        }
                    };
                }
                catch (Exception ex)
                {
                    // Log and return an empty response on error
                    System.Console.Error.WriteLine($"[COMPLETION ERROR] {ex}");
                    return new LspResponse { Id = request.Id };
                }
            });

        }

        /// <summary>
        /// Resolves symbol and HTML tag completion suggestions at a given
        /// position in an XML document.
        /// </summary>
        /// <param name="sourceUri">The URI of the XML document.</param>
        /// <param name="line">The zero-based line number in the XML document.</param>
        /// <param name="character">The zero-based character position on the line.</param>
        /// <returns>A list of suggested completion strings, including C# symbols and HTML tags.</returns>
        private async Task<IReadOnlyList<string>> GetCompletionSuggestionsAsync(
            string sourceUri,
            int line,
            int character)
        {
            try
            {
                // Convert URI to a local file system path
                var sourceLocal = _fileService.ToLocalPath(sourceUri);

                // Determine the project directory
                var projectDir = _workspaceService.FindProjectDir(sourceLocal);
                if (string.IsNullOrEmpty(projectDir))
                    projectDir = Path.GetDirectoryName(sourceLocal) ?? "";

                // Map source XML path to generated C# code path
                var relativePath = Path.GetRelativePath(projectDir, sourceLocal);
                var generatedPath = Path.Combine(
                    projectDir,
                    "Generated",
                    Path.ChangeExtension(relativePath, ".cs"));

                if (!_sourceMapCache.TryGetValue(generatedPath, out var mapInfo))
                    return Array.Empty<string>();

                if (!_projectFiles.TryGetValue(generatedPath, out var syntaxTree))
                    return Array.Empty<string>();

                // Load the XML document text
                SourceText xmlText;
                using (var stream = File.Open(
                           sourceLocal,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite))
                {
                    xmlText = SourceText.From(stream);
                }

                // Convert line/character to absolute offset
                var xmlOffset = xmlText.Lines.GetPosition(new LinePosition(line, character));

                // Find the mapping entry from XML to generated C# code
                var mapEntryCandidate = mapInfo.Maps
                    .Where(m => xmlOffset >= m.OriginalStart && xmlOffset <= m.OriginalEnd)
                    .OrderBy(m => m.OriginalEnd - m.OriginalStart)
                    .FirstOrDefault();

                if (mapEntryCandidate.TransformedEnd == 0)
                    return Array.Empty<string>();

                var mapEntry = mapEntryCandidate;
                var generatedOffset = mapEntry.TransformedStart + (xmlOffset - mapEntry.OriginalStart);

                // Use Roslyn to obtain semantic symbols at the generated code position
                var compilation = CSharpCompilation.Create("InlineXML.Completions")
                    .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                    .AddReferences(_commonReferences)
                    .AddSyntaxTrees(_projectFiles.Values);

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var symbolNames = semanticModel.LookupSymbols(generatedOffset)
                    .Select(s => s.Name)
                    .Distinct()
                    .ToList();

                // --- HTML completion integration ---
                var lineText = xmlText.Lines[line].ToString();
                var charIndex = Math.Min(character, lineText.Length);
                var textUpToCursor = lineText[..charIndex];

                // Determine partial token the user is typing
                var lastDelimiter = textUpToCursor.LastIndexOfAny(new[] { '<', ' ', '\t', '\r', '\n' });
                var partial = lastDelimiter >= 0 ? textUpToCursor[(lastDelimiter + 1)..] : textUpToCursor;

                // Hardcoded set of common HTML tags
                var htmlTags = new[]
                {
                    "div", "span", "form", "header", "footer",
                    "h1", "h2", "h3", "h4", "h5", "h6",
                    "p", "a", "button", "ul", "li", "section",
                    "article", "aside", "nav", "main"
                };

                var htmlCompletions = htmlTags
                    .Where(tag => tag.StartsWith(partial, StringComparison.OrdinalIgnoreCase));

                // Merge C# symbol completions with HTML tags
                return symbolNames
                    .Concat(htmlCompletions)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[COMPLETION RESOLVE ERROR] {ex}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Maps a Roslyn <see cref="ISymbol"/> to a corresponding LSP <see cref="CompletionItemKind"/>.
        /// </summary>
        /// <param name="symbol">The symbol to map.</param>
        /// <returns>The LSP completion kind for the given symbol.</returns>
        private static CompletionItemKind MapRoslynKindToCompletionKind(ISymbol symbol)
        {
            return symbol switch
            {
                IMethodSymbol _ => CompletionItemKind.Method,
                IPropertySymbol _ => CompletionItemKind.Property,
                IFieldSymbol _ => CompletionItemKind.Field,
                IEventSymbol _ => CompletionItemKind.Variable,
                INamedTypeSymbol nts => nts.TypeKind switch
                {
                    TypeKind.Class => CompletionItemKind.Class,
                    TypeKind.Interface => CompletionItemKind.Interface,
                    TypeKind.Struct => CompletionItemKind.Class,
                    TypeKind.Enum => CompletionItemKind.Module,
                    _ => CompletionItemKind.Text
                },
                _ => CompletionItemKind.Text
            };
        }
    }
}

#region Completion DTOs

/// <summary>
/// Parameters for an LSP completion request.
/// </summary>
public sealed class CompletionParams
{
    /// <summary>
    /// The text document for which completions are requested.
    /// </summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = new();

    /// <summary>
    /// The position within the text document where completion is requested.
    /// </summary>
    [JsonPropertyName("position")]
    public Position Position { get; set; } = new();
}

/// <summary>
/// Represents a list of completion items returned to the client.
/// </summary>
public sealed class CompletionList
{
    /// <summary>
    /// Indicates whether further completion results may be available.
    /// </summary>
    [JsonPropertyName("isIncomplete")]
    public bool IsIncomplete { get; set; }

    /// <summary>
    /// The set of completion items.
    /// </summary>
    [JsonPropertyName("items")]
    public List<CompletionItem> Items { get; set; } = new();
}

/// <summary>
/// LSP-compliant types for completion items.
/// </summary>
public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10
}

/// <summary>
/// Identifies a text document by URI.
/// </summary>
public sealed class TextDocumentIdentifier
{
    /// <summary>
    /// The URI of the text document.
    /// </summary>
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

/// <summary>
/// Represents an individual completion item returned by the LSP server.
/// </summary>
public sealed class CompletionItem
{
    /// <summary>
    /// The label of the completion item displayed in the UI.
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The text to be inserted if this completion is chosen.
    /// </summary>
    [JsonPropertyName("insertText")]
    public string InsertText { get; set; } = string.Empty;

    /// <summary>
    /// The kind of completion item (e.g., Method, Class, Variable).
    /// </summary>
    [JsonPropertyName("kind")]
    public CompletionItemKind Kind { get; set; }

    /// <summary>
    /// Additional details about the completion item (optional).
    /// </summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// Documentation or description associated with the completion item.
    /// </summary>
    [JsonPropertyName("documentation")]
    public string Documentation { get; set; } = string.Empty;
}

#endregion
