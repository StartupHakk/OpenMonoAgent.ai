using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenMono.Acp;






public static class AcpServer
{
    public static WebApplication Build(AcpServerSettings settings, IServiceCollection services)
    {
        var builder = WebApplication.CreateBuilder();

        // Suppress noisy ASP.NET Core pipeline logs (request start/finish,
        // endpoint selection, result-writing). Keep Warning+ so real errors
        // still surface.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        foreach (var d in services)
            builder.Services.Add(d);











        builder.WebHost.ConfigureKestrel(o =>
        {
            if (settings.BindAllInterfaces) o.ListenAnyIP(settings.Port);
            else o.ListenLocalhost(settings.Port);
        });

        builder.Services.AddSingleton(settings);

        var app = builder.Build();
        AcpEndpoints.Map(app);
        return app;
    }
}
