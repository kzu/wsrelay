using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace wsrelay
{
    public class Startup
    {
        ILogger logger;
        Relay relay;
        ICollection<string> serverAddresses;

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            this.logger = logger;
            relay = new Relay(logger);
            
            var serverAddressesFeature = app.ServerFeatures.Get<IServerAddressesFeature>();
            serverAddresses = serverAddressesFeature?.Addresses;
            var addresses = string.Join(", ", serverAddressesFeature?.Addresses);

            if (!string.IsNullOrEmpty(addresses) && logger.IsEnabled(LogLevel.Information))
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
                    var auth = context.Request.Headers["Authorization"];

                    // Always require a matching API_KEY if configured (someone can decide to make a 
                    // wsrelay available that does not require even an API_KEY).
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_KEY")) && 
                        (auth == StringValues.Empty || auth != Environment.GetEnvironmentVariable("API_KEY")))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        await context.Response.WriteAsync("Invalid or missing Authorization header.", context.RequestAborted);
                        context.Abort();
                        return;
                    }

                    // Always require the X-HUB header, since that's what ties "channels" or "rooms", 
                    // otherwise it's not useful.
                    if (string.IsNullOrEmpty(context.Request.Headers[Relay.HubHeader]))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await context.Response.WriteAsync(Relay.HubHeader + " header not found.", context.RequestAborted);
                        context.Abort();
                        return;
                    }

                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    // The /echo endpoint can be used as a sort of ping to detect whether 
                    // the service is working properly. It just returns the same messages 
                    // sent by the client.
                    if (context.Request.Path == "/echo")
                    {
                        await EchoAsync(context, webSocket);
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
                var clock = Stopwatch.StartNew();
                var client = new ClientWebSocket();
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_KEY")))
                    client.Options.SetRequestHeader("Authorization", Environment.GetEnvironmentVariable("API_KEY"));

                if (!string.IsNullOrEmpty(context.Request.Headers[Relay.HubHeader]))
                    client.Options.SetRequestHeader(Relay.HubHeader, context.Request.Headers[Relay.HubHeader]);
                else
                    client.Options.SetRequestHeader(Relay.HubHeader, Guid.NewGuid().ToString());

                // See https://github.com/aspnet/IISIntegration/blob/master/src/Microsoft.AspNetCore.Server.IISIntegration/WebHostBuilderIISExtensions.cs#L47
                var pairingToken = Environment.GetEnvironmentVariable($"ASPNETCORE_TOKEN");
                if (!string.IsNullOrEmpty(pairingToken))
                    // See https://github.com/aspnet/IISIntegration/blob/master/src/Microsoft.AspNetCore.Server.IISIntegration/IISMiddleware.cs#L89
                    client.Options.SetRequestHeader("MS-ASPNETCORE-TOKEN", pairingToken);

                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Pinging {0}...", socketUri.Uri);

                await client.ConnectAsync(socketUri.Uri, context.RequestAborted);

                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Successfully connected to {0}...", socketUri.Uri);

                var payload = new byte[1024 * 8];
                new Random().NextBytes(payload);

                await client.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, context.RequestAborted);
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted);

                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Successfully send ping data to {0}...", socketUri.Uri);

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                clock.Stop();
                await context.Response.WriteAsync($@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8' />
    <title>WebSockets Relay</title>
</head>
<body>
    <p style='height: 32px; background-image: url(https://raw.github.com/kzu/wsrelay/master/icon/32.png); background-repeat: no-repeat; padding-left: 36px; padding-top: 7px'>
        ack in {clock.ElapsedMilliseconds} ms
    </p>
</body>
</html>", context.RequestAborted);
            }
            catch (Exception e)
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await context.Response.WriteAsync(e.Message + ": " + socketUri.Uri, context.RequestAborted);
            }
        }
    }
}