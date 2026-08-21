using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class ArcadeCoreOverrideServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-arcade-overrides-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetAsync_PersistsPerUserByContentFingerprint_AndClearsWithNull()
    {
        var user = Guid.NewGuid();
        var service = new ArcadeCoreOverrideService(_root);

        await service.SetAsync(user, "content-fingerprint", "mame");

        Assert.Equal("mame", await service.GetAsync(user, "content-fingerprint"));
        Assert.Null(await service.GetAsync(Guid.NewGuid(), "content-fingerprint"));

        await service.SetAsync(user, "content-fingerprint", null);

        Assert.Null(await service.GetAsync(user, "content-fingerprint"));
    }

    [Fact]
    public async Task SetAsync_DoesNotLeaveTemporaryFile_WhenTheFinalWriteFails()
    {
        var user = Guid.NewGuid();
        var service = new ArcadeCoreOverrideService(_root);

        // Force the same failure mode the fix guards against: an exception thrown after the
        // ".tmp" file is created and written, but before it replaces the real file. Pre-creating
        // a directory at the destination path makes File.Move throw reliably (a directory can
        // never be overwritten by a file move), exercising the same finally-cleanup path a
        // mid-serialization exception would.
        var destination = Path.Combine(_root, $"{user:N}.json");
        Directory.CreateDirectory(destination);

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.SetAsync(user, "content-fingerprint", "mame"));

        Assert.False(File.Exists(destination + ".tmp"), "The .tmp file must not survive a failed write.");
    }

    // Regression test for the ArcadeCoreOverrideService/GameBackendOverrideService dedup (both now
    // wrap the shared PerUserPreferenceStore): a preference file written by the ORIGINAL,
    // pre-refactor ArcadeCoreOverrideService (a plain indented JSON object mapping key -> value)
    // must still load correctly through the new wrapper, so existing users' stored core choices
    // are not silently dropped by this refactor.
    [Fact]
    public async Task GetAsync_ReadsAPreferenceFileWrittenInTheOriginalPreRefactorFormat()
    {
        var user = Guid.NewGuid();
        Directory.CreateDirectory(_root);

        // Exactly the on-disk shape the original (pre-dedup) ArcadeCoreOverrideService.SetAsync
        // produced: WriteIndented JSON, one property per content key.
        var legacyJson = "{\n  \"content-fingerprint\": \"mame\"\n}";
        await File.WriteAllTextAsync(Path.Combine(_root, $"{user:N}.json"), legacyJson);

        var service = new ArcadeCoreOverrideService(_root);

        Assert.Equal("mame", await service.GetAsync(user, "content-fingerprint"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
