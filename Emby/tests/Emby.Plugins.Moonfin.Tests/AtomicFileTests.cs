using System.Reflection;
using System.Text;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Model.Logging;
using Moonfin.Upgrades;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Writing user data in place leaves a truncated file when the process dies partway through, which
/// is what corrupts stored settings. AtomicFile is the guard, and it is hand-copied from the
/// Jellyfin plugin.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"moonfin-emby-atomic-{Guid.NewGuid():N}");

    private string NewPath()
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, $"{Guid.NewGuid():N}.json");
    }

    /// <summary>
    /// Stands in for a real parser. It has to reject a half-written document, since that is how a
    /// truncated file gets caught, so a closing brace is required rather than just an opening one.
    /// </summary>
    private static string? Parse(string text) =>
        text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal)
            ? text
            : null;

    [Fact]
    public void BackupPath_SitsBesideTheTarget()
    {
        Assert.Equal("/data/user.json.bak", AtomicFile.BackupPath("/data/user.json"));
    }

    [Fact]
    public void WriteAllText_CreatesAFileThatWasNotThere()
    {
        var path = NewPath();

        AtomicFile.WriteAllText(path, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        Assert.False(File.Exists(AtomicFile.BackupPath(path)));
    }

    [Fact]
    public void WriteAllText_KeepsThePreviousContentsAsABackup()
    {
        var path = NewPath();
        AtomicFile.WriteAllText(path, "{\"v\":1}");

        AtomicFile.WriteAllText(path, "{\"v\":2}");

        Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        Assert.Equal("{\"v\":1}", File.ReadAllText(AtomicFile.BackupPath(path)));
    }

    [Fact]
    public void WriteAllText_LeavesNoTempFileBehind()
    {
        var path = NewPath();

        AtomicFile.WriteAllText(path, "{}");
        AtomicFile.WriteAllText(path, "{}");

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void WriteAllText_WritesUtf8WithNoByteOrderMark()
    {
        // File.WriteAllText produced no BOM, so files written before this helper stay readable.
        var path = NewPath();

        AtomicFile.WriteAllText(path, "{}");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Encoding.UTF8.GetBytes("{}"), bytes);
    }

    [Fact]
    public void WriteAllText_RoundTripsTextOutsideAscii()
    {
        var path = NewPath();
        var contents = "{\"name\":\"café " + char.ConvertFromUtf32(0x1F680) + "\"}";

        AtomicFile.WriteAllText(path, contents);

        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void ReadWithRecovery_ReadsTheMainFileWhenItIsGood()
    {
        var path = NewPath();
        AtomicFile.WriteAllText(path, "{\"v\":1}");

        Assert.Equal("{\"v\":1}", AtomicFile.ReadWithRecovery(path, Parse));
    }

    [Fact]
    public void ReadWithRecovery_FallsBackToTheBackupWhenTheMainFileIsGone()
    {
        var path = NewPath();
        File.WriteAllText(AtomicFile.BackupPath(path), "{\"v\":1}");

        Assert.Equal("{\"v\":1}", AtomicFile.ReadWithRecovery(path, Parse));
    }

    [Fact]
    public void ReadWithRecovery_FallsBackWhenTheMainFileIsHalfWritten()
    {
        var path = NewPath();
        AtomicFile.WriteAllText(path, "{\"v\":1}");
        // The second write is what produces the backup, the first has nothing to back up.
        AtomicFile.WriteAllText(path, "{\"v\":2}");
        File.WriteAllText(path, "{\"v\":");

        Assert.Equal("{\"v\":1}", AtomicFile.ReadWithRecovery(path, Parse));
    }

    [Fact]
    public void ReadWithRecovery_RestoresTheMainFileFromTheBackupItUsed()
    {
        var path = NewPath();
        AtomicFile.WriteAllText(path, "{\"v\":1}");
        AtomicFile.WriteAllText(path, "{\"v\":2}");
        File.WriteAllText(path, "broken");

        AtomicFile.ReadWithRecovery(path, Parse);

        Assert.Equal("{\"v\":1}", File.ReadAllText(path));
        Assert.True(File.Exists(AtomicFile.BackupPath(path)), "the backup stays put after promotion");
    }

    [Fact]
    public void ReadWithRecovery_AnswersNullWhenNeitherCopyIsUsable()
    {
        var path = NewPath();
        File.WriteAllText(path, "broken");
        File.WriteAllText(AtomicFile.BackupPath(path), "also broken");

        Assert.Null(AtomicFile.ReadWithRecovery(path, Parse));
    }

    [Fact]
    public void ReadWithRecovery_AnswersNullWhenNothingIsThere()
    {
        Assert.Null(AtomicFile.ReadWithRecovery(NewPath(), Parse));
    }

    [Fact]
    public void ReadWithRecovery_TreatsAThrowingParseAsAFailedRead()
    {
        var path = NewPath();
        File.WriteAllText(path, "boom");
        File.WriteAllText(AtomicFile.BackupPath(path), "{\"v\":1}");

        var read = AtomicFile.ReadWithRecovery<string>(
            path, text => text == "boom" ? throw new InvalidOperationException() : Parse(text));

        Assert.Equal("{\"v\":1}", read);
    }

    [Fact]
    public void DeleteWithSidecars_RemovesTheFileAndBothSidecars()
    {
        var path = NewPath();
        File.WriteAllText(path, "{}");
        File.WriteAllText(AtomicFile.BackupPath(path), "{}");
        File.WriteAllText(path + ".tmp", "{}");

        AtomicFile.DeleteWithSidecars(path);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(AtomicFile.BackupPath(path)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void DeleteWithSidecars_ShrugsOffFilesThatAreNotThere()
    {
        Assert.Null(Record.Exception(() => AtomicFile.DeleteWithSidecars(NewPath())));
    }

    [Fact]
    public void ReadWithRecovery_DistinguishesInaccessibleParentFromMissingFile()
    {
        var missingPath = NewPath();
        Assert.Null(AtomicFile.ReadWithRecovery(missingPath, Parse, out var missingError));
        Assert.Null(missingError);

        var blockedParent = NewPath();
        File.WriteAllText(blockedParent, "not-a-directory");
        var blockedPath = Path.Combine(blockedParent, "upgrade-dates.json");
        Assert.Null(AtomicFile.ReadWithRecovery(blockedPath, Parse, out var accessError));
        Assert.NotNull(accessError);
        Assert.ThrowsAny<IOException>(() => new EmbyStateFile().Read(blockedPath, Parse));
    }

    [Fact]
    public void ReadWithRecovery_PreservesAccessErrorWhenPrimaryFailsEvenIfBackupSucceeds()
    {
        var path = NewPath();
        File.WriteAllText(path, "{\"v\":2}");
        File.WriteAllText(AtomicFile.BackupPath(path), "{\"v\":1}");

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Primary has access error (sharing violation), but backup is intact.
            var recovered = AtomicFile.ReadWithRecovery(path, Parse, out var accessError);
            Assert.Equal("{\"v\":1}", recovered);
            Assert.NotNull(accessError);
            Assert.IsAssignableFrom<IOException>(accessError);

            // Two-argument overload returns the backup copy.
            Assert.Equal("{\"v\":1}", AtomicFile.ReadWithRecovery(path, Parse));

            // State-file adapter must reject the access error and throw.
            Assert.ThrowsAny<IOException>(() => new EmbyStateFile().Read(path, Parse));
        }

        // The primary file should not have been overwritten or promoted.
        Assert.Equal("{\"v\":2}", File.ReadAllText(path));
    }

    [Fact]
    public void FilePinStore_RejectsUnreadablePrimaryWithReadableBackup()
    {
        var pinDir = Path.Combine(_root, "pins");
        Directory.CreateDirectory(pinDir);
        var userId = Guid.NewGuid();
        var primaryPath = Path.Combine(pinDir, userId.ToString("N") + ".json");
        var backupPath = AtomicFile.BackupPath(primaryPath);

        File.WriteAllText(primaryPath, "{\"Hash\":\"new-hash\",\"Salt\":\"new-salt\",\"Iterations\":100000}");
        File.WriteAllText(backupPath, "{\"Hash\":\"old-hash\",\"Salt\":\"old-salt\",\"Iterations\":100000}");

        var pinStore = new FilePinStore(pinDir, NoOpLogger.Create());

        using (var lockStream = new FileStream(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => pinStore.Load(userId));
        }

        // Verifying primary was not overwritten with old hash
        Assert.Contains("new-hash", File.ReadAllText(primaryPath));
    }

    [Fact]
    public void MediaUpgradeLedger_RejectsUnreadablePrimaryWithReadableBackup()
    {
        var path = NewPath();
        var backupPath = AtomicFile.BackupPath(path);
        File.WriteAllText(path, "[{\"ItemType\":\"Movie\",\"Name\":\"New Movie\"}]");
        File.WriteAllText(backupPath, "[{\"ItemType\":\"Movie\",\"Name\":\"Old Movie\"}]");

        var stateFile = new EmbyStateFile();
        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => stateFile.Read<List<MediaUpgradeRecord>>(path, json => new List<MediaUpgradeRecord>()));
            var ledger = new MediaUpgradeLedger(path, stateFile);
            Assert.Equal(0, ledger.Count);
        }

        Assert.Contains("New Movie", File.ReadAllText(path));
    }

    private class NoOpLogger : DispatchProxy
    {
        public static ILogger Create() => Create<ILogger, NoOpLogger>();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
