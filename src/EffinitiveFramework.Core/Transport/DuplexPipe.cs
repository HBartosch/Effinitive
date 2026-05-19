using System.IO.Pipelines;

namespace EffinitiveFramework.Core.Transport;

/// <summary>
/// Helper to create a pair of connected pipes (DuplexPipe) for the transport layer.
/// Application reads from Transport.Input, writes to Transport.Output.
/// Transport reads from Application.Output, writes to Application.Input.
/// </summary>
internal static class DuplexPipe
{
    /// <summary>
    /// Create a connected pipe pair suitable for a socket transport.
    /// Returns the application-facing IDuplexPipe. The transport-facing
    /// pipes are returned via the out parameter.
    /// </summary>
    public static IDuplexPipe CreateConnectionPair(
        PipeOptions inputOptions,
        PipeOptions outputOptions,
        out IDuplexPipe transport)
    {
        var input = new Pipe(inputOptions);   // Transport writes, Application reads
        var output = new Pipe(outputOptions); // Application writes, Transport reads

        var applicationPipe = new Connection(input.Reader, output.Writer);
        transport = new Connection(output.Reader, input.Writer);

        return applicationPipe;
    }

    private sealed class Connection(PipeReader reader, PipeWriter writer) : IDuplexPipe
    {
        public PipeReader Input => reader;
        public PipeWriter Output => writer;
    }
}
