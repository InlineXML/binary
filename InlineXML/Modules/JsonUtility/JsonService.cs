using System.Buffers;
using System.Collections;
using System.Text;
using System.Text.Json;
using System.Reflection;
using InlineXML.Modules.DI;

namespace InlineXML.Modules.Routing;

/// <summary>
/// High-performance JSON serialization service that converts LSP responses and notifications to JSON.
/// Uses pooled buffers and a hybrid approach combining manual Utf8JsonWriter with JIT serialization.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// The RoutingService needs to send JSON data to the IDE over stdout. This service handles that conversion.
/// It takes C# objects (like LspResponse, diagnostics, hover information) and converts them to JSON strings.
/// </para>
/// <para>
/// <strong>Performance Optimization:</strong>
/// Instead of creating a new buffer for each JSON message, this service reuses a single 8KB buffer.
/// Think of it like a notepad: instead of buying a new notebook every time you write a message,
/// you erase the notebook and rewrite on it. This saves memory and CPU time.
/// </para>
/// <para>
/// <strong>JIT Serialization (Not AOT):</strong>
/// This service uses JIT (Just-In-Time) compilation for JSON serialization. This means:
/// <list type="bullet">
/// <item><description><strong>JIT:</strong> At runtime, the .NET compiler generates code to handle your specific types</description></item>
/// <item><description><strong>NOT AOT:</strong> Ahead-Of-Time compilation (pre-compiling all code) is NOT supported</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Why Not AOT? (The Technical Reason):</strong>
/// LSP is extremely polymorphic—the "params" field can be literally ANY object type depending on the method.
/// For example:
/// <list type="bullet">
/// <item><description>textDocument/didOpen sends OpenTextDocumentParams</description></item>
/// <item><description>textDocument/hover sends HoverParams</description></item>
/// <item><description>textDocument/completion sends CompletionParams</description></item>
/// <item><description>A generic notification can send any custom object</description></item>
/// </list>
/// AOT compilation requires knowing all types upfront. Since LSP's params is `object?` (could be anything),
/// AOT cannot pre-generate serialization code for an unknown, open-ended set of types.
/// At runtime (JIT), the .NET runtime can generate serialization code on-the-fly for whatever type
/// actually shows up. But this only works in JIT mode, not AOT.
/// </para>
/// <para>
/// <strong>The Hybrid Approach:</strong>
/// We manually write the LSP envelope (the "jsonrpc", "id", "result"/"error" fields) with precise control,
/// then delegate the polymorphic payload to JsonSerializer (JIT). This gives us:
/// <list type="bullet">
/// <item><description>Predictable envelope structure (manually written, no surprises)</description></item>
/// <item><description>Flexible payloads (JIT handles any type we pass)</description></item>
/// <item><description>Performance (pooled buffers, minimal allocations)</description></item>
/// </list>
/// </para>
/// </remarks>
public class JsonService : AbstractService
{
    /// <summary>
    /// Pooled buffer for JSON writing. Reused across all serialization calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// Instead of allocating a new byte array for every JSON message, we allocate one 8KB buffer
    /// and reuse it. After we send a message, we clear the buffer and reuse it for the next message.
    /// This is more efficient than creating/destroying buffers constantly.
    /// </para>
    /// <para>
    /// <strong>Why 8192 bytes?:</strong>
    /// Most LSP messages are under 8KB. If a message exceeds this, ArrayBufferWriter automatically
    /// grows the buffer. 8KB is a good balance between memory usage and growth frequency.
    /// </para>
    /// <para>
    /// <strong>Thread Safety:</strong>
    /// This buffer is NOT thread-safe. However, RoutingService uses a write lock before calling
    /// Stringify, so only one thread accesses it at a time.
    /// </para>
    /// </remarks>
    private readonly ArrayBufferWriter<byte> _bufferWriter = new(8192);
    
    /// <summary>
    /// JSON serializer options for all JIT serialization calls.
    /// Configures camelCase property names and null-omission for LSP compatibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What Each Option Does:</strong>
    /// <list type="bullet">
    /// <item><description>
    /// <strong>PropertyNamingPolicy = JsonNamingPolicy.CamelCase:</strong>
    /// LSP uses camelCase property names (e.g., "textDocument", not "TextDocument").
    /// .NET classes use PascalCase (e.g., "TextDocument"). This policy converts PascalCase
    /// C# properties to camelCase JSON automatically.
    /// </description></item>
    /// <item><description>
    /// <strong>DefaultIgnoreCondition = WhenWritingNull:</strong>
    /// When a C# property is null, don't include it in the JSON output.
    /// This keeps messages compact and LSP-compliant (many LSP fields are optional).
    /// </description></item>
    /// <item><description>
    /// <strong>WriteIndented = false:</strong>
    /// Don't add extra whitespace/newlines. Keep JSON compact for transmission.
    /// Indented JSON would waste bandwidth sending to the IDE.
    /// </description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes any LSP response or notification object into a JSON string using pooled memory.
    /// </summary>
    /// <param name="response">The object to serialize (LspResponse, LspNotification, or null).</param>
    /// <returns>A JSON string representation of the object, or "null" if the input is null.</returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the main entry point for JSON serialization. It:
    /// <list type="number">
    /// <item><description>Checks if the input is null</description></item>
    /// <item><description>Clears the reusable buffer</description></item>
    /// <item><description>Creates a Utf8JsonWriter that writes to the buffer</description></item>
    /// <item><description>Recursively writes the object to JSON</description></item>
    /// <item><description>Converts the buffer bytes to a UTF-8 string</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Pooled Buffer Reuse:</strong>
    /// Each call clears the buffer before use. After serialization, the buffer is left in memory
    /// for the next call to use. This eliminates allocation churn.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var response = new LspResponse 
    /// { 
    ///     Id = 1, 
    ///     Result = new { message = "Hello" }
    /// };
    /// string json = jsonService.Stringify(response);
    /// // Returns: {"jsonrpc":"2.0","id":1,"result":{"message":"Hello"}}
    /// </code>
    /// </example>
    public string Stringify(object? response)
    {
        // Handle null inputs
        if (response == null) return "null";

        // Clear the pooled buffer for reuse
        _bufferWriter.Clear();
        
        // Create a JSON writer that writes to the pooled buffer
        using (var writer = new Utf8JsonWriter(_bufferWriter, new JsonWriterOptions { Indented = false }))
        {
            // Recursively serialize the object
            WriteValue(writer, response);
        }

        // Convert the UTF-8 bytes in the buffer to a .NET string
        return Encoding.UTF8.GetString(_bufferWriter.WrittenSpan);
    }

    /// <summary>
    /// Recursive writer that manually handles LspResponse and delegates other types to JsonSerializer.
    /// </summary>
    /// <param name="writer">The Utf8JsonWriter to write to.</param>
    /// <param name="value">The value to serialize.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is a recursive "type dispatcher" that decides how to serialize each type:
    /// <list type="number">
    /// <item><description>If it's an LspResponse, manually write its structure</description></item>
    /// <item><description>If it's a primitive (string, int, bool, etc.), write it directly</description></item>
    /// <item><description>If it's anything else, delegate to JsonSerializer for JIT handling</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Why Manual LspResponse Handling?:</strong>
    /// LspResponse has a special "id" field that can be null. The LSP spec says we should omit
    /// the "id" field entirely if it's null. By handling it manually, we ensure this behavior.
    /// JsonSerializer's default behavior might include an "id": null, which isn't ideal.
    /// </para>
    /// <para>
    /// <strong>Why Delegate Other Types to JsonSerializer?:</strong>
    /// The "params" / "result" fields in LSP messages can be ANY type (polymorphic).
    /// We don't know upfront what type will show up. JsonSerializer uses JIT to generate
    /// serialization code at runtime for whatever type appears. This is the only way to handle
    /// truly open-ended polymorphism.
    /// </para>
    /// <para>
    /// <strong>The Hybrid Approach Explained:</strong>
    /// <code>
    /// Manual writing (predictable):
    /// {
    ///   "jsonrpc": "2.0",
    ///   "id": 1,
    ///   "result": [ ... delegate to JIT ... ]
    /// }
    /// 
    /// The envelope is exactly what we expect.
    /// The payload (result/error) is JIT-serialized and can be any type.
    /// </code>
    /// </para>
    /// </remarks>
    private void WriteValue(Utf8JsonWriter writer, object? value)
    {
        // Handle null values
        if (value == null)
        {
           writer.WriteNullValue();
           return;
        }

        switch (value)
        {
           // ========================================
           // LspResponse: Manual envelope writing
           // ========================================
           case LspResponse res:
              writer.WriteStartObject();
              
              // Always include "jsonrpc" field
              writer.WriteString("jsonrpc", res.Jsonrpc);
              
              // Conditionally include "id" field (omit if null)
              if (res.Id.HasValue) 
              { 
                 writer.WritePropertyName("id"); 
                 res.Id.Value.WriteTo(writer);  // JsonElement has its own WriteTo method
              }
              
              // Either "error" or "result" is populated, not both
              if (res.Error != null)
              {
                 // Response contains an error
                 writer.WritePropertyName("error");
                 // Delegate error serialization to JsonSerializer (it's a complex nested type)
                 JsonSerializer.Serialize(writer, res.Error, _options);
              }
              else
              {
                 // Response contains a successful result
                 writer.WritePropertyName("result");
                 // Delegate result serialization to JsonSerializer
                 // This handles any type: InitializeResult, null, string, custom objects, etc.
                 JsonSerializer.Serialize(writer, res.Result, _options);
              }
              
              writer.WriteEndObject();
              break;

           // ========================================
           // Primitive Types: Direct writing
           // ========================================
           // These are fast-path primitives that don't need serialization logic
           case string s: 
              writer.WriteStringValue(s); 
              break;
              
           case int i: 
              writer.WriteNumberValue(i); 
              break;
              
           case long l: 
              writer.WriteNumberValue(l); 
              break;
              
           case bool b: 
              writer.WriteBooleanValue(b); 
              break;
              
           case JsonElement je: 
              // JsonElement is already parsed JSON, just write it directly
              je.WriteTo(writer); 
              break;

           // ========================================
           // Everything Else: Delegate to JIT
           // ========================================
           default:
              // This includes:
              // - LspNotification<T> (generic notifications)
              // - Custom result/param types (InitializeResult, Diagnostic, etc.)
              // - Any polymorphic payload type
              // 
              // JsonSerializer will JIT-compile serialization code for the specific type
              // at runtime. This is the only way to handle truly unknown types.
              JsonSerializer.Serialize(writer, value, _options);
              break;
        }
    }
}