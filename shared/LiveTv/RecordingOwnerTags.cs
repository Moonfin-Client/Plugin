using System;
using System.Collections.Generic;
using System.Linq;

namespace Moonfin.LiveTv;

/// <summary>
/// The "Recorded by name" tags the recording owners feature stamps on recordings. This file has no
/// other dependencies, so the smart search helper the Go hosts build can compile it on its own.
/// </summary>
public static class RecordingOwnerTags
{
    /// <summary>The fixed, non-localized start of each owner tag, so a rebuild can parse it.</summary>
    public const string Prefix = "Recorded by ";

    /// <summary>Whether a tag is one of the owner tags this feature stamps on recordings.</summary>
    public static bool IsOwnerTag(string? tag) =>
        tag != null && tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An item's tags without the owner tags. Those say who recorded it, not what it is, so
    /// similar-items scoring and smart search leave them out.
    /// </summary>
    public static IReadOnlyList<string> Without(IEnumerable<string>? tags) =>
        tags == null ? Array.Empty<string>() : tags.Where(t => !IsOwnerTag(t)).ToList();
}
