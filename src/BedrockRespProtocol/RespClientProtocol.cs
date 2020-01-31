﻿using Bedrock.Framework.Protocols;
using Microsoft.AspNetCore.Connections;
using Resp;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace BedrockRespProtocol
{
    public sealed class RespClientProtocol
    {
        // private readonly ConnectionContext _connection;
        private readonly ProtocolReader _reader;
        private readonly ProtocolWriter _writer;

        public RespClientProtocol(ConnectionContext connection)
        {
            // _connection = connection;
            _reader = connection.CreateReader();
            _writer = connection.CreateWriter();
        }
        public async ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
        {
            var before = DateTime.UtcNow;
            await SendAsync(RedisFrame.Ping, cancellationToken);
            using var pong = await ReadAsync(cancellationToken);
            var after = DateTime.UtcNow;
            if (pong is RedisSimpleString rss && rss.Equals("PONG", StringComparison.OrdinalIgnoreCase))
            {
                return after - before;
            }
            else
            {
                throw new InvalidOperationException("huh: " + pong.ToString());
            }
        }

        public ValueTask SendAsync(RedisFrame frame, CancellationToken cancellationToken = default)
            => _writer.WriteAsync<RedisFrame>(MessageWriter, frame, cancellationToken);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowCanceled() => throw new OperationCanceledException();
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowAborted() => throw new ConnectionAbortedException();

        public ValueTask<RedisFrame> ReadAsync(CancellationToken cancellationToken = default)
        {
            var result = _reader.ReadAsync<RedisFrame>(MessageReader, cancellationToken);
            // avoid the async machinery if we already have the result on the pipe
            return result.IsCompletedSuccessfully ? new ValueTask<RedisFrame>(Validate(_reader, result.Result)) : Awaited(_reader, result);

            static async ValueTask<RedisFrame> Awaited(ProtocolReader reader, ValueTask<ProtocolReadResult<RedisFrame>> result)
                => Validate(reader, await result);

            static RedisFrame Validate(ProtocolReader reader, in ProtocolReadResult<RedisFrame> result)
            {
                reader.Advance();
                if (result.IsCanceled) ThrowCanceled();
                if (result.IsCompleted) ThrowAborted();
                return result.Message;
            }
        }

        private IMessageWriter<RedisFrame> MessageWriter => Resp2ClientWriter.Instance;
        private IMessageReader<RedisFrame> MessageReader => Resp2ClientReader.Instance;
    }
}
