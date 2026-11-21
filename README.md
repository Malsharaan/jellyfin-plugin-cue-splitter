# CUE Splitter for Jellyfin

A Jellyfin plugin that shows albums stored as a **single audio image + `.cue` sheet** as individual tracks, the way foobar2000 or fooyin do, **without splitting or modifying the original file**.

- Works with any image ffmpeg can decode: FLAC, APE (Monkey's Audio), WavPack, WAV, TTA, TAK, ALAC...
- Every client works: web, mobile apps, desktop/TV apps, DLNA, and Subsonic-style apps going through Jellyfin's streaming API.
- Lossless and **sample-accurate**: each track is decoded on the fly from the image, with gapless boundaries exactly where the sheet puts them.
- Seeking works everywhere: tracks are served as byte-addressable streams with HTTP Range support.
- Transcoding (MP3/AAC/Opus, HLS) works as for any other song.

## How it works

```
 Music/Space - Deliverance/
 ├── Space - Deliverance.flac   ── hidden from the library (ignore rule)
 └── Space - Deliverance.cue    ── parsed ──► 12 virtual "Audio" items in the album
                                               path = http://127.0.0.1:8096/CueSplitter/Tracks/{id}/stream.wav?key=…
```

1. **Ignore rule**: an audio file referenced by a usable `.cue` sheet in the same folder is hidden from the library. You can turn this off in the plugin settings.
2. **Synchronization**: at the end of every library scan, or when you run the *Synchronize CUE tracks* scheduled task, the plugin parses the sheets of all music libraries. It then creates, updates or removes one `Audio` item per track inside the album folder. Each item gets:
   - title, track and disc number;
   - track artist and album artist (compilations are handled);
   - album, genres (split on `, ; / |`) and year;
   - an exact duration.

   It then refreshes the album and creates any missing artists.
3. **Streaming**: the "path" of each virtual track is an internal endpoint of the plugin. That endpoint returns a WAV stream: a computed header, followed by the PCM samples of the track decoded by Jellyfin's ffmpeg from the image (`-ss` + exact sample count). Because the size is known in advance, any byte range can be served. Jellyfin's normal pipeline (direct stream, transcoding, HLS) reads from this endpoint, so clients just see a regular song.

The internal endpoint only answers requests coming from the server itself and carrying a secret key. The key is generated at first start and stored in the plugin configuration.

### CUE handling details

- Track boundaries follow `INDEX 01`. A pregap (`INDEX 00`) belongs to the end of the previous track, which is foobar2000's default.
- The text encoding is detected automatically: a BOM if present, else UTF-8, else Windows-1252, which is typical of EAC sheets with accents.
- A `FILE` entry whose name does not match is still resolved: same base name with another extension (sheet says `.wav`, folder holds `.flac`), or the single audio file of the folder.
- Several sheets in one folder become discs 1, 2, ..., unless `REM DISCNUMBER` says otherwise. A duplicate sheet that describes the same image is ignored.
- Sheets that describe already split files (one track per file) are left alone.
- Multi-file sheets with several tracks per file are supported.

## Requirements

- Jellyfin **12.4** (targetAbi `12.4.0.0`, .NET 10)
- The ffmpeg bundled with Jellyfin

## Installation

### Option 1: plugin repository (recommended)

Adding the repository lets Jellyfin install the plugin from its catalog and notify you of updates.

1. In Jellyfin, open **Dashboard → Plugins**, then the **Repositories** tab (called *Manage repositories* in some versions).
2. Click **+** (Add) and enter:
   - **Repository name**: `CUE Splitter`
   - **Repository URL**:
     ```
     https://raw.githubusercontent.com/Malsharaan/jellyfin-plugin-cue-splitter/main/manifest.json
     ```
3. Save, then go to the **Catalog** tab. **CUE Splitter** is listed under the *Music* category.
4. Open it, choose the latest version and click **Install**.
5. **Restart Jellyfin.** On Windows, use the tray icon's *Restart* or restart the service. With Docker, run `docker restart jellyfin`. On Linux, run `sudo systemctl restart jellyfin`.
6. Run a library scan: **Dashboard → Libraries → Scan All Libraries**. Albums with a `.cue` sheet then show their individual tracks.

Updates appear in **Dashboard → Plugins** when a new version is published. Install them, then restart Jellyfin.

### Option 2: manual install from a release

1. Download `cue-splitter_<version>.zip` from the [Releases](https://github.com/Malsharaan/jellyfin-plugin-cue-splitter/releases) page.
2. Extract `Jellyfin.Plugin.CueSplitter.dll` into a new folder `CUE Splitter_<version>` inside the `plugins` folder of your Jellyfin data directory:
   - Windows: `C:\ProgramData\Jellyfin\Server\plugins\`
   - Linux (package): `/var/lib/jellyfin/plugins/`
   - Docker: `/config/plugins/` inside the container
3. Restart Jellyfin, then scan your music library.

### Uninstall

Go to **Dashboard → Plugins → CUE Splitter → Uninstall**. The plugin deletes its virtual tracks from the library. Your audio files are never touched. Then restart Jellyfin and run a library scan: the original album images reappear as single tracks.

### Publishing a new version (maintainers)

1. Bump `version` in `build.yaml` (four-part, e.g. `1.1.0.0`) and commit.
2. Tag and push with a changelog message:
   ```bash
   git tag -a v1.1.0.0 -m "What changed"
   git push origin v1.1.0.0
   ```
3. The CI then:
   - builds and tests the plugin;
   - attaches `cue-splitter_1.1.0.0.zip` to a GitHub release;
   - adds the version to `manifest.json` (URL, MD5 checksum, targetAbi) on `main`.

   Servers that use the repository see the update automatically.

### From source (Windows, local server)

```powershell
./scripts/deploy-local.ps1   # builds, installs into C:\ProgramData\Jellyfin\Server and restarts jellyfin.exe
```

Or by hand:

```bash
dotnet test
dotnet publish src/Jellyfin.Plugin.CueSplitter -c Release -o out
```

## Configuration

*Dashboard → Plugins → CUE Splitter*

| Setting | Default | Description |
|---|---|---|
| Hide the original album image | on | Only show the virtual tracks. |
| Internal server URL | automatic | URL the server uses to reach its own endpoint. Set it (for example `http://127.0.0.1:8096`) only if Jellyfin does not listen on the loopback interface. |

Changes apply after the next library scan, or after running *Dashboard → Scheduled tasks → Synchronize CUE tracks*.

## Notes and limitations

- Tracks are streamed as PCM WAV (about 1.4 Mbit/s for CD audio). On a LAN this is fine. Clients with a bitrate limit get a transcoded stream, as for any other file.
- Media info for a virtual track reports the stream that is served (PCM WAV), not the codec of the image.
- If you edit a `.cue` file, the change is picked up at the next library scan (or by the scheduled task). Real-time folder monitoring alone does not trigger it.
- If you change the server port, the next sync updates the tracks. Their ids, and so play counts and favorites, are kept.

## Development

```
src/Jellyfin.Plugin.CueSplitter/
├── Cue/          CUE parser, encoding detection, sheet → track plans (cached)
├── Streaming/    ffprobe format probe, WAV header, Range parsing, ffmpeg streamer
├── Library/      ignore rule, synchronizer (post-scan + scheduled task), metadata provider
├── Api/          internal stream endpoint
└── Configuration/
tests/            xUnit tests
```

## License

GPL-3.0. See [LICENSE](LICENSE).
