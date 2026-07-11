using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Api;

/// <summary>
/// Exposes the server's active transcode jobs so the admin dashboard can show
/// Moonfin client downloads in progress. Progressive jobs are the transcoded
/// downloads Moonfin clients start via /Videos/{id}/stream, while HLS jobs
/// belong to regular playback and already appear on the native dashboard.
/// </summary>
[ApiController]
[Route("Moonfin/Transcodes")]
[Authorize(Policy = "RequiresElevation")]
public sealed class MoonfinTranscodesController : ControllerBase
{
    // The server keeps its active transcode list private, so it is read
    // through reflection. Members are resolved once and reused.
    private static readonly object ReflectionLock = new();
    private static FieldInfo? _activeJobsField;
    private static MethodInfo? _killJobMethod;
    private static bool _reflectionResolved;

    private readonly ILogger<MoonfinTranscodesController> _logger;
    private readonly ITranscodeManager _transcodeManager;
    private readonly ISessionManager _sessionManager;

    public MoonfinTranscodesController(
        ILogger<MoonfinTranscodesController> logger,
        ITranscodeManager transcodeManager,
        ISessionManager sessionManager)
    {
        _logger = logger;
        _transcodeManager = transcodeManager;
        _sessionManager = sessionManager;
    }

    [HttpGet("Active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetActiveTranscodes([FromQuery] bool includePlayback = false)
    {
        try
        {
            var snapshot = SnapshotActiveJobs();
            if (snapshot == null)
            {
                return StatusCode(500, "Could not read active transcode jobs on this server version.");
            }

            var sessions = _sessionManager.Sessions.ToList();

            var result = snapshot
                .Where(job => !job.HasExited && (job.CompletionPercentage ?? 0) < 100)
                .Where(job => includePlayback || job.Type == TranscodingJobType.Progressive)
                .Select(job =>
                {
                    var isHardwareAccelerated = false;
                    try
                    {
                        var args = job.Process?.StartInfo?.Arguments ?? string.Empty;
                        isHardwareAccelerated =
                            args.Contains("vaapi", StringComparison.OrdinalIgnoreCase) ||
                            args.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
                            args.Contains("qsv", StringComparison.OrdinalIgnoreCase) ||
                            args.Contains("amf", StringComparison.OrdinalIgnoreCase) ||
                            args.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase) ||
                            args.Contains("hwaccel", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                    }

                    DateTime? startTime = null;
                    try
                    {
                        // Report the start in UTC so the browser measures elapsed
                        // time correctly even when it sits in a different timezone.
                        startTime = job.Process?.StartTime.ToUniversalTime();
                    }
                    catch
                    {
                    }

                    var session = string.IsNullOrEmpty(job.DeviceId)
                        ? null
                        : sessions.FirstOrDefault(s =>
                            string.Equals(s.DeviceId, job.DeviceId, StringComparison.OrdinalIgnoreCase));

                    return new
                    {
                        Id = job.Id,
                        MediaSource = job.MediaSource?.Name ?? job.MediaSource?.Path,
                        Path = job.Path,
                        Framerate = job.Framerate,
                        CompletionPercentage = job.CompletionPercentage,
                        BytesTranscoded = job.BytesTranscoded,
                        BitRate = job.BitRate,
                        PositionTicks = job.TranscodingPositionTicks,
                        RuntimeTicks = job.MediaSource?.RunTimeTicks,
                        StartTime = startTime,
                        IsHardwareAccelerated = isHardwareAccelerated,
                        IsLiveOutput = job.IsLiveOutput,
                        Type = job.Type.ToString(),
                        UserName = session?.UserName,
                        DeviceName = session?.DeviceName,
                        Client = session?.Client,
                    };
                });

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading active transcodes.");
            return StatusCode(500, "Error reading active transcodes.");
        }
    }

    [HttpDelete("Active/{jobId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> KillTranscodeJob(string jobId)
    {
        try
        {
            var snapshot = SnapshotActiveJobs();
            if (snapshot == null)
            {
                return StatusCode(500, "Could not read active transcode jobs on this server version.");
            }

            var job = snapshot.FirstOrDefault(j =>
                string.Equals(j.Id, jobId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.PlaySessionId, jobId, StringComparison.OrdinalIgnoreCase));

            if (job == null)
            {
                return NotFound($"Transcode job with ID '{jobId}' not found.");
            }

            if (_killJobMethod != null)
            {
                Func<string, bool> deleteFiles = _ => true;
                if (_killJobMethod.Invoke(_transcodeManager, new object[] { job, false, deleteFiles }) is Task task)
                {
                    await task;
                }
            }
            else
            {
                await _transcodeManager.KillTranscodingJobs(
                    job.DeviceId ?? string.Empty,
                    job.PlaySessionId,
                    _ => true);
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error killing transcode job {JobId}.", jobId);
            return StatusCode(500, "Error killing transcode job.");
        }
    }

    private void EnsureReflection()
    {
        if (_reflectionResolved)
        {
            return;
        }

        lock (ReflectionLock)
        {
            if (_reflectionResolved)
            {
                return;
            }

            var type = _transcodeManager.GetType();
            _activeJobsField = type.GetField(
                "_activeTranscodingJobs",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _killJobMethod = type.GetMethod(
                "KillTranscodingJob",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (_activeJobsField == null)
            {
                _logger.LogError(
                    "Could not find _activeTranscodingJobs on {Type}. The server may have changed its internals.",
                    type.FullName);
            }

            _reflectionResolved = true;
        }
    }

    /// <summary>
    /// Snapshots the active job list. The server mutates the list under a lock
    /// on the list instance, so the copy is taken under the same lock to avoid
    /// racing an enumeration against a mutation.
    /// </summary>
    private List<TranscodingJob>? SnapshotActiveJobs()
    {
        EnsureReflection();
        if (_activeJobsField == null)
        {
            return null;
        }

        var raw = _activeJobsField.GetValue(_transcodeManager);
        if (raw is not IEnumerable<TranscodingJob> jobs)
        {
            return new List<TranscodingJob>();
        }

        lock (raw)
        {
            return jobs.ToList();
        }
    }
}
