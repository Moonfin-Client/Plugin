using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Devices;

namespace Moonfin.Server.Api
{
    [ApiController]
    [Route("Moonfin/Transcodes")]
    [Authorize(Policy = "RequiresElevation")]
    public class MoonfinTranscodesController : ControllerBase
    {
        private readonly ILogger<MoonfinTranscodesController> _logger;
        private readonly ITranscodeManager _transcodeManager;
        private readonly ISessionManager _sessionManager;
        private readonly IDeviceManager _deviceManager;

        public MoonfinTranscodesController(ILogger<MoonfinTranscodesController> logger, ITranscodeManager transcodeManager, ISessionManager sessionManager, IDeviceManager deviceManager)
        {
            _logger = logger;
            _transcodeManager = transcodeManager;
            _sessionManager = sessionManager;
            _deviceManager = deviceManager;
        }

        /// <summary>
        /// Gets all active transcode jobs.
        /// </summary>
        /// <response code="200">Active transcodes returned.</response>
        [HttpGet("Active")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<object>> GetActiveTranscodes()
        {
            try
            {
                var transcodeManagerType = _transcodeManager.GetType();
                var activeJobsField = transcodeManagerType.GetField("_activeTranscodingJobs", BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (activeJobsField == null)
                {
                    _logger.LogError("Could not find _activeTranscodingJobs field on ITranscodeManager.");
                    return StatusCode(500, "Internal reflection error: Could not find active jobs field.");
                }

                if (activeJobsField.GetValue(_transcodeManager) is not IEnumerable<TranscodingJob> activeJobs)
                {
                    return Ok(Array.Empty<object>());
                }

                var sessions = _sessionManager.Sessions.ToList();
                
                var result = activeJobs
                    .Where(job => !job.HasExited && (job.CompletionPercentage ?? 0) < 100)
                    .Select(job => {
                    var session = sessions.FirstOrDefault(s => s.Id == job.PlaySessionId || s.DeviceId == job.DeviceId);
                    var device = !string.IsNullOrEmpty(job.DeviceId) ? _deviceManager.GetDevice(job.DeviceId) : null;
                    
                    bool isHardwareAccelerated = false;
                    try {
                        var args = job.Process?.StartInfo?.Arguments ?? "";
                        isHardwareAccelerated = args.Contains("vaapi", StringComparison.OrdinalIgnoreCase) || args.Contains("nvenc", StringComparison.OrdinalIgnoreCase) || args.Contains("qsv", StringComparison.OrdinalIgnoreCase) || args.Contains("hwaccel", StringComparison.OrdinalIgnoreCase);
                    } catch { }

                    // Using the session and device defined above
                    
                    string deviceName = session?.DeviceName ?? session?.Client ?? device?.CustomName ?? device?.AppName ?? device?.Name ?? "Moonfin Device";
                    string userName = session?.UserName ?? device?.LastUserName ?? "Unknown User";

                    // Smart Fallback for Moonfin Sync (which sends null DeviceId)
                    if (session == null && device == null)
                    {
                        // Find all active Moonfin sessions
                        var moonfinSessions = _sessionManager.Sessions.Where(s => s.Client != null && s.Client.Contains("Moonfin", StringComparison.OrdinalIgnoreCase)).ToList();
                        
                        if (moonfinSessions.Count == 1)
                        {
                            // If there is exactly one Moonfin user online, it's highly likely it's them.
                            var ms = moonfinSessions.First();
                            deviceName = ms.DeviceName ?? ms.Client ?? "Moonfin Device";
                            userName = ms.UserName ?? "Moonfin User";
                        }
                        else if (moonfinSessions.Count > 1)
                        {
                            deviceName = "Moonfin App";
                            userName = "Moonfin User";
                        }
                    }

                    DateTime? startTime = null;
                    try {
                        startTime = job.Process?.StartTime;
                    } catch { }

                    return new
                    {
                        Id = job.Id,
                        PlaySessionId = job.PlaySessionId,
                        DeviceId = job.DeviceId,
                        DeviceName = deviceName,
                        UserName = userName,
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
                        HasExited = job.HasExited,
                        IsLiveOutput = job.IsLiveOutput,
                        Type = job.Type.ToString()
                    };
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading active transcodes via reflection.");
                return StatusCode(500, "Error reading active transcodes.");
            }
        }

        /// <summary>
        /// Kills a specific transcode job by ID or PlaySessionId.
        /// </summary>
        /// <response code="204">Transcode job killed.</response>
        [HttpDelete("Active/{jobId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> KillTranscodeJob(string jobId)
        {
            try
            {
                var transcodeManagerType = _transcodeManager.GetType();
                var activeJobsField = transcodeManagerType.GetField("_activeTranscodingJobs", BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (activeJobsField == null)
                {
                    return StatusCode(500, "Internal reflection error: Could not find active jobs field.");
                }

                if (activeJobsField.GetValue(_transcodeManager) is not IEnumerable<TranscodingJob> activeJobs)
                {
                    return NotFound("No active jobs found.");
                }

                var job = activeJobs.FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.OrdinalIgnoreCase) 
                                                      || string.Equals(j.PlaySessionId, jobId, StringComparison.OrdinalIgnoreCase));

                if (job == null)
                {
                    return NotFound($"Transcode job with ID '{jobId}' not found.");
                }

                // Call private KillTranscodingJob via reflection
                var killMethod = transcodeManagerType.GetMethod("KillTranscodingJob", BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (killMethod != null)
                {
                    // Signature: Task KillTranscodingJob(TranscodingJob job, bool closeLiveStream, Func<string, bool> delete)
                    Func<string, bool> deleteFiles = (path) => true; // Always delete partial files
                    var task = (Task)killMethod.Invoke(_transcodeManager, new object[] { job, false, deleteFiles });
                    if (task != null)
                    {
                        await task;
                    }
                }
                else
                {
                    // Fallback to public KillTranscodingJobs if private method is not found
                    await _transcodeManager.KillTranscodingJobs(job.DeviceId, job.PlaySessionId, (path) => true);
                }

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error killing transcode job {jobId}.");
                return StatusCode(500, "Error killing transcode job.");
            }
        }
    }
}
