using Jellyfin.Plugin.CueSplitter.Cue;
using Jellyfin.Plugin.CueSplitter.Library;
using Jellyfin.Plugin.CueSplitter.Streaming;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CueSplitter;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AudioProbe>();
        serviceCollection.AddSingleton<CueLibraryService>();
        serviceCollection.AddSingleton<CueTrackSynchronizer>();
        serviceCollection.AddSingleton<CueTrackStreamer>();
    }
}
