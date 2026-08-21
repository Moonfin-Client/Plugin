using System.IO.Compression;

namespace Moonfin.Server.Tests;

/// <summary>
/// Shared helper for building tiny on-disk ZIP fixtures. Several test classes need a real ZIP
/// file (not just a MemoryStream) because the production code under test opens archives via
/// <see cref="ZipFile"/> APIs that require a seekable file on disk; this factors out the
/// identical entry-writing loop that used to be duplicated per test class.
/// </summary>
internal static class ZipFixtures
{
    /// <summary>Writes a ZIP archive under <paramref name="directory"/> containing the given entries.</summary>
    public static string WriteZip(string directory, string name, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(directory, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, data) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(data);
        }

        return path;
    }
}
