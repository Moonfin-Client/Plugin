namespace Moonfin.Server.Services;

/// <summary>
/// Which administrators manage which accounts. An account can have several, so a child with
/// parents in separate homes shows up for both. The records are kept as they were set, and
/// who still exists and is still an administrator is looked at when they are read, so
/// deleting a user or taking away their role never leaves a stale entry giving anything.
/// </summary>
public static class ManagedAccounts
{
    /// <summary>Guards the shared config list. Take it around Set, SaveConfiguration and reads.</summary>
    public static readonly object Gate = new();

    /// <summary>The most administrators one account can have.</summary>
    public const int MaxManagers = 20;

    /// <summary>
    /// Replaces who manages <paramref name="userId"/>. An empty list removes the record. An
    /// account is never its own manager, and a manager listed twice counts once.
    /// </summary>
    public static void Set(List<ManagedAccount> records, Guid userId, IEnumerable<Guid> managers)
    {
        var id = Format(userId);
        records.RemoveAll(r => Matches(r.UserId, userId));

        var managedBy = managers
            .Where(m => m != Guid.Empty && m != userId)
            .Select(Format)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (managedBy.Count > 0)
        {
            records.Add(new ManagedAccount { UserId = id, ManagedBy = managedBy });
        }
    }

    /// <summary>
    /// Why a list of managers can't be set on <paramref name="userId"/>, or null when it can.
    /// Every manager has to be an administrator that exists, and the account has to exist.
    /// </summary>
    public static string? Problem(IReadOnlyList<DirectoryUser> directory, Guid userId, IReadOnlyCollection<Guid> managers)
    {
        if (directory.All(u => u.Id != userId))
        {
            return "That user does not exist";
        }

        if (managers.Count > MaxManagers)
        {
            return $"An account can have at most {MaxManagers} managers";
        }

        foreach (var manager in managers)
        {
            if (manager == userId)
            {
                return "An account cannot manage itself";
            }

            var user = directory.FirstOrDefault(u => u.Id == manager);
            if (user == null || !user.IsAdministrator)
            {
                return "Only administrators can manage accounts";
            }
        }

        return null;
    }

    /// <summary>The administrators set to manage the account, as they were set.</summary>
    public static IReadOnlyList<Guid> ManagersOf(IEnumerable<ManagedAccount> records, Guid userId)
    {
        var record = records.FirstOrDefault(r => Matches(r.UserId, userId));
        return record == null ? [] : record.ManagedBy.Select(Parse).Where(g => g != Guid.Empty).ToList();
    }

    /// <summary>
    /// The accounts <paramref name="adminId"/> manages: those that still exist, while the
    /// caller is still an administrator.
    /// </summary>
    public static IReadOnlyList<DirectoryUser> ManagedBy(
        IEnumerable<ManagedAccount> records,
        IReadOnlyList<DirectoryUser> directory,
        Guid adminId)
    {
        var admin = directory.FirstOrDefault(u => u.Id == adminId);
        if (admin == null || !admin.IsAdministrator)
        {
            return [];
        }

        var ids = records
            .Where(r => r.ManagedBy.Any(m => Matches(m, adminId)))
            .Select(r => Parse(r.UserId))
            .ToHashSet();
        return directory.Where(u => ids.Contains(u.Id) && u.Id != adminId).ToList();
    }

    private static string Format(Guid id) => id.ToString("N");

    private static Guid Parse(string? value) => Guid.TryParse(value, out var id) ? id : Guid.Empty;

    private static bool Matches(string? stored, Guid id) => Parse(stored) == id;
}
