using System;
using System.Collections.Generic;
using System.Linq;

namespace Moonfin.LiveTv;

/// <summary>Shared guide-shortcut rules for Jellyfin and Emby; storage stays backend-specific.</summary>
internal static class GuidePinRules
{
    internal const int MaxStoredKeys = 1000;

    private static bool ValidKey(string? key) =>
        key != null && key.Length <= 1024 &&
        ((key.StartsWith("group:", StringComparison.Ordinal) && key.Length > 6) ||
         (key.StartsWith("category:", StringComparison.Ordinal) && key.Length > 9));

    internal static bool ValidChange(string? action, string? filter, List<string>? filters) =>
        action switch
        {
            "add" or "remove" => ValidKey(filter),
            "initializeIfAbsent" => filters is { Count: <= MaxStoredKeys } && filters.All(ValidKey),
            _ => false,
        };

    // Only the global administrator list contributes managed guide shortcuts.
    internal static List<string> AdminPins(List<string>? global) =>
        global?.Where(ValidKey).Distinct().ToList() ?? new List<string>();

    internal static List<string> EffectivePins(
        List<string> defaults, List<string>? exclusions, List<string>? personal)
    {
        var pins = new List<string>();
        foreach (var pin in defaults)
            if (exclusions?.Contains(pin) != true && !pins.Contains(pin)) pins.Add(pin);
        foreach (var pin in personal ?? new List<string>())
            if (ValidKey(pin) && !pins.Contains(pin)) pins.Add(pin);
        return pins;
    }

    // Null means an existing personal list won the one-time initialization race.
    internal static (List<string> Personal, List<string> Exclusions)? Apply(
        string action, string? filter, List<string>? filters, List<string>? defaults,
        List<string>? personal, List<string>? exclusions)
    {
        if (action == "initializeIfAbsent" && personal != null) return null;
        personal = personal?.ToList() ?? new List<string>();
        exclusions = exclusions?.ToList() ?? new List<string>();
        if (action == "initializeIfAbsent")
        {
            foreach (var pin in filters!)
                if (!personal.Contains(pin)) personal.Add(pin);
            // A pre-plugin local list wins over today's defaults, not future ones.
            foreach (var pin in defaults ?? new List<string>())
                if (!personal.Contains(pin) && !exclusions.Contains(pin)) exclusions.Add(pin);
        }
        else if (action == "add")
        {
            if (!personal.Contains(filter!)) personal.Add(filter!);
            exclusions.RemoveAll(pin => pin == filter);
        }
        else
        {
            personal.RemoveAll(pin => pin == filter);
            if (defaults?.Contains(filter!) == true && !exclusions.Contains(filter!)) exclusions.Add(filter!);
        }
        if (personal.Count + exclusions.Count > MaxStoredKeys) throw new GuidePinLimitException();
        return (personal, exclusions);
    }
}

internal sealed class GuidePinLimitException : InvalidOperationException
{
    internal GuidePinLimitException() : base("A user can store at most 1,000 guide pins and exclusions in total.") { }
}
