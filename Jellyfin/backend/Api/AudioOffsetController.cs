using System.Net.Mime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Helpers;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Streams a title with its audio moved earlier or later, for clients whose player can't delay
/// audio (Roku). The client asks to start one at the viewer's position, plays the HLS playlist it
/// gets back, and counts the title's time from the start it's told. A seek outside what's been
/// made, or a new offset, starts another.
/// </summary>
[ApiController]
[Route("Moonfin/AudioOffset")]
[Authorize]
public sealed class AudioOffsetController : ControllerBase
{
    // fMP4 segments hold these as they are. Anything else would need the video encoded again,
    // which is what the client's normal playback is for.
    private static readonly HashSet<string> CopyableVideo = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "hevc", "h265", "av1", "vp9",
    };

    // Disc images and .strm links have no single file ffmpeg can read as a title
    private static readonly HashSet<string> UnplayableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".img", ".strm",
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly AudioOffsetStreamService _streams;

    public AudioOffsetController(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, AudioOffsetStreamService streams)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _streams = streams;
    }

    private static bool PlayableFile(string path) => !UnplayableExtensions.Contains(Path.GetExtension(path));

    private List<TrackInfo> ReadStreams(BaseItem item)
    {
        var tracks = new List<TrackInfo>();
        foreach (var stream in AnimeMediaStreamReader.GetStreams(_mediaSourceManager, item) ?? Array.Empty<object>())
        {
            if (stream == null)
            {
                continue;
            }

            // Read by name, since MediaStream's assembly isn't the same one on every server version
            var type = stream.GetType();
            object? Get(string name) => type.GetProperty(name)?.GetValue(stream);
            tracks.Add(new TrackInfo(
                Get("Type")?.ToString() ?? string.Empty,
                Get("Codec") as string ?? string.Empty,
                Get("Index") is int index ? index : -1,
                Get("IsExternal") is true,
                Get("Channels") as int?));
        }

        return tracks;
    }

    private sealed record TrackInfo(string Type, string Codec, int Index, bool IsExternal, int? Channels);

    [HttpPost("Start")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AudioOffsetStartResponse>> Start([FromBody] AudioOffsetStartRequest request, CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        if (!Guid.TryParse(request.ItemId, out var itemId))
        {
            return BadRequest(new { error = "ItemId is required" });
        }

        var user = HttpContext.RequestServices is { } services ? UserReflection.ResolveUser(services, userId.Value) : null;
        var item = _libraryManager.GetItemById(itemId);
        if (item == null || user == null || !UserReflection.IsVisibleTo(item, user))
        {
            return NotFound(new { error = "Item not found" });
        }

        // A title's other versions are items of their own, and a media source's id is that item's id.
        // Their files and tracks are read from the items, since the server's media-source helpers
        // differ from one Jellyfin version to the next.
        var sourceItem = item;
        if (Guid.TryParse(request.MediaSourceId, out var sourceId) && sourceId != item.Id)
        {
            sourceItem = _libraryManager.GetItemById(sourceId);
            if (sourceItem == null || !UserReflection.IsVisibleTo(sourceItem, user))
            {
                return NotFound(new { error = "Media source not found" });
            }
        }

        var path = sourceItem.Path;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path) || !PlayableFile(path))
        {
            return UnprocessableEntity(new { error = "Only local video files can be streamed with an audio offset" });
        }

        var streams = ReadStreams(sourceItem);
        var video = streams.FirstOrDefault(s => s.Type == "Video" && !s.IsExternal);
        if (video == null || !CopyableVideo.Contains(video.Codec))
        {
            return UnprocessableEntity(new { error = "This video's codec can't be copied into the stream" });
        }

        var audio = streams.FirstOrDefault(s => s.Type == "Audio" && !s.IsExternal && (request.AudioStreamIndex == null || s.Index == request.AudioStreamIndex))
            ?? streams.FirstOrDefault(s => s.Type == "Audio" && !s.IsExternal);
        if (audio == null)
        {
            return UnprocessableEntity(new { error = "This title has no audio track in its file" });
        }

        var deviceKey = userId.Value.ToString("N") + ":" + (this.GetDeviceIdFromClaims() ?? string.Empty);
        var startSeconds = Math.Max(0, (request.StartTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond);
        var (job, error) = await _streams.StartAsync(
            userId.Value,
            deviceKey,
            path,
            video.Index,
            audio.Index,
            audio.Codec,
            audio.Channels,
            startSeconds,
            AudioOffsetPlan.ClampOffset(request.OffsetMs),
            cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { error });
        }

        return Ok(new AudioOffsetStartResponse
        {
            JobId = job.Id,
            Url = $"Moonfin/AudioOffset/{job.Id}/{AudioOffsetPlan.PlaylistName}",
            StartTimeTicks = (long)Math.Round(job.StartSeconds * TimeSpan.TicksPerSecond),
            RunTimeTicks = sourceItem.RunTimeTicks,
            AudioStreamIndex = audio.Index,
            MediaSourceId = sourceItem.Id.ToString("N"),
        });
    }

    /// <summary>A job's playlist, init segment or media segment.</summary>
    [HttpGet("{jobId}/{file}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetFile([FromRoute] string jobId, [FromRoute] string file)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null || !AudioOffsetPlan.IsServedFile(file))
        {
            return NotFound();
        }

        var job = _streams.Get(jobId, userId.Value);
        var path = job == null ? null : Path.Combine(job.Directory, file);
        if (path == null || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        if (file == AudioOffsetPlan.PlaylistName)
        {
            return Content(AudioOffsetPlan.PointMasterAtShiftedAudio(System.IO.File.ReadAllText(path)), AudioOffsetPlan.ContentType(file));
        }

        // ffmpeg is still writing beside these, and replaces the playlists as they grow
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return File(stream, AudioOffsetPlan.ContentType(file));
    }

    [HttpDelete("{jobId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Stop([FromRoute] string jobId)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null || _streams.Get(jobId, userId.Value) == null)
        {
            return NotFound();
        }

        _streams.Stop(jobId);
        return NoContent();
    }
}

public sealed class AudioOffsetStartRequest
{
    public string? ItemId { get; set; }

    public string? MediaSourceId { get; set; }

    public int? AudioStreamIndex { get; set; }

    /// <summary>Positive plays the audio later than the picture, negative earlier.</summary>
    public int OffsetMs { get; set; }

    /// <summary>Where the viewer is in the title.</summary>
    public long? StartTimeTicks { get; set; }
}

public sealed class AudioOffsetStartResponse
{
    public string JobId { get; set; } = string.Empty;

    /// <summary>The playlist, relative to the server's address.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Where in the title the stream's first frame is. Usually a little before the asked-for start, on a keyframe.</summary>
    public long StartTimeTicks { get; set; }

    public long? RunTimeTicks { get; set; }

    public int AudioStreamIndex { get; set; }

    public string? MediaSourceId { get; set; }
}
