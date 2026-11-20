using Moonfin.LiveTv;
using Moonfin.LiveTv.Tests;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The owners store through Jellyfin's real state file, whose read promotes a .bak over a main
/// file it can't use: a newer plugin's file must never be replaced that way.
/// </summary>
public sealed class RecordingOwnershipStoreFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonfin-owners-atomic-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp folder left.
        }
    }

    [Fact]
    public async Task FutureVersionStore_UnparseableByThisVersion_NeitherPromotedNorRebuilt()
    {
        var jordan = Guid.NewGuid();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, RecordingOwnershipService.StoreFileName);
        File.WriteAllText(path, "{\"Version\":2,\"Timers\":{\"api-p1\":{\"owners\":[\"" + jordan + "\"]}}}");
        File.WriteAllText(AtomicFile.BackupPath(path), "{\"Version\":1,\"Timers\":{\"api-p1\":[\"" + jordan + "\"]}}");
        var main = File.ReadAllBytes(path);
        var backup = File.ReadAllBytes(AtomicFile.BackupPath(path));

        var host = new FakeRecordingHost();
        host.Users.Add(new ServerUser(jordan, "Jordan", null, false));
        var tagged = host.AddItem("/rec/a.ts");
        tagged.Tags = new[] { "Recorded by Jordan" };
        using var service = new RecordingOwnershipService(host, NoRecordingLog.Instance, new JellyfinStateFile(), () => DateTime.UtcNow, path, () => true);

        Assert.Empty((await service.GetMineAsync(jordan, CancellationToken.None)).TimerIds);
        await service.ClaimAsync(OwnedKind.Item, tagged.Id.ToString(), jordan, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(main, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(AtomicFile.BackupPath(path)));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
