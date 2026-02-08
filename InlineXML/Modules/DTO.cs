using System.Text.Json;
using System.Text.Json.Serialization;

namespace InlineXML.Modules.Routing;

/* --- THE LSP ENVELOPE --- */

public class LspRequest
{
    [JsonPropertyName("jsonrpc")] public string? Jsonrpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = string.Empty;
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
}

public class LspResponse
{
    [JsonPropertyName("jsonrpc")] public string Jsonrpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; set; }
    
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")] 
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LspError? Error { get; set; }
}

// Concrete type for Notifications (No ID)
public class LspNotification<T>
{
    [JsonPropertyName("jsonrpc")] public string Jsonrpc { get; set; } = "2.0";
    [JsonPropertyName("method")] public string Method { get; set; } = string.Empty;
    [JsonPropertyName("params")] public T? Params { get; set; }
}

public class LspError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }
}

/* --- DTOs --- */

public class InitializeResult
{
    [JsonPropertyName("capabilities")] public ServerCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("serverInfo")] public ServerInfo ServerInfo { get; set; } = new();
}

public class ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")] public int TextDocumentSync { get; set; } = 1;
    [JsonPropertyName("hoverProvider")] public bool HoverProvider { get; set; }
    [JsonPropertyName("completionProvider")] public CompletionOptions? CompletionProvider { get; set; }
    [JsonPropertyName("definitionProvider")] public bool DefinitionProvider { get; set; }
}

public class CompletionOptions
{
    [JsonPropertyName("resolveProvider")] public bool ResolveProvider { get; set; }
    [JsonPropertyName("triggerCharacters")] public List<string>? TriggerCharacters { get; set; }
}

public class ServerInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "InlineXML Language Server";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
}

public class PublishDiagnosticsParams
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;
    [JsonPropertyName("diagnostics")] public List<Diagnostic> Diagnostics { get; set; } = new();
}

public class Diagnostic
{
    [JsonPropertyName("range")] public Range Range { get; set; } = new();
    [JsonPropertyName("severity")] public int Severity { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "Roslyn";
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

public class Range
{
    [JsonPropertyName("start")] public Position Start { get; set; } = new();
    [JsonPropertyName("end")] public Position End { get; set; } = new();
}

public class Position
{
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("character")] public int Character { get; set; }
}

/* --- THE MASTER AOT CONTEXT --- */

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
)]
[JsonSerializable(typeof(LspRequest))]
[JsonSerializable(typeof(LspResponse))]
[JsonSerializable(typeof(LspNotification<PublishDiagnosticsParams>))] // This is the fix for diagnostics
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(List<Diagnostic>))]
[JsonSerializable(typeof(Range))]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(string))]
internal partial class LspJsonContext : JsonSerializerContext
{
}