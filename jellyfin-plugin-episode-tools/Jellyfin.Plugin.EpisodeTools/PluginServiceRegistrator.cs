using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Jellyfin.Plugin.EpisodeTools.Intros;
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

        // Serve the intros found by the fast scanner as media segments (intro skip buttons).
        serviceCollection.AddSingleton<IMediaSegmentProvider, EpisodeToolsSegmentProvider>();
    }
}
