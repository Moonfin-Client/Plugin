using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class GameBackendOverrideServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-backend-overrides-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetAsync_PersistsPerUserByGameKey_AndClearsWithNull()
    {
        var user = Guid.NewGuid();
        var service = new GameBackendOverrideService(_root);

        await service.SetAsync(user, "game-key", "emulatorjs");

        Assert.Equal("emulatorjs", await service.GetAsync(user, "game-key"));
        Assert.Null(await service.GetAsync(Guid.NewGuid(), "game-key"));

        await service.SetAsync(user, "game-key", null);

        Assert.Null(await service.GetAsync(user, "game-key"));
    }

    [Fact]
    public async Task SetAsync_DoesNotLeaveTemporaryFile_WhenTheFinalWriteFails()
    {
        var user = Guid.NewGuid();
        var service = new GameBackendOverrideService(_root);

        // Same failure-mode reproduction as ArcadeCoreOverrideServiceTests: pre-creating a
        // directory at the destination path makes File.Move throw reliably, exercising the
        // finally-cleanup path a mid-serialization exception would.
        var destination = Path.Combine(_root, $"{user:N}.json");
        Directory.CreateDirectory(destination);

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.SetAsync(user, "game-key", "emulatorjs"));

        Assert.False(File.Exists(destination + ".tmp"), "The .tmp file must not survive a failed write.");
    }

    // Regression test for the ArcadeCoreOverrideService/GameBackendOverrideService dedup (both now
    // wrap the shared PerUserPreferenceStore): a preference file written by the ORIGINAL,
    // pre-refactor GameBackendOverrideService (a plain indented JSON object mapping key -> value)
    // must still load correctly through the new wrapper, so existing users' stored backend
    // choices are not silently dropped by this refactor.
    [Fact]
    public async Task GetAsync_ReadsAPreferenceFileWrittenInTheOriginalPreRefactorFormat()
    {
        var user = Guid.NewGuid();
        Directory.CreateDirectory(_root);

        // Exactly the on-disk shape the original (pre-dedup) GameBackendOverrideService.SetAsync
        // produced: WriteIndented JSON, one property per game key.
        var legacyJson = "{\n  \"game-key\": \"emulatorjs\"\n}";
        await File.WriteAllTextAsync(Path.Combine(_root, $"{user:N}.json"), legacyJson);

        var service = new GameBackendOverrideService(_root);

        Assert.Equal("emulatorjs", await service.GetAsync(user, "game-key"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
