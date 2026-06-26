using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.CustomerSite;

/// <summary>
/// Program.
/// </summary>
public class Program
{
    /// <summary>
    /// Defines the entry point of the application.
    /// </summary>
    /// <param name="args">The arguments.</param>
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddDebug()
                .AddConsole();
        });

        ILogger logger = loggerFactory.CreateLogger<Program>();
        logger.LogInformation("Service Provisioning initialized!!");
    }

    /// <summary>
    /// Creates the host builder.
    /// </summary>
    /// <param name="args">The arguments.</param>
    /// <returns> Host Builder.</returns>
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddDebug();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                // No UseUrls(): let the host set the listen URL via ASPNETCORE_URLS
                // so Azure App Service (Linux) can bind its injected port. AdminSite
                // already does this. For local dev, pass ASPNETCORE_URLS yourself, e.g.
                // ASPNETCORE_URLS="https://localhost:7101;http://localhost:7100" dotnet run
                //webBuilder.UseUrls("https://*:7101", "http://*:7100");
                webBuilder.UseStartup<Startup>();
            });
}