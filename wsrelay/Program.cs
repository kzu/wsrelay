using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;

namespace wsrelay
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            using (var shutdown = new CancellationTokenSource())
            {
                try
                {
                    await CreateWebHostBuilder(args).Build().RunAsync(shutdown.Token);
                }
                catch (IOException io) when (io.InnerException is AddressInUseException aiu)
                {
                    shutdown.Cancel();
                    Environment.Exit(-1);
                }
            }
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>();
    }
}