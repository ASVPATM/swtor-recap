using System.Buffers.Binary;
using System.Text.Json;
using SwtorRecap.Core;

namespace SwtorRecap.Engine;

public static class FrameProtocol
{
    public const int MaximumBytes=1024*1024;
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken cancellationToken)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,cancellationToken);
        var length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length<=0||length>MaximumBytes)throw new InvalidDataException("IPC frame size is invalid.");
        var data=new byte[length];await stream.ReadExactlyAsync(data,cancellationToken);
        return JsonSerializer.Deserialize<T>(data,Json.Options)??throw new InvalidDataException("IPC envelope is empty.");
    }
    public static async Task WriteAsync<T>(Stream stream,T value,CancellationToken cancellationToken)
    {
        var data=JsonSerializer.SerializeToUtf8Bytes(value,Json.Options);if(data.Length>MaximumBytes)throw new InvalidDataException("Response is too large; narrow the query.");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,data.Length);
        await stream.WriteAsync(header,cancellationToken);await stream.WriteAsync(data,cancellationToken);await stream.FlushAsync(cancellationToken);
    }
}
