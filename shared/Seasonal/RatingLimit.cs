using System;
using System.Collections.Generic;

namespace Moonfin.Seasonal;

/// <summary>
/// A user's parental limit, as the server states it: the highest rating value they may see
/// and the server's own table of rating names to values. A title the table doesn't know, or
/// one with no rating at all, is hidden whenever a limit exists.
/// </summary>
public sealed class RatingLimit
{
    public static readonly RatingLimit None = new RatingLimit(null, new Dictionary<string, int>(), null);

    private readonly int? _max;
    private readonly Dictionary<string, int> _values;
    private readonly string? _country;

    public RatingLimit(int? maxValue, IEnumerable<KeyValuePair<string, int>> ratingValues, string? countryCode)
    {
        _max = maxValue;
        _country = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode!.Trim();
        _values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ratingValues)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                _values[pair.Key.Trim()] = pair.Value;
            }
        }
    }

    public bool HasLimit => _max.HasValue;

    /// <summary>
    /// The table value for a certification, trying it as written, without a leading country
    /// prefix ("US-PG-13"), and with the server's country prefixed.
    /// </summary>
    public int? ValueOf(string? certification)
    {
        if (string.IsNullOrWhiteSpace(certification))
        {
            return null;
        }

        var name = certification!.Trim();
        if (_values.TryGetValue(name, out var value))
        {
            return value;
        }

        if (name.Length > 3 && name[2] == '-' && _values.TryGetValue(name.Substring(3), out value))
        {
            return value;
        }

        if (_country != null && _values.TryGetValue(_country + "-" + name, out value))
        {
            return value;
        }

        return null;
    }

    public bool IsAllowed(string? certification)
    {
        if (!_max.HasValue)
        {
            return true;
        }

        var value = ValueOf(certification);
        return value.HasValue && value.Value <= _max.Value;
    }
}
