using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
namespace wsrelay.tests
{
    public class EndToEnd
    {
        [Fact]
        public void sessions()
        {
            Task.WaitAll(Enumerable.Range(0, 20).Select(_ => Task.Run(connect)).ToArray());
        }

        [Fact]
        public async Task connect()
        {
            var uri = new Uri($"wss://{ThisAssembly.Metadata.APP_HOSTNAME}");
            //var uri = new Uri("wss://localhost:5001");

            var sender = new ClientWebSocket();
            var sessionId = Guid.NewGuid().ToString();
            sender.Options.SetRequestHeader("X-HUB", sessionId);
            sender.Options.SetRequestHeader("Authorization", ThisAssembly.Metadata.API_KEY);
            await sender.ConnectAsync(uri, CancellationToken.None);

            var receiver = new ClientWebSocket();
            receiver.Options.SetRequestHeader("X-HUB", sessionId);
            receiver.Options.SetRequestHeader("Authorization", ThisAssembly.Metadata.API_KEY);
            await receiver.ConnectAsync(uri, CancellationToken.None);

            var payload = new byte[1024 * 8];
            var received = new byte[0];
            new Random().NextBytes(payload);

            var ev = new ManualResetEventSlim();
            Task.Run(async () =>
            {
                // chunked reading of 4Kb
                var buffer = new byte[1024 * 4];
                var segment = new ArraySegment<byte>(buffer);
                try
                {
                    var mem = new MemoryStream();
                    var result = await receiver.ReceiveAsync(segment, CancellationToken.None);
                    while (receiver.State == WebSocketState.Open && !result.CloseStatus.HasValue)
                    {
                        mem.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            received = mem.ToArray();
                            ev.Set();
                            return;
                        }

                        result = await receiver.ReceiveAsync(segment, CancellationToken.None);
                    }
                }
                finally
                {
                    ev.Set();
                }
            });

            await sender.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, CancellationToken.None);

            if (Debugger.IsAttached)
                ev.Wait();
            else
                Assert.True(ev.Wait(10000), "Timed out");

            await sender.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            await receiver.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);

            Assert.Equal(payload.Length, received.Length);
            for (var i = 0; i < received.Length; i++)
            {
                Assert.Equal(payload[i], received[i]);
            }
        }
    }
}
