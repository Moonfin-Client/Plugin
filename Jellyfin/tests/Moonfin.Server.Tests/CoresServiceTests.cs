using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class CoresServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-cores-{Guid.NewGuid():N}");

    [Fact]
    public void IsDataInstalled_RejectsLoaderOnlyDirectory()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "loader.js"), string.Empty);

        Assert.False(CoresService.IsDataInstalled(_root));
    }

    [Fact]
    public void IsDataInstalled_AcceptsCompleteCommonRuntime()
    {
        Directory.CreateDirectory(_root);
        foreach (var fileName in new[] { "loader.js", "emulator.min.js", "emulator.min.css" })
        {
            File.WriteAllText(Path.Combine(_root, fileName), string.Empty);
        }

        Assert.True(CoresService.IsDataInstalled(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
