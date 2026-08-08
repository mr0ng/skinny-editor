using System.Text;
using System.Text.Json;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Protocol;

public sealed class JsonPipeConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposeStarted;

    public JsonPipeConnection(Stream stream)
    {
        _stream = stream;
        _reader = new(stream, new UTF8Encoding(false), leaveOpen: true);
        _writer = new(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public async Task SendAsync<T>(string type, T payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
        var payloadElement = JsonSerializer.SerializeToElement(payload, SceneSerializer.Options);
        var envelope = new ProtocolEnvelope(type, payloadElement);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0,
                this);
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ReadLoopAsync(
        Func<ProtocolEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            var envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(line, JsonOptions)
                ?? throw new JsonException("Received an empty protocol envelope.");
            await onMessage(envelope, cancellationToken);
        }
    }

    public static T GetPayload<T>(ProtocolEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(SceneSerializer.Options)
        ?? throw new JsonException($"Message '{envelope.Type}' did not contain a valid payload.");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        // Close the transport first so an in-flight write cannot hold the gate
        // forever when the peer has stopped reading during process teardown.
        await _stream.DisposeAsync();
        await _writeGate.WaitAsync();
        try
        {
            try
            {
                await _writer.DisposeAsync();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Closing the transport above intentionally interrupts a pending flush.
            }

            _reader.Dispose();
        }
        finally
        {
            // A send may already be waiting for the gate. Leave the semaphore alive
            // so that waiter can observe _disposeStarted and fail predictably.
            _writeGate.Release();
        }
    }
}
