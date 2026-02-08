using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InlineXML.Modules.DI;

namespace InlineXML.Modules.Routing;

/// <summary>
/// Orchestrates the incoming LSP (Language Server Protocol) JSON-RPC message stream and dispatches requests to registered handlers.
/// Uses <see cref="JsonService"/> for all outgoing traffic to ensure NativeAOT compatibility.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// Think of this service as a mail carrier for your IDE:
/// <list type="number">
/// <item><description>The IDE sends messages to this server over stdin (like letters in a mailbox)</description></item>
/// <item><description>This service reads those messages, understands what the IDE is asking for</description></item>
/// <item><description>It looks up which code handler should handle each type of request (using a routing table)</description></item>
/// <item><description>It calls that handler to do the work</description></item>
/// <item><description>It sends the response back to the IDE over stdout (like a reply letter)</description></item>
/// </list>
/// The protocol used is LSP (Language Server Protocol), which is a standard way for IDEs to talk to language tools.
/// The format is JSON (human-readable structured data), wrapped in a special message envelope with headers.
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Listens to stdin for incoming LSP JSON-RPC messages</description></item>
/// <item><description>Parses the message headers (like "Content-Length") to know how much data to read</description></item>
/// <item><description>Deserializes JSON into LSP request objects</description></item>
/// <item><description>Routes requests to the appropriate registered handler functions</description></item>
/// <item><description>Sends responses and notifications back to the IDE on stdout</description></item>
/// <item><description>Handles errors gracefully without crashing the server</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Message Flow Example:</strong>
/// <code>
/// IDE sends:
///   Content-Length: 142\r\n
///   \r\n
///   {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":12345,...}}
/// 
/// Server (RoutingService):
///   1. Reads "Content-Length: 142" → knows to read exactly 142 bytes
///   2. Reads the JSON body
///   3. Looks for a handler registered for "initialize" method
///   4. Calls that handler with the request
///   5. Gets back a response
/// 
/// Server sends back:
///   Content-Length: 250\r\n
///   \r\n
///   {"jsonrpc":"2.0","id":1,"result":{"capabilities":{...}}}
/// 
/// IDE receives the response and shows results to the user
/// </code>
/// </para>
/// </remarks>
public class RoutingService : AbstractService
{
    /// <summary>
    /// Registry mapping LSP method names to their handler functions.
    /// Example: "initialize" → handler that initializes the language server.
    /// </summary>
    /// <remarks>
    /// Uses ConcurrentDictionary for thread-safe access. Methods can be registered at any time,
    /// and multiple threads might check for method handlers simultaneously.
    /// </remarks>
    private readonly ConcurrentDictionary<string, Func<LspRequest, ValueTask<LspResponse>>> _routes = new();
    
    /// <summary>
    /// Lock object to synchronize writes to stdout.
    /// </summary>
    /// <remarks>
    /// When multiple async tasks try to write to stdout simultaneously, we could get interleaved bytes
    /// (like two messages mixing together). This lock ensures only one thread writes at a time.
    /// </remarks>
    private readonly object _writeLock = new();
    
    /// <summary>
    /// Standard input stream where the IDE sends LSP messages.
    /// </summary>
    private readonly Stream _stdin = System.Console.OpenStandardInput();
    
    /// <summary>
    /// Standard output stream where we send responses and notifications back to the IDE.
    /// </summary>
    private readonly Stream _stdout = System.Console.OpenStandardOutput();

    /// <summary>
    /// Registers a handler function for a specific LSP method.
    /// </summary>
    /// <param name="route">The LSP method name (e.g., "initialize", "textDocument/didOpen", "shutdown").</param>
    /// <param name="handler">
    /// An async function that takes an LSP request and returns an LSP response.
    /// The function receives the method and parameters from the request, and should return a response
    /// with either a result (success) or an error object (failure).
    /// </param>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// Think of this like registering a function in a phone directory:
    /// "When someone calls asking for method X, call function Y."
    /// Multiple handlers can be registered, one for each LSP method the server supports.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Register a handler for the "initialize" method
    /// routingService.RegisterRoute("initialize", async (request) =>
    /// {
    ///     // Do initialization work...
    ///     return new LspResponse 
    ///     { 
    ///         Id = request.Id,
    ///         Result = new InitializeResult { Capabilities = ... }
    ///     };
    /// });
    /// </code>
    /// </example>
    public void RegisterRoute(string route, Func<LspRequest, ValueTask<LspResponse>> handler) => _routes[route] = handler;

    /// <summary>
    /// Starts the main server loop that listens to stdin and processes LSP messages indefinitely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the "main loop" of the language server. It:
    /// <list type="number">
    /// <item><description>Runs forever (until the IDE closes the connection)</description></item>
    /// <item><description>Waits for a message to arrive on stdin</description></item>
    /// <item><description>Reads the message headers to determine the message size</description></item>
    /// <item><description>Reads exactly that many bytes of JSON data</description></item>
    /// <item><description>Deserializes the JSON into an LspRequest object</description></item>
    /// <item><description>Routes the request to the appropriate handler</description></item>
    /// <item><description>Handles any errors without crashing</description></item>
    /// <item><description>Loops back to wait for the next message</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>The LSP Message Format:</strong>
    /// LSP messages have two parts:
    /// <code>
    /// [Headers]
    /// Content-Length: 142
    /// Content-Type: application/vscode-jsonrpc; charset=utf-8
    /// 
    /// [Blank Line]
    /// 
    /// [Body - exactly 142 bytes of JSON]
    /// {"jsonrpc":"2.0","id":1,"method":"initialize",...}
    /// </code>
    /// The Content-Length header tells us exactly how many bytes to read, so we don't read too much or too little.
    /// </para>
    /// </remarks>
    public async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                int contentLength = 0;
                string? line;

                // ========================================
                // STEP 1: Read and Parse Headers
                // ========================================
                // Headers come in lines like "Content-Length: 142", "Content-Type: ...", etc.
                // An empty line signals the end of headers
                while (!string.IsNullOrEmpty(line = ReadHeaderLine(_stdin)))
                {
                    // We specifically look for Content-Length to know how many bytes of JSON to expect
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract the number from "Content-Length: 142" → "142"
                        int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                    }
                }

                // If we didn't get a valid Content-Length, something's wrong - skip this message
                if (contentLength <= 0) continue;

                // ========================================
                // STEP 2: Read the JSON Body
                // ========================================
                // Now we know exactly how many bytes to read (contentLength)
                // We might not get all the bytes in one read() call, so keep reading until we have them all
                byte[] buffer = new byte[contentLength];
                int totalRead = 0;
                while (totalRead < contentLength)
                {
                    // Try to read the remaining bytes
                    int read = await _stdin.ReadAsync(buffer, totalRead, contentLength - totalRead);
                    
                    // If read returns 0, the stream is closed (IDE disconnected)
                    if (read == 0) return;
                    
                    totalRead += read;
                }

                // ========================================
                // STEP 3: Deserialize JSON to LSP Request
                // ========================================
                var request = JsonSerializer.Deserialize<LspRequest>(buffer, LspJsonContext.Default.LspRequest);
                if (request != null)
                {
                    // Don't wait for the handler - spawn it as a background task so we can listen for more messages
                    // This allows the server to handle requests concurrently
                    _ = Task.Run(async () =>
                    {
                        try 
                        {
                            // Route the request to the appropriate handler
                            var response = await RouteAsync(request);
                            
                            // Notifications (messages with no ID) don't get responses
                            // Only requests (messages with an ID) get responses
                            if (request.Id != null)
                            {
                                SendPayload(response);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log handler errors but don't crash the main loop
                            System.Console.Error.WriteLine($"[C# ROUTE ERROR]: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                // Log errors but continue listening - we want to stay alive even if something goes wrong
                System.Console.Error.WriteLine($"[C# ERROR]: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Reads a single header line from the input stream.
    /// </summary>
    /// <param name="stream">The stream to read from (typically stdin).</param>
    /// <returns>The header line without the trailing newline characters.</returns>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// LSP headers are text lines ending with \r\n (carriage return + line feed).
    /// This method reads bytes one at a time until it finds a \n (newline character).
    /// It strips out the \r and \n, leaving just the header content.
    /// </remarks>
    /// <example>
    /// If the stream contains: "Content-Length: 142\r\n"
    /// This method returns: "Content-Length: 142" (no trailing \r\n)
    /// </example>
    private string ReadHeaderLine(Stream stream)
    {
        var lineBytes = new List<byte>();
        int b;
        
        // Read bytes one at a time until we hit a newline
        while ((b = stream.ReadByte()) != -1)
        {
            byte cur = (byte)b;
            
            // When we hit a newline, we're done with this line
            if (cur == '\n') break;
            
            // Skip carriage returns (they're part of the line ending but not the content)
            if (cur != '\r') lineBytes.Add(cur);
        }
        
        // Convert the bytes to a UTF-8 string
        return Encoding.UTF8.GetString(lineBytes.ToArray());
    }

    /// <summary>
    /// Routes a request to the appropriate handler based on the method name.
    /// </summary>
    /// <param name="request">The LSP request to route.</param>
    /// <returns>
    /// The response from the handler, or a MethodNotFound error if no handler is registered.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the "dispatcher" that looks at the request's method name and finds the right handler:
    /// <list type="number">
    /// <item><description>Check if a handler is registered for this method name</description></item>
    /// <item><description>If yes, call it and return its response</description></item>
    /// <item><description>If no, return an error response saying "method not found"</description></item>
    /// </list>
    /// The MethodNotFound error (code -32601) is a standard LSP error code that means
    /// "you asked me to do something I don't know how to do."
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // If a handler is registered for "initialize"
    /// var request = new LspRequest { Id = 1, Method = "initialize", ... };
    /// var response = await routing.RouteAsync(request);
    /// // → Calls the "initialize" handler and returns its response
    /// 
    /// // If no handler is registered for "unknownMethod"
    /// var request = new LspRequest { Id = 2, Method = "unknownMethod", ... };
    /// var response = await routing.RouteAsync(request);
    /// // → Returns: { Id = 2, Error = { Code = -32601, Message = "Method 'unknownMethod' not found." } }
    /// </code>
    /// </example>
    public async ValueTask<LspResponse> RouteAsync(LspRequest request)
    {
        // Try to find a handler for this method name
        if (_routes.TryGetValue(request.Method, out var handler))
        {
            // Handler found - call it and return its response
            return await handler(request);
        }

        // No handler found - return a standard LSP "method not found" error
        return new LspResponse 
        { 
            Id = request.Id, 
            Error = new LspError 
            { 
                Code = -32601, 
                Message = $"Method '{request.Method}' not found." 
            } 
        };
    }

    /// <summary>
    /// Sends an asynchronous notification to the IDE.
    /// </summary>
    /// <typeparam name="T">The type of the parameters being sent.</typeparam>
    /// <param name="method">The LSP method name for this notification (e.g., "textDocument/publishDiagnostics").</param>
    /// <param name="@params">The parameters object containing the data for this notification.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// There's a difference between:
    /// <list type="bullet">
    /// <item><description><strong>Requests:</strong> "Hey server, can you do X?" (needs a response)</description></item>
    /// <item><description><strong>Notifications:</strong> "FYI, thing Y happened!" (no response needed)</description></item>
    /// </list>
    /// This method sends a one-way notification to the IDE. The IDE won't send back a response—it just receives
    /// the information.
    /// </para>
    /// <para>
    /// <strong>Common Notifications:</strong>
    /// <list type="bullet">
    /// <item><description><strong>textDocument/publishDiagnostics:</strong> "Here are the errors we found in your file"</description></item>
    /// <item><description><strong>window/logMessage:</strong> "Here's a message to show in your output panel"</description></item>
    /// <item><description><strong>window/showMessage:</strong> "Here's an important message to show the user"</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Send diagnostics (error report) to the IDE for a specific file
    /// routingService.SendNotification("textDocument/publishDiagnostics", new PublishDiagnosticsParams
    /// {
    ///     Uri = "file:///C:/MyProject/MyFile.cs",
    ///     Diagnostics = new List&lt;Diagnostic&gt;
    ///     {
    ///         new Diagnostic 
    ///         { 
    ///             Range = new Range { Start = new Position { Line = 10, Character = 5 }, ... },
    ///             Message = "CS0103: 'foo' does not exist in current context",
    ///             Severity = 1  // Error
    ///         }
    ///     }
    /// });
    /// </code>
    /// </example>
    public void SendNotification<T>(string method, T @params)
    {
        // Wrap the parameters in a notification object
        // The JsonService needs a concrete type (not just T) to serialize properly
        var notification = new LspNotification<T>
        {
            Method = method,
            Params = @params
        };
        
        // Send it through the same pathway as responses
        SendPayload(notification);
    }

    /// <summary>
    /// Core method that writes JSON data to stdout with LSP message headers.
    /// </summary>
    /// <param name="message">The object to serialize to JSON and send (can be a response or notification).</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the "send message" method. It:
    /// <list type="number">
    /// <item><description>Takes an object (response or notification) and serializes it to JSON</description></item>
    /// <item><description>Calculates how many bytes the JSON is</description></item>
    /// <item><description>Writes the LSP headers (Content-Length, etc.)</description></item>
    /// <item><description>Writes a blank line (separator between headers and body)</description></item>
    /// <item><description>Writes the JSON body</description></item>
    /// <item><description>Flushes stdout so the IDE receives the bytes immediately</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>LSP Message Format:</strong>
    /// <code>
    /// Content-Length: 250\r\n
    /// \r\n
    /// {"jsonrpc":"2.0","id":1,"result":{...}}
    /// </code>
    /// The headers come first, then a blank line, then the JSON body.
    /// </para>
    /// <para>
    /// <strong>Thread Safety:</strong>
    /// Multiple async tasks might try to send messages simultaneously. The _writeLock prevents
    /// interleaved writes (where bytes from two messages get mixed together). Only one thread
    /// can write to stdout at a time.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Send a response to a request
    /// var response = new LspResponse 
    /// { 
    ///     Id = 1, 
    ///     Result = new InitializeResult { Capabilities = ... }
    /// };
    /// SendPayload(response);
    /// // Writes to stdout:
    /// // "Content-Length: 250\r\n\r\n{...JSON...}"
    /// </code>
    /// </example>
    private void SendPayload(object message)
    {
	    try 
	    {
		    // Get the JSON serializer service
		    var jsonService = Services.Get<JsonService>();
		    
		    // Serialize the object to a JSON string
		    string jsonBody = jsonService.Stringify(message);
        
		    // Safety check: Don't send empty messages (corrupted or invalid responses)
		    // "{}" is an empty JSON object which usually indicates a serialization failure
		    if (string.IsNullOrEmpty(jsonBody) || jsonBody == "{}") return;

		    // Convert the JSON string to UTF-8 bytes
		    byte[] utf8Bytes = Encoding.UTF8.GetBytes(jsonBody);
		    
		    // Build the LSP header: "Content-Length: <number>\r\n\r\n"
		    // The \r\n appears twice: once after the header, once as a blank line
		    byte[] headerBytes = Encoding.UTF8.GetBytes($"Content-Length: {utf8Bytes.Length}\r\n\r\n");

		    // Synchronize access to stdout so multiple async tasks don't write at the same time
		    lock (_writeLock)
		    {
			    // Write headers to stdout
			    _stdout.Write(headerBytes, 0, headerBytes.Length);
			    
			    // Write JSON body to stdout
			    _stdout.Write(utf8Bytes, 0, utf8Bytes.Length);
			    
			    // Flush the stream to ensure the IDE receives the bytes immediately
			    // Without this, bytes might sit in a buffer and not reach the IDE
			    _stdout.Flush();
		    }
        
		    // Log the outgoing message to stderr for debugging
		    // If the IDE doesn't receive the message, you can check stderr to confirm we tried to send it
		    System.Console.Error.WriteLine($"[TX]: {jsonBody}");
	    }
	    catch (Exception ex)
	    {
		    // Log fatal errors but don't crash - other parts of the server might still work
		    System.Console.Error.WriteLine($"[ROUTING FATAL]: {ex.Message}");
	    }
    }
}