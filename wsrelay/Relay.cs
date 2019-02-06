using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace wsrelay
{
    public class Relay
    {
        ConcurrentDictionary<string, List<WebSocket>> sessions = new ConcurrentDictionary<string, List<WebSocket>>();
        ConcurrentDictionary<WebSocket, BlockingCollection<Message>> output = new ConcurrentDictionary<WebSocket, BlockingCollection<Message>>();
        private ILogger logger;

        public const string HubHeader = "X-HUB";

        public Relay(ILogger logger) => this.logger = logger;

        public async Task ProcessAsync(HttpContext context, WebSocket socket)
        {
            var hubId = context.Request.Headers[HubHeader];
            if (hubId == StringValues.Empty)
                return;

            var sockets = sessions.GetOrAdd(hubId.ToString(), _ => new List<WebSocket>());
            lock (sockets)
                sockets.Add(socket);

            logger.LogTrace("Processing new connection for hub {0}", hubId);

            var cts = new CancellationTokenSource();
            context.RequestAborted.Register(() => cts.Cancel());

#pragma warning disable CS4014 // We don't await this call because it must run on a separate thread
            Task.Run(() => SendAsync(sockets, socket, hubId, cts.Token));
#pragma warning restore CS4014

            await ReadAsync(sockets, socket, hubId, cts.Token);
            cts.Cancel();
        }

        private async Task SendAsync(List<WebSocket> sockets, WebSocket socket, string sessionId, CancellationToken cancellation)
        {
            logger.LogTrace("Starting SendAsync loop...");

            var messages = output.GetOrAdd(socket, _ => new BlockingCollection<Message>());
            foreach (var message in messages.GetConsumingEnumerable(cancellation))
            {
                try
                {
                    logger.LogTrace("Got message to broadcast for hub {0}", sessionId);
                    await socket.SendAsync(new ArraySegment<byte>(message.Payload), message.MessageType, true, cancellation);
                }
                catch (Exception e)
                {
                    try
                    {
                        logger.LogWarning("Sending failed for socket on hub {0} with {1}. Closing...", sessionId, e.Message);
                        await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Failed to broadcast message to client", CancellationToken.None);
                    }
                    finally
                    {
                        lock (sockets)
                            sockets.Remove(socket);
                    }
                }
            }
        }

        private async Task ReadAsync(List<WebSocket> sockets, WebSocket socket, string sessionId, CancellationToken cancellation)
        {
            try
            {
                // chunked reading of 4Kb
                var buffer = new byte[1024 * 4];
                var segment = new ArraySegment<byte>(buffer);
                var mem = new MemoryStream();

                logger.LogTrace("Starting ReadAsync loop...");

                var result = await socket.ReceiveAsync(segment, cancellation);
                while (socket.State == WebSocketState.Open && !result.CloseStatus.HasValue)
                {
                    logger.LogTrace("Received {0} bytes...", result.Count);

                    mem.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        logger.LogTrace("Read complete message for hub {0}...", sessionId);
                        foreach (var other in sockets.ToArray())
                        {
                            if (other == socket)
                                continue;

                            output.GetOrAdd(other, _ => new BlockingCollection<Message>())
                                .Add(new Message(result.MessageType, mem.ToArray()));

                            mem = new MemoryStream();
                        }
                    }
                    else
                    {
                        logger.LogTrace("Read partial message for hub {0}...", sessionId);
                    }

                    result = await socket.ReceiveAsync(segment, cancellation);
                }

                logger.LogTrace("Closing socket for hub {0}: {1}", sessionId, result.CloseStatusDescription);
                await socket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
            }
            finally
            {
                lock (sockets)
                    sockets.Remove(socket);
            }
        }

        class Message
        {
            public Message(WebSocketMessageType messageType, byte[] payload)
            {
                MessageType = messageType;
                Payload = payload;
            }

            public WebSocketMessageType MessageType { get; }
            public byte[] Payload { get; }
        }
    }
}
