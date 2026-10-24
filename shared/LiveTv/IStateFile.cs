using System;

namespace Moonfin.LiveTv;

/// <summary>Reads and writes the feature's state file through each server's own safe-write helper.</summary>
public interface IStateFile
{
    /// <summary>The parsed file, falling back to its backup copy, or null when neither is usable.</summary>
    T? Read<T>(string path, Func<string, T?> parse)
        where T : class;

    void Write(string path, string contents);
}
