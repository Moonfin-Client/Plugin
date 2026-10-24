using System;
using System.Text.RegularExpressions;

namespace Moonfin.LiveTv;

/// <summary>Where the incomplete recordings feature logs, so each server can use its own logger.</summary>
public interface IRecordingLog
{
    void Info(string template, params object?[] args);

    void Warn(Exception? exception, string template, params object?[] args);

    void Error(Exception? exception, string template, params object?[] args);
}

/// <summary>Fills a structured log template for a logger that only takes plain text.</summary>
public static class RecordingLogText
{
    private static readonly Regex Placeholder = new(@"\{[^{}]+\}", RegexOptions.CultureInvariant);

    public static string Format(string template, object?[] args)
    {
        var next = 0;
        return Placeholder.Replace(template, match =>
        {
            if (next >= args.Length)
            {
                return match.Value;
            }

            var format = match.Value.IndexOf(':') is var colon and > 0 ? match.Value.Substring(colon + 1, match.Value.Length - colon - 2) : null;
            var value = args[next++];
            return value is IFormattable formattable && format != null
                ? formattable.ToString(format, System.Globalization.CultureInfo.InvariantCulture)
                : value?.ToString() ?? string.Empty;
        });
    }
}
