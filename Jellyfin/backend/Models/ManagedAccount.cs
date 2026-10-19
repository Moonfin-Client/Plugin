namespace Moonfin.Server;

/// <summary>
/// Who manages one account: the user ids (no dashes) of the administrators set over it. A list
/// of records rather than a dictionary because the config is serialized as XML.
/// </summary>
public class ManagedAccount
{
    public string UserId { get; set; } = string.Empty;

    public List<string> ManagedBy { get; set; } = new();
}
