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

namespace Moonfin.Server.Api
{
    [ApiController]
    [Route("Moonfin/Transcodes")]
    [Authorize(Policy = "RequiresElevation")]
    public class MoonfinTranscodesController : ControllerBase
    {
        private readonly ILogger<MoonfinTranscodesController> _logger;
        private readonly ITranscodeManager _transcodeManager;

        public MoonfinTranscodesController(ILogger<MoonfinTranscodesController> logger, ITranscodeManager transcodeManager)
        {
            _logger = logger;
            _transcodeManager = transcodeManager;
        }

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
                
                var result = activeJobs
                    .Where(job => !job.HasExited && (job.CompletionPercentage ?? 0) < 100)
                    .Select(job => {
                        bool isHardwareAccelerated = false;
                        try {
                            var args = job.Process?.StartInfo?.Arguments ?? "";
                            isHardwareAccelerated = args.Contains("vaapi", StringComparison.OrdinalIgnoreCase) || 
                                                    args.Contains("nvenc", StringComparison.OrdinalIgnoreCase) || 
                                                    args.Contains("qsv", StringComparison.OrdinalIgnoreCase) || 
                                                    args.Contains("hwaccel", StringComparison.OrdinalIgnoreCase);
                        } catch { }

                        DateTime? startTime = null;
                        try {
                            startTime = job.Process?.StartTime;
                        } catch { }

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

                var killMethod = transcodeManagerType.GetMethod("KillTranscodingJob", BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (killMethod != null)
                {
                    Func<string, bool> deleteFiles = (path) => true;
                    var task = (Task)killMethod.Invoke(_transcodeManager, new object[] { job, false, deleteFiles });
                    if (task != null)
                    {
                        await task;
                    }
                }
                else
                {
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
