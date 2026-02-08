using InlineXML.Configuration;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.InlineXml;
using InlineXML.Modules.Roslyn;
using InlineXML.Modules.Transformation;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InlineXML.Modules.Development;

public class AppDevService : AbstractService
{
    public AppDevService()
    {
       Events.AfterAllServicesReady.AddEventListener(mode =>
       {
           if (mode == ExecutionMode.DeveloperMode) Main();
           return mode;
       });
    }

    private void Main()
    {
         var xcsCode = @"
namespace SevenUI.ExtendedSharp.Examples;

using XMLTest;
using System.Collections.Generic;

public static class Component
{
    public static FunctionalComponent<NodeProps> UserCard = (props, children) => {
        return (
            <div className=""card"">
                <h2>{props.Username}</h2>
                <p>ID: {props.Id}</p>
            </div>
        );
    };

    public static FunctionalComponent<ListProps> UserList = (props, children) => {
        var users = new List<User> 
        { 
            new { Id = 1, Username = ""Alice"" },
            new { Id = 2, Username = ""Bob"" },
            new { Id = 3, Username = ""Charlie"" }
        };

        return (
            <div className=""user-list"">
                <h1>Users</h1>
                <div className=""list-container"">
                    {users.Map(user => (
                        <div key={user.Id} className=""user-item"">
                            <UserCard Username={user.Username} Id={user.Id} />
                            <button onclick={() => HandleSelect(user.Id)}>
                                Select {user.Username}
                            </button>
                        </div>
                    ))}
                </div>
                <footer>
                    <p>Total users: {users.Count}</p>
                    {props.ShowAdmin && (
                        <div className=""admin-panel"">
                            <button onclick={HandleRefresh}>Refresh</button>
                            <button onclick={HandleDelete}>Delete All</button>
                        </div>
                    )}
                </footer>
            </div>
        );
    };

    public static FunctionalComponent<DashboardProps> Dashboard = (props, children) => {
        var isLoading = true;
        var hasError = false;
        var itemCount = 42;

        return (
            <div className=""dashboard"">
                <header>
                    <h1>{props.Title ?? ""Dashboard""}</h1>
                    <nav>
                        {new[] { ""Home"", ""Settings"", ""About"" }.Map(item => (
                            <a href={""#"" + item.ToLower()} className=""nav-link"">
                                {item}
                            </a>
                        ))}
                    </nav>
                </header>

                <main>
                    {isLoading && (
                        <Loading />
                    )}
                    
                    {!isLoading && hasError && (
                        <div className=""error-message"">
                            <p>Error loading data</p>
                            <button onclick={HandleRetry}>Retry</button>
                        </div>
                    )}

                    {!isLoading && !hasError && (
                        <div className=""content"">
                            <UserList ShowAdmin={props.IsAdmin} />
                            
                            <div className=""stats"">
                                <div className=""stat-card"">
                                    <span className=""stat-label"">Items</span>
                                    <span className=""stat-value"">{itemCount}</span>
                                </div>

                                {props.Metrics.Map((metric, index) => (
                                    <div key={index} className=""stat-card"">
                                        <span className=""stat-label"">{metric.Name}</span>
                                        <span className=""stat-value"">{metric.Value}</span>
                                    </div>
                                ))}
                            </div>
                        </div>
                    )}
                </main>

                <footer className=""main-footer"">
                    <p>&copy; 2024 Test App</p>
                    <div className=""footer-links"">
                        {props.Links.Map(link => (
                            <a href={link.Href}>{link.Label}</a>
                        ))}
                    </div>
                </footer>
            </div>
        );
    };

    private static void HandleSelect(int userId) 
    { 
        System.Console.WriteLine($""Selected user: {userId}""); 
    }

    private static void HandleRefresh() 
    { 
        System.Console.WriteLine(""Refreshing...""); 
    }

    private static void HandleDelete() 
    { 
        System.Console.WriteLine(""Deleting all...""); 
    }

    private static void HandleRetry() 
    { 
        System.Console.WriteLine(""Retrying...""); 
    }
}";

        var syntaxTree = CSharpSyntaxTree.ParseText(xcsCode);
        var xmlExpressions = ExpressionLocator.FindExpressions(syntaxTree).ToList();
    
        var resultBuilder = new StringBuilder();
        var globalSourceMaps = new List<SourceMapEntry>();
        int lastPosition = 0;
        int currentTransformedOffset = 0;

        for (int i = 0; i < xmlExpressions.Count; i++)
        {
            var (start, end) = xmlExpressions[i];

            // CRITICAL FIX: Skip this expression if we already transformed it 
            // as part of a parent element's recursive child parsing.
            if (start < lastPosition) continue;

            // 1. MAP THE LEADING C# (Identity)
            int leadingLen = start - lastPosition;
            if (leadingLen > 0)
            {
                string leadingCs = xcsCode.Substring(lastPosition, leadingLen);
                globalSourceMaps.Add(new SourceMapEntry
                {
                    OriginalStart = lastPosition,
                    OriginalEnd = start,
                    TransformedStart = currentTransformedOffset,
                    TransformedEnd = currentTransformedOffset + leadingCs.Length
                });
                resultBuilder.Append(leadingCs);
                currentTransformedOffset += leadingCs.Length;
            }

            // 2. TRANSFORM XML
            var rawFragment = xcsCode.Substring(start, end - start);
            
            string trimmedFragment = rawFragment.Trim();
            int startParenIndex = trimmedFragment.StartsWith("(") ? 1 : 0;
            int endParenIndex = trimmedFragment.EndsWith(")") ? 1 : 0;

            int xmlLength = trimmedFragment.Length - startParenIndex - endParenIndex;
            if (xmlLength <= 0) 
            {
                resultBuilder.Append(rawFragment);
                currentTransformedOffset += rawFragment.Length;
                lastPosition = end;
                continue;
            }

            string xmlOnly = trimmedFragment.Substring(startParenIndex, xmlLength);
            int internalXmlOffset = rawFragment.IndexOf(xmlOnly); 

            var xmlSpan = xmlOnly.AsSpan();
            var parser = new Parser("Document", "CreateElement");
            var tokens = parser.Parse(ref xmlSpan);

            var builder = new AstBuilder();
            var ast = builder.Build(tokens, xmlOnly.AsSpan());

            var generator = new CodeGenerator("Document", "CreateElement");
            var generatedCode = generator.Generate(ast, out var localMaps);

            // 3. WEAVE AND MAP GENERATED CODE
            // We preserve original parentheses if they were there
            if (startParenIndex > 0) resultBuilder.Append("(");
            
            int codeStartInFile = currentTransformedOffset + startParenIndex;
            resultBuilder.Append(generatedCode);
            
            if (endParenIndex > 0) resultBuilder.Append(")");

            foreach (var local in localMaps)
            {
                globalSourceMaps.Add(new SourceMapEntry
                {
                    OriginalStart = start + internalXmlOffset + local.OriginalStart,
                    OriginalEnd = start + internalXmlOffset + local.OriginalEnd,
                    TransformedStart = codeStartInFile + local.TransformedStart,
                    TransformedEnd = codeStartInFile + local.TransformedEnd
                });
            }

            currentTransformedOffset += rawFragment.Length - xmlLength + generatedCode.Length;
            lastPosition = end;
        }

        // 4. MAP THE TRAILING C#
        if (lastPosition < xcsCode.Length)
        {
            string trailingCs = xcsCode.Substring(lastPosition);
            globalSourceMaps.Add(new SourceMapEntry
            {
                OriginalStart = lastPosition,
                OriginalEnd = xcsCode.Length,
                TransformedStart = currentTransformedOffset,
                TransformedEnd = currentTransformedOffset + trailingCs.Length
            });
            resultBuilder.Append(trailingCs);
        }

        System.Console.WriteLine("--- TRANSFORMED CODE ---");
        System.Console.WriteLine(resultBuilder.ToString());
    }
}