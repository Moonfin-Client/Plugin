using System;
using System.Collections.Generic;
using System.Linq;

namespace Moonfin.Seasonal;

/// <summary>A holiday whose window covers the date asked about.</summary>
public sealed class ActiveHoliday
{
    public ActiveHoliday(SeasonalHoliday holiday, DateTime start, DateTime end)
    {
        Holiday = holiday;
        Start = start;
        End = end;
    }

    public SeasonalHoliday Holiday { get; }

    public DateTime Start { get; }

    public DateTime End { get; }

    public int LengthDays => (End - Start).Days + 1;
}

public static class SeasonalSelector
{
    /// <summary>
    /// The holidays active on <paramref name="date"/> for a viewer in <paramref name="country"/>,
    /// shortest window first so Thanksgiving beats the month of Halloween around it. Hidden
    /// ids are left out. A null country matches only the windows open to everyone.
    /// </summary>
    public static List<ActiveHoliday> Active(
        IEnumerable<SeasonalHoliday> holidays,
        DateTime date,
        string? country,
        IEnumerable<string>? hidden = null)
    {
        var hiddenIds = new HashSet<string>(hidden ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var active = new List<ActiveHoliday>();
        foreach (var holiday in holidays)
        {
            if (hiddenIds.Contains(holiday.Id))
            {
                continue;
            }

            foreach (var window in holiday.Windows)
            {
                if (!window.AppliesTo(country))
                {
                    continue;
                }

                if (SeasonalCalendar.TryActive(window, date, out var start, out var end))
                {
                    active.Add(new ActiveHoliday(holiday, start, end));
                    break;
                }
            }
        }

        return active
            .OrderBy(a => a.LengthDays)
            .ThenBy(a => a.Holiday.Id, StringComparer.Ordinal)
            .ToList();
    }
}
