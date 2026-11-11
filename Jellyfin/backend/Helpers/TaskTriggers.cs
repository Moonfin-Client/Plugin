using System.Reflection;
using MediaBrowser.Model.Tasks;

namespace Moonfin.Server.Helpers;

/// <summary>
/// Default triggers for Moonfin's scheduled tasks. Jellyfin 10.11 turned
/// TaskTriggerInfo.Type from a string into an enum, so a direct assignment only binds on
/// the version it was compiled against. Reflection keeps one build working on 10.10 to 12.
/// </summary>
internal static class TaskTriggers
{
    // The 10.10 string constants and the 10.11 enum members share these names.
    private const string StartupTrigger = "StartupTrigger";
    private const string DailyTrigger = "DailyTrigger";
    private const string IntervalTrigger = "IntervalTrigger";

    private static readonly PropertyInfo? _typeProperty =
        typeof(TaskTriggerInfo).GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);

    public static TaskTriggerInfo Startup() => Build(StartupTrigger, null);

    public static TaskTriggerInfo Daily(TimeSpan timeOfDay) => Build(DailyTrigger, timeOfDay.Ticks);

    public static TaskTriggerInfo Interval(TimeSpan interval)
    {
        var trigger = Build(IntervalTrigger, null);
        trigger.IntervalTicks = interval.Ticks;
        return trigger;
    }

    private static TaskTriggerInfo Build(string triggerName, long? timeOfDayTicks)
    {
        var trigger = new TaskTriggerInfo();
        if (timeOfDayTicks.HasValue)
        {
            trigger.TimeOfDayTicks = timeOfDayTicks;
        }

        if (_typeProperty?.CanWrite == true)
        {
            var value = CoerceTriggerName(triggerName, _typeProperty.PropertyType);
            if (value != null)
            {
                _typeProperty.SetValue(trigger, value);
            }
        }

        return trigger;
    }

    // An unrecognised shape returns null, which leaves the trigger untyped and lets the
    // server fall back to its own default.
    private static object? CoerceTriggerName(string triggerName, Type propertyType)
    {
        if (propertyType == typeof(string))
        {
            return triggerName;
        }

        if (propertyType.IsEnum && Enum.TryParse(propertyType, triggerName, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
