using System;
using System.Collections.Generic;
using System.Text;

namespace Moonfin.Seasonal;

/// <summary>
/// A shuffle that comes out the same for one user all day on every device, and different
/// the next day. It carries its own generator so the order doesn't depend on the runtime's
/// Random.
/// </summary>
public static class DailyShuffle
{
    public static uint Seed(string userKey, DateTime date)
    {
        var text = userKey + "|" + date.ToString("yyyy-MM-dd");
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash == 0 ? 1u : hash;
    }

    public static List<T> Shuffle<T>(IReadOnlyList<T> items, uint seed)
    {
        var result = new List<T>(items);
        var state = seed;
        for (var i = result.Count - 1; i > 0; i--)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            var j = (int)(state % (uint)(i + 1));
            var swap = result[i];
            result[i] = result[j];
            result[j] = swap;
        }

        return result;
    }
}
