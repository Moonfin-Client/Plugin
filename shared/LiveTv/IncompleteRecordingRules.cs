using System;
using System.Collections.Generic;
using System.Linq;

namespace Moonfin.LiveTv;

/// <summary>The pure decisions behind incomplete recordings, kept apart so they can be tested alone.</summary>
public static class IncompleteRecordingRules
{
    public const string Tag = "DVRIncompleteRecording";

    public const int DefaultThresholdPercent = 90;

    public const int MinThresholdPercent = 50;

    public const int MaxThresholdPercent = 100;

    /// <summary>A later showing is scheduled at most this many times before the request gives up.</summary>
    public const int MaxFailedAttempts = 2;

    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest gap between two samples that is still credited when the file grew across it.
    /// A longer gap is unobserved time: growth across it shows something was written, not when.
    /// </summary>
    public static readonly TimeSpan MaxCreditedGap = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan SafetyNetInterval = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan RequestSearchFallback = TimeSpan.FromDays(1);

    public static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(60);

    public static readonly TimeSpan AttachWindow = TimeSpan.FromHours(24);

    public static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(7);

    public static readonly TimeSpan ClosedRequestRetention = TimeSpan.FromDays(30);

    /// <summary>A dismissed record is kept this long after judging, past the attach window, then dropped.</summary>
    public static readonly TimeSpan DismissedRecordRetention = TimeSpan.FromDays(7);

    /// <summary>An attached record whose item is still in the library is dropped after this.</summary>
    public static readonly TimeSpan RecordRetention = TimeSpan.FromDays(365);

    /// <summary>
    /// A recording still being written this long after its window closed (someone extended it) is
    /// judged anyway.
    /// </summary>
    public static readonly TimeSpan OverrunLimit = TimeSpan.FromMinutes(30);

    public static int ClampThreshold(int percent) =>
        Math.Clamp(percent, MinThresholdPercent, MaxThresholdPercent);

    /// <summary>Incomplete when fewer minutes were written than the threshold share of the padded schedule.</summary>
    public static bool IsIncomplete(double recordedMinutes, double expectedMinutes, int thresholdPercent)
    {
        if (expectedMinutes <= 0)
        {
            return false;
        }

        return recordedMinutes < ClampThreshold(thresholdPercent) / 100.0 * expectedMinutes;
    }

    /// <summary>
    /// Judges a recording by its best single file, since only one playable file is a complete
    /// copy, and files are never added together. Complete when one file's estimated coverage reaches
    /// the threshold. Incomplete when no file could reach it even if all of its unobserved time
    /// had been recorded. Anything between is unknown and isn't flagged.
    /// </summary>
    public static string Classify(IEnumerable<RecordingFragment> fragments, double expectedMinutes, int thresholdPercent)
    {
        var list = fragments.ToList();
        var bestCovered = list.Count == 0 ? 0 : list.Max(f => f.CoveredSeconds) / 60;
        var bestPossible = list.Count == 0 ? 0 : list.Max(f => f.CoveredSeconds + f.UnknownSeconds) / 60;

        if (!IsIncomplete(bestCovered, expectedMinutes, thresholdPercent))
        {
            return RecordingVerdict.Complete;
        }

        return IsIncomplete(bestPossible, expectedMinutes, thresholdPercent)
            ? RecordingVerdict.Incomplete
            : RecordingVerdict.Unknown;
    }

    /// <summary>
    /// The earliest airing of the episode or movie that starts after now, on any channel, preferring HD when
    /// two start together. This is how the server itself picks among duplicate showings.
    /// </summary>
    public static GuideProgram? ChooseShowing(EpisodeKey episode, IEnumerable<GuideProgram> programs, DateTime now)
    {
        return programs
            .Where(p => p.StartDate > now)
            .Where(p => p.Key is { } key && episode.Matches(key))
            .OrderBy(p => p.StartDate)
            .ThenByDescending(p => p.IsHD)
            .FirstOrDefault();
    }

    /// <summary>Collapses a request's internal state to what clients show.</summary>
    public static string? DisplayState(CompleteShowingRequest? request) => request?.State switch
    {
        CompleteShowingState.Scheduled => "scheduled",
        CompleteShowingState.Done => "recorded",
        CompleteShowingState.Waiting or CompleteShowingState.GaveUp or CompleteShowingState.NotFound => "notFound",
        _ => null,
    };
}
