using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.EpisodeTools;

/// <summary>
/// Registers the plugin's API endpoints (the convert report) with Jellyfin.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddMvc()
            .AddApplicationPart(typeof(PluginServiceRegistrator).Assembly)
            .AddControllersAsServices();
    }
}
