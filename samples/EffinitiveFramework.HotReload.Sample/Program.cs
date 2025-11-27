using EffinitiveFramework.Core.Http2;
using System.Collections.Concurrent;
using System.Text;

// Track active HTTP/2 connections for pushing updates
var http2Connections = new ConcurrentDictionary<Http2Connection, byte>();

// File watcher for C# module changes
Directory.CreateDirectory("./modules");
var watcher = new FileSystemWatcher("./modules", "*.cs")
{
    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
};

watcher.Changed += async (sender, e) =>
{
    if (e?.Name == null) return;
    
    Console.WriteLine($"📝 Module changed: {e.Name}");
    
    // Compile the module (simplified - in real app use Roslyn)
    var compiledModule = await CompileModuleAsync(e.FullPath ?? "");
    
    // Push to all connected clients via HTTP/2 server push
    foreach (var (connection, _) in http2Connections)
    {
        try
        {
            await connection.PushResourceAsync(
                associatedStreamId: 1,
                requestHeaders: new Dictionary<string, string>
                {
                    { ":method", "GET" },
                    { ":path", $"/modules/{e.Name}.dll" },
                    { ":scheme", "https" },
                    { ":authority", "localhost:5001" }
                },
                responseHeaders: new Dictionary<string, string>
                {
                    { ":status", "200" },
                    { "content-type", "application/octet-stream" },
                    { "x-hot-reload", "true" },
                    { "content-length", compiledModule.Length.ToString() }
                },
                responseBody: compiledModule,
                cancellationToken: default
            );
            
            Console.WriteLine($"✅ Pushed {e.Name}.dll to client ({compiledModule.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to push: {ex.Message}");
        }
    }
};

watcher.EnableRaisingEvents = true;

Console.WriteLine("🔥 HTTP/2 Server Push - Hot Reload Demo");
Console.WriteLine("=======================================");
Console.WriteLine();
Console.WriteLine("This demonstrates how to use HTTP/2 server push for hot-reloading");
Console.WriteLine("compiled C# modules in a Blazor-like application.");
Console.WriteLine();
Console.WriteLine("Features:");
Console.WriteLine("  • FileSystemWatcher monitors ./modules/*.cs");
Console.WriteLine("  • Changes trigger compilation (demo uses fake compiler)");
Console.WriteLine("  • Http2Connection.PushResourceAsync sends .dll to browser");
Console.WriteLine("  • Browser detects push via PerformanceObserver API");
Console.WriteLine();
Console.WriteLine("Try it:");
Console.WriteLine("  1. echo 'public class Test { }' > ./modules/Test.cs");
Console.WriteLine("  2. Edit Test.cs and save");
Console.WriteLine("  3. Watch server push update to browser automatically!");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop");
Console.WriteLine();

// Simplified compiler (in production, use Roslyn)
async Task<ReadOnlyMemory<byte>> CompileModuleAsync(string sourceFile)
{
    if (!File.Exists(sourceFile))
        return Array.Empty<byte>();
        
    var source = await File.ReadAllTextAsync(sourceFile);
    
    // Demo: fake compilation
    var fakeAssembly = Encoding.UTF8.GetBytes($"// Compiled from {Path.GetFileName(sourceFile)}\n{source}");
    
    /* Production implementation with Roslyn:
    
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    
    var syntaxTree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create(
        Path.GetFileNameWithoutExtension(sourceFile),
        new[] { syntaxTree },
        references: new[] { 
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        },
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    
    using var ms = new MemoryStream();
    var result = compilation.Emit(ms);
    
    if (!result.Success)
    {
        foreach (var diagnostic in result.Diagnostics)
            Console.WriteLine($"Error: {diagnostic}");
        return Array.Empty<byte>();
    }
    
    return ms.ToArray();
    */
    
    return fakeAssembly;
}

await Task.Delay(-1); // Keep running
