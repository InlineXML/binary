using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.InlineXml;

namespace InlineXML.Modules.Development;

/// <summary>
/// Development service for InlineXML tool contributors and debuggers.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppDevService</c> is intended exclusively for tool contributors and developers working on
/// the InlineXML framework itself. This service is NOT part of the public API and should never
/// be used or referenced by end users of the tool.
/// </para>
/// <para>
/// This service is typically activated via a development flag (e.g., <c>--dev</c>) and provides
/// quick visibility into execution flow, transformations, and internal state changes during
/// development and debugging sessions.
/// </para>
/// <para>
/// Use this service to prototype features, validate parser behavior, inspect token streams,
/// and generally iterate quickly on the framework without needing external test projects.
/// </para>
/// </remarks>
public class AppDevService : AbstractService
{
    /// <summary>
    /// Initializes the development service and hooks into the application startup lifecycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service subscribes to the <see cref="Events.AfterAllServicesReady"/> event to execute
    /// development-time code after all other services have been fully initialized. This ensures
    /// that the entire framework is ready before dev-specific logic runs.
    /// </para>
    /// </remarks>
    public AppDevService()
    {
       Events.AfterAllServicesReady.AddEventListener(mode =>
       {
          Main();
          return mode;
       });
    }

    /// <summary>
    /// Main entry point for development-time execution and debugging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Override or extend this method to add your own development and debugging logic.
    /// This is where you can quickly prototype features, inspect parser output, validate
    /// transformations, or trace execution paths without needing a separate test project.
    /// </para>
    /// <remarks>
    /// Examples:
    /// <list type="bullet">
    /// <item>Print parsed token streams to verify parser correctness</item>
    /// <item>Log syntax tree transformations and AST modifications</item>
    /// <item>Benchmark component resolution and expression parsing</item>
    /// <item>Validate code generation output before writing files</item>
    /// <item>Trace event dispatch and service initialization order</item>
    /// </list>
    /// </remarks>
    /// </remarks>
    private void Main()
    {
        // Sample XCS component code for testing
        var xcsCode = @"
return (
    <div className=""container"">
        <h1>{ title }</h1>
        <button onClick={ handleClick }>
            Click {username} me
        </button>
        {
            items.map(item => (
                <div key={ item.id }>
                    <p>{ item.name }</p>
                {1 < 5 ? ""Hello"" : ""World""}
                </div>
            ))
        }
    </div>
);
";

        System.Console.WriteLine("=== TOKENS ===\n");

        // Parse the XCS code into tokens
        var parser = new Parser("Factory", "CreateElement");
        var codeSpan = xcsCode.AsSpan();
        var tokens = parser.Parse(ref codeSpan);

        // Output all tokens
        foreach (var token in tokens)
        {
            var value = codeSpan.Slice(token.Start, token.End - token.Start).ToString();
            System.Console.WriteLine($"[{token.Kind}] \"{value}\" (pos {token.Start}-{token.End})");
        }

        System.Console.WriteLine("\n=== AST ===\n");

        // Build AST from tokens
        var builder = new AstBuilder();
        var ast = builder.Build(tokens, codeSpan);

        PrintAst(ast, codeSpan, indent: 0);

        System.Console.WriteLine("\n=== GENERATED CODE ===\n");

        // Generate C# code from AST
        var generator = new CodeGenerator("Factory", "CreateElement");
        var generatedCode = generator.Generate(ast, out var sourceMap);
        
        System.Console.WriteLine(generatedCode);

        System.Console.WriteLine("\n=== SOURCE MAP ===\n");

        // Output source map entries
        foreach (var entry in sourceMap)
        {
            System.Console.WriteLine($"Original: {entry.OriginalStart}-{entry.OriginalEnd} → Transformed: {entry.TransformedStart}-{entry.TransformedEnd}");
        }
		

    }

    private void PrintAst(List<AstNode> nodes, ReadOnlySpan<char> source, int indent)
    {
        foreach (var node in nodes)
        {
            var prefix = new string(' ', indent * 2);
            
            if (node is ElementNode element)
            {
                System.Console.WriteLine($"{prefix}<{element.TagName}>");
                
                if (element.Attributes.Count > 0)
                {
                    System.Console.WriteLine($"{prefix}  [attributes]");
                    foreach (var (name, value) in element.Attributes)
                    {
                        var valueStr = value switch
                        {
                            StringLiteralNode str => $"\"{str.Value}\"",
                            ExpressionNode expr => expr.Expression,
                            _ => "?"
                        };
                        System.Console.WriteLine($"{prefix}    {name} = {valueStr}");
                    }
                }
                
                if (element.Children.Count > 0)
                {
                    System.Console.WriteLine($"{prefix}  [children]");
                    PrintAst(element.Children, source, indent + 2);
                }
            }
            else if (node is ExpressionNode expr)
            {
                System.Console.WriteLine($"{prefix}{{ {expr.Expression} }}");
            }
            else if (node is StringLiteralNode str)
            {
                System.Console.WriteLine($"{prefix}\"{str.Value}\"");
            }
        }
    }
}