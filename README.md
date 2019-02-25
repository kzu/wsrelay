![Icon](https://raw.github.com/kzu/wsrelay/master/icon/32.png) wsrelay: a minimalistic WebSocket relay
============

An ASP.NET Core web service that acts as a relay for incoming WebSocket connections 
based on a required custom `X-HUB` header. 

Essentially, all connected clients that share the same `X-HUB` (which can be an arbitrary 
string) can broadcast messages between them.

## Deployment

<a href="https://azuredeploy.net/" target="_blank">
    <img src="https://azuredeploy.net/deploybutton.png"/>
</a>

Just click the button 👆 or alternatively, do it the hard way:

1. Fork this repository to your account
2. Create a [new Web App](https://portal.azure.com/#create/Microsoft.WebSite) in the Azure 
   portal
3. Once created, go to the **Deployment Center** tab within the App Service blade and select 
   the following options in the presented wizard:
    * Choose GitHub in the *source control* step
    * App Service Kudu build server in the *build provider* step
    * Organization, repo and branch for code in the *configure* step
4. Go to the **Application settings** tab, make sure you turn on the `Web sockets` option.
    * Optionally turn off PHP, Java and `ARR Affinity` since they are not used.
5. Add an App Setting named `API_KEY`, which can be any string (just create a GUID, say). 
   This will need to be passed in from connecting clients as the value of the `Authorization`
   header.
6. Since we're transmitting sensitive information via HTTP headers (the `Authorization` and 
   `X-HUB`), it's recommended you go to the *Custom Domains* tab and turn on *HTTPS Only*

After a brief while, the service will be up and running. The service provides a 
[/ping](wsrelay/Startup.cs#L96)
endpoint that connects via WebSockets with itself, sends a random 8k binary payload and 
closes the connection. It can be used from curl to quickly test that the service is operational:

```
curl -i https://YOURAPPSERVICE.azurewebsites.net/ping
```

You can use this endpoint to test for the service health too, for example. 

## Usage

To consume the deployed relay from C#/.NET you just need to install the 
[System.Net.WebSockets.Client](https://www.nuget.org/packages/System.Net.WebSockets.Client) 
nuget package, and use code like the following:

```csharp
var client = new ClientWebSocket();
var hubId = "my-hub";
var apiKey = "YOUR_ARBITRARY_STRING_FROM_WEB_APP_SETTINGS";
// The header is required, otherwise you'll get a 400 (Bad Request) response
client.Options.SetRequestHeader("X-HUB", hubId);
// The header is required, otherwise you'll get a 401 (Unauthorized) response
client.Options.SetRequestHeader("Authorization", apiKey);
await client.ConnectAsync(new Uri("wss://YOURAPPSERVICE.azurewebsites.net"), CancellationToken.None);
```

Once connected, you can use `SendAsync` or `ReceiveAsync` to interact with 
the other clients in the same hub.


## Why

WebSockets are awesome because they are bi-directional, binary and fast, and they 
work over standard 80 (unsecured) and 443 (SSL) ports, so there's no need to open 
firewall ports or anything on the client.

Of course you need a WebSockets server that listens on those ports and does something 
meaningful for the client connecting to it. One such very useful function is to provide 
a "hub" where connecting clients just broadcast messages to each other based on some 
shared identifier (i.e. on the same "room"). 

[Azure SignalR Service](https://azure.microsoft.com/en-us/services/signalr-service/) 
provides one such service so you don't have to deploy your own server. It has a couple 
interesting limitations, though: 

  * The [free tier](https://azure.microsoft.com/en-us/pricing/details/signalr-service/) 
    has a limit of 20 concurrent connections and 20k messages per day
  * SignalR itself ultimately exchanges JSON-serialized payloads as strings, meaning 
    if you need to exchange binary payloads, you'd need to Base64-encode them, which 
    increases the size of the transfered data and (for now?) 
    [does not support compression at the WebSocket level](https://github.com/dotnet/corefx/issues/15430).

An alternative that provide greater control over the underlying WebSocket connection 
is the [Azure Relay Hybrid Connections](https://docs.microsoft.com/en-us/azure/service-bus-relay/relay-hybrid-connections-dotnet-get-started). 
It has its own set of limitations too:

  * There is [no free tier](https://azure.microsoft.com/en-us/pricing/details/service-bus/), 
    and there is a significant $9.78 price *per listener* (that is the part that does the 
    useful thing when clients connect).
  * There is significant management overhead, since you need to create each of those "listener 
    endpoints" ahead of time (since they cost money) and doing so automatically is far
    from trivial (involving the [Azure Resource Manager](https://docs.microsoft.com/en-us/azure/azure-resource-manager/resource-group-overview) 
    and deployment templates and what-not).
  * Setting up the end-to-end connection is also 
    [far from simple](https://docs.microsoft.com/en-us/azure/service-bus-relay/relay-hybrid-connections-dotnet-get-started) 
    and is a long shot from what a typical `HttpClient` or `ClientWebSocket` connection looks like.


The [Azure App Service](https://azure.microsoft.com/en-us/pricing/details/app-service/windows/) 
provides a cost-effective alternative that supports WebSockets too (and is what `wsrelay` 
uses). It has its own limitations too: 

  * You need to deploy the app yourself (although doing it from GitHub is trivial, 
    as shown in the [Deployment](#deployment) steps above)
  * Scaling out instances is not as easy. You will need to either use the 
    [Azure Application Gateway](https://azure.microsoft.com/en-us/pricing/details/application-gateway/) 
    or [Application Request Routing](https://blogs.msdn.microsoft.com/tconte/2013/09/19/advanced-cookie-based-session-affinity-with-application-request-routing/) 
    so that requests that share the same `X-HUB` custom header are served by 
    the same server (given the current simple implementation which just keeps an in-memory list of clients).

If your use cases can live with these limitations, the `wsrelay` can be trivially 
deployed and run in just a few minutes, and provide fast connectivity for your real-time 
collaboration needs.
