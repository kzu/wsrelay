using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace wsrelay
{
    public class Startup
    {
        ILogger logger;
        Relay relay;
        ICollection<string> serverAddresses;

        public void ConfigureServices(IServiceCollection services) { }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILogger<Startup> logger)
        {
            this.logger = logger;
            relay = new Relay(logger);

            var serverAddressesFeature = app.ServerFeatures.Get<IServerAddressesFeature>();
            serverAddresses = serverAddressesFeature?.Addresses;
            var addresses = string.Join(", ", serverAddressesFeature?.Addresses);

            logger.LogInformation($"Addresses: {addresses}");

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseWebSockets();

            app.Use(async (context, next) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    // The /echo endpoint can be used as a sort of ping to detect whether 
                    // the service is working properly. It just returns the same messages 
                    // sent by the client.
                    if (context.Request.Path == "/echo")
                    {
                        await EchoAsync(context, webSocket);
                    }
                    else if (context.Request.Headers[Relay.SessionIdHeader] == StringValues.Empty)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await context.Response.WriteAsync(Relay.SessionIdHeader + " header not found.", context.RequestAborted);
                        context.Abort();
                    }
                    else
                    {
                        await relay.ProcessAsync(context, webSocket);
                    }
                }
                else if (context.Request.Path == "/ping")
                {
                    await PingAsync(context);
                }
                else
                {
                    await next();
                }

            });

            app.UseFileServer();
        }

        private async Task EchoAsync(HttpContext context, WebSocket socket)
        {
            var buffer = new byte[1024 * 4];
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            while (socket.State == WebSocketState.Open || !result.CloseStatus.HasValue)
            {
                // Just echo what we got
                await socket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
                // Receive the next chunk
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }
            // When the connection is not open anymore, shutdown gracefully.
            await socket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        }

        private async Task PingAsync(HttpContext context)
        {
            var serverAddress = serverAddresses?.FirstOrDefault();
            if (serverAddress == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await context.Response.WriteAsync($"No server address supported.", context.RequestAborted);
                return;
            }

            var socketUri = new UriBuilder(serverAddress);
            socketUri.Scheme = socketUri.Scheme == "https" ? "wss" : "ws";

            try
            {
                var client = new ClientWebSocket();
                client.Options.SetRequestHeader("X-SessionId", Guid.NewGuid().ToString());
                // See https://github.com/aspnet/IISIntegration/blob/master/src/Microsoft.AspNetCore.Server.IISIntegration/WebHostBuilderIISExtensions.cs#L47
                var pairingToken = Environment.GetEnvironmentVariable($"ASPNETCORE_TOKEN");
                if (!string.IsNullOrEmpty(pairingToken))
                    // See https://github.com/aspnet/IISIntegration/blob/master/src/Microsoft.AspNetCore.Server.IISIntegration/IISMiddleware.cs#L89
                    client.Options.SetRequestHeader("MS-ASPNETCORE-TOKEN", pairingToken);

                logger.LogInformation("Pinging {0}...", socketUri.Uri);
                await client.ConnectAsync(socketUri.Uri, context.RequestAborted);
                logger.LogInformation("Successfully connected to {0}...", socketUri.Uri);

                var payload = new byte[1024 * 8];
                new Random().NextBytes(payload);

                await client.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, context.RequestAborted);
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted);

                logger.LogInformation("Successfully send ping data to {0}...", socketUri.Uri);
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                await context.Response.WriteAsync("ack", context.RequestAborted);
            }
            catch (Exception e)
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await context.Response.WriteAsync(e.Message + ": " + socketUri.Uri, context.RequestAborted);
            }
        }
    }
}