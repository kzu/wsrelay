using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Xunit;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
namespace wsrelay.tests
{
    public class EndToEnd
    {
        readonly string apiKey;
        readonly string hostName;

        public EndToEnd()
        {
            dynamic config = JsonConvert.DeserializeObject(File.ReadAllText("appsettings.Development.json"));

            apiKey = config.API_KEY;
            hostName = config.APP_HOSTNAME;
        }

        [Fact]
        public void sessions()
        {
            Task.WaitAll(Enumerable.Range(0, 10).Select(_ => Task.Run(connect)).ToArray());
        }

        [Fact]
        public async Task connect()
        {
            Uri uri;
            if ("localhost".Equals(hostName, StringComparison.OrdinalIgnoreCase))
                uri = new Uri($"wss://{hostName}:5001");
            else
                uri = new Uri($"wss://{hostName}");

            var sender = new ClientWebSocket();
            var sessionId = Guid.NewGuid().ToString();
            sender.Options.SetRequestHeader("X-HUB", sessionId);
            sender.Options.SetRequestHeader("Authorization", apiKey);
            await sender.ConnectAsync(uri, CancellationToken.None);

            var receiver = new ClientWebSocket();
            receiver.Options.SetRequestHeader("X-HUB", sessionId);
            receiver.Options.SetRequestHeader("Authorization", apiKey);
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

        [SkippableFact]
        public async Task connectdontclose()
        {
            Uri uri;
            if ("localhost".Equals(hostName, StringComparison.OrdinalIgnoreCase))
                uri = new Uri($"wss://{hostName}:5001");
            else
                uri = new Uri($"wss://{hostName}");

            var sender = new ClientWebSocket();
            var sessionId = Guid.NewGuid().ToString();
            sender.Options.SetRequestHeader("X-HUB", sessionId);
            sender.Options.SetRequestHeader("Authorization", apiKey);
            await sender.ConnectAsync(uri, CancellationToken.None);

            var payload = new byte[1024 * 8];
            new Random().NextBytes(payload);

            await sender.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
    }
}
