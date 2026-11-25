using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Runs the remuxes <see cref="AudioOffsetPlan"/> describes, one at a time per device, and serves
/// their files until the client stops or walks away. A device that starts another (a seek outside
/// what's been made, or a new offset) replaces its old one.
/// </summary>
public sealed class AudioOffsetStreamService : IDisposable
{
    // Paused players ask for nothing, so the files outlive a long pause, but not a forgotten
    // session.
    private static readonly TimeSpan IdleLimit = TimeSpan.FromHours(3);
    private static readonly TimeSpan FirstSegmentWait = TimeSpan.FromSeconds(30);

    private readonly ILogger<AudioOffsetStreamService> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _config;
    private readonly ConcurrentDictionary<string, AudioOffsetJob> _jobs = new();
    private readonly Timer _sweep;

    public AudioOffsetStreamService(
        ILogger<AudioOffsetStreamService> logger,
        IMediaEncoder mediaEncoder,
        IConfigurationManager config)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
        _config = config;
        ClearLeftovers();
        _sweep = new Timer(_ => SweepIdle(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    // Jobs live beside the server's own transcodes, so they land on whatever disk the admin gave those.
    private string Root => Path.Combine(TranscodeRoot(), "moonfin-audio-offset");

    private string TranscodeRoot()
    {
        try
        {
            return ServerTranscodePath();
        }
        catch (Exception ex) when (ex is MissingMethodException or TypeLoadException)
        {
            return Path.Combine(_config.CommonApplicationPaths.CachePath, "transcodes");
        }
    }

    // Kept apart so a server without the helper fails here, where TranscodeRoot can catch it
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string ServerTranscodePath() => _config.GetTranscodePath();

    public AudioOffsetJob? Get(string jobId, Guid userId)
    {
        if (!_jobs.TryGetValue(jobId, out var job) || job.UserId != userId)
        {
            return null;
        }

        job.Touch();
        return job;
    }

    /// <summary>
    /// Starts a remux and waits until its first segment exists, so the client's first request for
    /// the playlist finds something to play. Returns null with the reason when ffmpeg gave up.
    /// </summary>
    public async Task<(AudioOffsetJob? Job, string? Error)> StartAsync(
        Guid userId,
        string deviceKey,
        string inputPath,
        int videoStreamIndex,
        int audioStreamIndex,
        string? audioCodec,
        int? audioChannels,
        double startSeconds,
        int offsetMs,
        CancellationToken cancellationToken)
    {
        foreach (var old in _jobs.Values.Where(j => j.DeviceKey == deviceKey).ToList())
        {
            Stop(old.Id);
        }

        var videoStart = await FindKeyframeAsync(inputPath, videoStreamIndex, startSeconds, cancellationToken).ConfigureAwait(false);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        // The picture and the shifted sound are made apart, since one ffmpeg reading the file twice
        // started the second input's audio seconds late
        var job = new AudioOffsetJob(id, userId, deviceKey, dir, videoStart);
        var started =
            StartFfmpeg(job, AudioOffsetPlan.VideoArguments(inputPath, videoStreamIndex, audioStreamIndex, audioCodec, audioChannels, videoStart, dir))
            && StartFfmpeg(job, AudioOffsetPlan.AudioArguments(inputPath, audioStreamIndex, audioCodec, audioChannels, videoStart, offsetMs, dir));
        if (!started)
        {
            job.Kill();
            TryDelete(dir);
            return (null, "ffmpeg did not start");
        }

        _jobs[id] = job;
        _logger.LogInformation(
            "Audio offset {Offset} ms for {Device}: remuxing {Path} from {Start:0.###}s",
            offsetMs,
            deviceKey,
            inputPath,
            videoStart);

        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < FirstSegmentWait)
        {
            if (await ReadyAsync(dir, cancellationToken).ConfigureAwait(false))
            {
                return (job, null);
            }

            if (job.AnyFailed)
            {
                break;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        var error = job.ErrorText();
        _logger.LogWarning("Audio offset remux of {Path} made nothing to play: {Error}", inputPath, error);
        Stop(id);
        return (null, string.IsNullOrEmpty(error) ? "The remux made no segments in time" : error);
    }

    private bool StartFfmpeg(AudioOffsetJob job, List<string> arguments)
    {
        var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = job.Directory,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) => job.AddError(e.Data);
        if (!process.Start())
        {
            process.Dispose();
            return false;
        }

        process.BeginErrorReadLine();
        job.Add(process);
        return true;
    }

    public bool Stop(string jobId)
    {
        if (!_jobs.TryRemove(jobId, out var job))
        {
            return false;
        }

        job.Kill();
        TryDelete(job.Directory);
        return true;
    }

    public void Dispose()
    {
        _sweep.Dispose();
        foreach (var id in _jobs.Keys.ToList())
        {
            Stop(id);
        }
    }

    private async Task<double> FindKeyframeAsync(string inputPath, int videoStreamIndex, double target, CancellationToken cancellationToken)
    {
        if (target <= 0)
        {
            return 0;
        }

        var psi = new ProcessStartInfo(_mediaEncoder.ProbePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in AudioOffsetPlan.KeyframeProbeArguments(inputPath, videoStreamIndex, target))
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var probe = Process.Start(psi);
            if (probe == null)
            {
                return target;
            }

            var output = await probe.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await probe.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var keyframes = AudioOffsetPlan.ParseKeyframes(output);
            return keyframes.Count == 0 ? target : AudioOffsetPlan.ChooseKeyframe(keyframes, target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without keyframe times the cut lands on whichever one ffmpeg finds, which only costs
            // the client a slightly wrong clock.
            _logger.LogWarning(ex, "Could not read keyframes of {Path}", inputPath);
            return target;
        }
    }

    private void SweepIdle()
    {
        foreach (var job in _jobs.Values.Where(j => j.IdleFor > IdleLimit).ToList())
        {
            Stop(job.Id);
        }
    }

    private void ClearLeftovers()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear old audio offset streams in {Root}", Root);
        }
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete audio offset stream {Dir}", dir);
        }
    }

    // The master playlist exists, and the video and the audio each have a segment to play
    private static async Task<bool> ReadyAsync(string dir, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(dir, AudioOffsetPlan.PlaylistName)))
        {
            return false;
        }

        foreach (var name in AudioOffsetPlan.MediaPlaylists)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) || !(await ReadShared(path, cancellationToken).ConfigureAwait(false)).Contains("#EXTINF", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> ReadShared(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}

public sealed class AudioOffsetJob
{
    private readonly List<Process> _processes = new();
    private readonly Queue<string> _errors = new();
    private long _lastTouch = Environment.TickCount64;

    public AudioOffsetJob(string id, Guid userId, string deviceKey, string directory, double startSeconds)
    {
        Id = id;
        UserId = userId;
        DeviceKey = deviceKey;
        Directory = directory;
        StartSeconds = startSeconds;
    }

    public string Id { get; }

    public Guid UserId { get; }

    public string DeviceKey { get; }

    public string Directory { get; }

    /// <summary>Where in the title the stream's first frame is, in seconds from the start of the file.</summary>
    public double StartSeconds { get; }

    /// <summary>True when one of the job's ffmpeg runs gave up, so the stream can't be played.</summary>
    public bool AnyFailed
    {
        get
        {
            lock (_processes)
            {
                return _processes.Any(p => p.HasExited && p.ExitCode != 0);
            }
        }
    }

    public void Add(Process process)
    {
        lock (_processes)
        {
            _processes.Add(process);
        }
    }

    public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastTouch));

    public void Touch() => Interlocked.Exchange(ref _lastTouch, Environment.TickCount64);

    public void AddError(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_errors)
        {
            _errors.Enqueue(line);
            while (_errors.Count > 20)
            {
                _errors.Dequeue();
            }
        }
    }

    public string ErrorText()
    {
        lock (_errors)
        {
            return string.Join('\n', _errors);
        }
    }

    public void Kill()
    {
        List<Process> processes;
        lock (_processes)
        {
            processes = _processes.ToList();
            _processes.Clear();
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
