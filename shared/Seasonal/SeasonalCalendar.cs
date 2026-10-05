using System;
using System.Collections.Generic;
using System.Globalization;

namespace Moonfin.Seasonal;

/// <summary>Turns the rules file's date rules into days of a given year.</summary>
public static class SeasonalCalendar
{
    /// <summary>Western Easter Sunday by the anonymous Gregorian computus.</summary>
    public static DateTime Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }

    public static DateTime NthWeekday(int year, int month, DayOfWeek weekday, int n)
    {
        var first = new DateTime(year, month, 1);
        var toWeekday = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(toWeekday + 7 * (n - 1));
    }

    /// <summary>The rule's day in <paramref name="year"/>, or null when a dates rule has no entry for it.</summary>
    public static DateTime? Resolve(DateRule rule, int year, IReadOnlyDictionary<string, string>? dates = null)
    {
        DateTime day;
        switch (rule.Kind)
        {
            case "easter":
                day = Easter(year);
                break;
            case "nthWeekday":
                day = NthWeekday(year, rule.Month, ParseWeekday(rule.Weekday), rule.N);
                break;
            case "dates":
                if (dates == null || !dates.TryGetValue(year.ToString(CultureInfo.InvariantCulture), out var text))
                {
                    return null;
                }

                day = DateTime.ParseExact(year + "-" + text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            default:
                day = new DateTime(year, rule.Month, rule.Day);
                break;
        }

        return day.AddDays(rule.OffsetDays);
    }

    /// <summary>
    /// The window that starts in <paramref name="year"/>, or null when its table has no entry
    /// for that year. An end that lands before the start belongs to the next year, which is
    /// how Dec 27 to Jan 1 works.
    /// </summary>
    public static (DateTime Start, DateTime End)? Window(SeasonalWindow window, int year)
    {
        var start = Resolve(window.Start, year, window.Dates);
        var end = Resolve(window.End, year, window.Dates);
        if (start == null || end == null)
        {
            return null;
        }

        if (end < start)
        {
            end = Resolve(window.End, year + 1, window.Dates);
            if (end == null)
            {
                return null;
            }
        }

        return (start.Value, end.Value);
    }

    /// <summary>Whether <paramref name="date"/> falls inside the window, whichever year it started in.</summary>
    public static bool TryActive(SeasonalWindow window, DateTime date, out DateTime start, out DateTime end)
    {
        var day = date.Date;
        for (var year = day.Year - 1; year <= day.Year + 1; year++)
        {
            var range = Window(window, year);
            if (range != null && day >= range.Value.Start && day <= range.Value.End)
            {
                start = range.Value.Start;
                end = range.Value.End;
                return true;
            }
        }

        start = default;
        end = default;
        return false;
    }

    private static DayOfWeek ParseWeekday(string? weekday)
    {
        if (Enum.TryParse(weekday, true, out DayOfWeek parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException("Unknown weekday '" + weekday + "' in the seasonal rules.");
    }
}
