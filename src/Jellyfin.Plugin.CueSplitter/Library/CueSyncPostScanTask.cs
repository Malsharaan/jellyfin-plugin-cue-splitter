using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.CueSplitter.Library;

/// <summary>
/// Synchronizes the virtual CUE tracks at the end of every library scan.
/// </summary>
public class CueSyncPostScanTask : ILibraryPostScanTask
{
    private readonly CueTrackSynchronizer _synchronizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueSyncPostScanTask"/> class.
    /// </summary>
    /// <param name="synchronizer">The synchronizer.</param>
    public CueSyncPostScanTask(CueTrackSynchronizer synchronizer)
    {
        _synchronizer = synchronizer;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _synchronizer.SyncAllAsync(progress, cancellationToken);
    }
}
