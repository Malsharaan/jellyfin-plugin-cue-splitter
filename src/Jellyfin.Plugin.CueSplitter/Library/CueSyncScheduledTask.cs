using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.CueSplitter.Library;

/// <summary>
/// Scheduled task to synchronize the CUE tracks without a full library scan.
/// </summary>
public class CueSyncScheduledTask : IScheduledTask
{
    private readonly CueTrackSynchronizer _synchronizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CueSyncScheduledTask"/> class.
    /// </summary>
    /// <param name="synchronizer">The synchronizer.</param>
    public CueSyncScheduledTask(CueTrackSynchronizer synchronizer)
    {
        _synchronizer = synchronizer;
    }

    /// <inheritdoc />
    public string Name => "Synchronize CUE tracks";

    /// <inheritdoc />
    public string Key => "CueSplitterSync";

    /// <inheritdoc />
    public string Description => "Creates, updates or removes the virtual tracks described by .cue sheets in music libraries. Also runs automatically after every library scan.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _synchronizer.SyncAllAsync(progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
