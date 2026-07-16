using System.Buffers.Binary;
using System.Text.Json;

namespace GPTino.BridgeContract;

/// <summary>Length-prefixed UTF-8 JSON framing for byte-mode named pipes.</summary>
public sealed class BridgeFrameCodec
{
    public BridgeFrameCodec(int maximumFrameBytes = BridgeProtocol.DefaultMaximumFrameBytes)
    {
        if (maximumFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        }

        MaximumFrameBytes = maximumFrameBytes;
    }

    public int MaximumFrameBytes { get; }

    public async ValueTask WriteAsync(
        Stream stream,
        BridgeFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        frame.Validate();

        var content = JsonSerializer.SerializeToUtf8Bytes(frame, BridgeProtocol.JsonOptions);
        if (content.Length > MaximumFrameBytes)
        {
            throw new BridgeProtocolException(
                "frame_too_large",
                $"Frame is {content.Length} bytes; maximum is {MaximumFrameBytes} bytes.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, content.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BridgeFrame> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrameBytes)
        {
            throw new BridgeProtocolException(
                "invalid_frame_length",
                $"Frame length {length} is outside the allowed range.");
        }

        var content = new byte[length];
        await ReadExactlyAsync(stream, content, cancellationToken).ConfigureAwait(false);

        try
        {
            var frame = JsonSerializer.Deserialize<BridgeFrame>(content, BridgeProtocol.JsonOptions)
                ?? throw new JsonException("Bridge frame deserialized to null.");
            frame.Validate();
            return frame;
        }
        catch (JsonException exception)
        {
            throw new BridgeProtocolException("invalid_json", "Invalid bridge frame JSON.", exception);
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The bridge stream ended in the middle of a frame.");
            }

            offset += read;
        }
    }
}
