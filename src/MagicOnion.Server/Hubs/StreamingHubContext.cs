using MagicOnion.Utils;
using MessagePack;
using System.Collections.Concurrent;

namespace MagicOnion.Server.Hubs;

public class StreamingHubContext
{
    ConcurrentDictionary<string, object>? items;

    /// <summary>Object storage per invoke.</summary>
    public ConcurrentDictionary<string, object> Items
    {
        get
        {
            lock (this) // lock per self! is this dangerous?
            {
                if (items == null) items = new ConcurrentDictionary<string, object>();
            }
            return items;
        }
    }

    /// <summary>Raw gRPC Context.</summary>
    public IStreamingServiceContext<byte[], byte[]> ServiceContext { get; internal set; } = default!; /* lateinit */
    public object HubInstance { get; internal set; } = default!; /* lateinit */

    public ReadOnlyMemory<byte> Request { get; internal set; }
    public string Path { get; internal set; } = default!; /* lateinit */
    public DateTime Timestamp { get; internal set; }

    public Guid ConnectionId => ServiceContext.ContextId;

    // public AsyncLock AsyncWriterLock { get; internal set; } = default!; /* lateinit */
    internal int MessageId { get; set; }
    internal int MethodId { get; set; }

    internal int responseSize = -1;
    internal Type? responseType;

    // helper for reflection
    internal async ValueTask WriteResponseMessageNil(ValueTask value)
    {
        if (MessageId == -1) // don't write.
        {
            return;
        }

        // MessageFormat:
        // response:  [messageId, methodId, response]
        byte[] BuildMessage()
        {
            using (var buffer = ArrayPoolBufferWriter.RentThreadStaticWriter())
            {
                var writer = new MessagePackWriter(buffer);

                writer.WriteArrayHeader(3);
                writer.Write(MessageId);
                writer.Write(MethodId);
                writer.WriteNil();
                writer.Flush();
                return buffer.WrittenSpan.ToArray();
            }
        }

        await value.ConfigureAwait(false);
        var result = BuildMessage();
        ServiceContext.QueueResponseStreamWrite(result);
        responseSize = result.Length;
        responseType = typeof(Nil);
    }

    internal async ValueTask WriteResponseMessage<T>(ValueTask<T> value)
    {
        if (MessageId == -1) // don't write.
        {
            return;
        }

        // MessageFormat:
        // response:  [messageId, methodId, response]
        byte[] BuildMessage(T v)
        {
            using (var buffer = ArrayPoolBufferWriter.RentThreadStaticWriter())
            {
                var writer = new MessagePackWriter(buffer);
                writer.WriteArrayHeader(3);
                writer.Write(MessageId);
                writer.Write(MethodId);
                writer.Flush();
                ServiceContext.MessageSerializer.Serialize(buffer, v);
                return buffer.WrittenSpan.ToArray();
            }
        }

        var vv = await value.ConfigureAwait(false);
        byte[] result = BuildMessage(vv);
        ServiceContext.QueueResponseStreamWrite(result);
        responseSize = result.Length;
        responseType = typeof(T);
    }

    internal ValueTask WriteErrorMessage(int statusCode, string detail, Exception? ex, bool isReturnExceptionStackTraceInErrorDetail)
    {
        // MessageFormat:
        // error-response:  [messageId, statusCode, detail, StringMessage]
        byte[] BuildMessage()
        {
            using (var buffer = ArrayPoolBufferWriter.RentThreadStaticWriter())
            {
                var writer = new MessagePackWriter(buffer);
                writer.WriteArrayHeader(4);
                writer.Write(MessageId);
                writer.Write(statusCode);
                writer.Write(detail);

                var msg = (isReturnExceptionStackTraceInErrorDetail && ex != null)
                    ? ex.ToString()
                    : null;

                if (msg != null)
                {
                    writer.Flush();
                    ServiceContext.MessageSerializer.Serialize(buffer, msg);
                }
                else
                {
                    writer.WriteNil();
                    writer.Flush();
                }
                return buffer.WrittenSpan.ToArray();
            }
        }

        var result = BuildMessage();
        ServiceContext.QueueResponseStreamWrite(result);
        responseSize = result.Length;
        return default;
    }
}
