using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class ManagedAccountsTests
{
    private static readonly Guid Mum = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Dad = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Child = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static IReadOnlyList<DirectoryUser> Directory(params DirectoryUser[] extra) =>
    [
        new DirectoryUser(Mum, "Mum", "tag-mum", true),
        new DirectoryUser(Dad, "Dad", null, true),
        new DirectoryUser(Child, "Child", "tag-child", false),
        new DirectoryUser(Other, "Other", null, false),
        .. extra,
    ];

    [Fact]
    public void ASetListIsReadBack()
    {
        var records = new List<ManagedAccount>();

        ManagedAccounts.Set(records, Child, [Mum, Dad]);

        Assert.Equal([Mum, Dad], ManagedAccounts.ManagersOf(records, Child));
        Assert.Equal([Child.ToString("N")], records.Select(r => r.UserId));
    }

    [Fact]
    public void SettingAgainReplacesTheList()
    {
        var records = new List<ManagedAccount>();
        ManagedAccounts.Set(records, Child, [Mum, Dad]);

        ManagedAccounts.Set(records, Child, [Dad]);

        Assert.Single(records);
        Assert.Equal([Dad], ManagedAccounts.ManagersOf(records, Child));
    }

    [Fact]
    public void AnEmptyListRemovesTheRecord()
    {
        var records = new List<ManagedAccount>();
        ManagedAccounts.Set(records, Child, [Mum]);

        ManagedAccounts.Set(records, Child, []);

        Assert.Empty(records);
        Assert.Empty(ManagedAccounts.ManagersOf(records, Child));
    }

    [Fact]
    public void AManagerListedTwiceCountsOnceAndAnAccountNeverManagesItself()
    {
        var records = new List<ManagedAccount>();

        ManagedAccounts.Set(records, Child, [Mum, Mum, Child, Guid.Empty]);

        Assert.Equal([Mum], ManagedAccounts.ManagersOf(records, Child));
    }

    [Fact]
    public void OtherAccountsKeepTheirRecords()
    {
        var records = new List<ManagedAccount>();
        ManagedAccounts.Set(records, Child, [Mum]);
        ManagedAccounts.Set(records, Other, [Dad]);

        ManagedAccounts.Set(records, Child, []);

        Assert.Equal([Dad], ManagedAccounts.ManagersOf(records, Other));
    }

    [Fact]
    public void ARecordWithDashesInItsIdsStillMatches()
    {
        var records = new List<ManagedAccount>
        {
            new() { UserId = Child.ToString("D"), ManagedBy = [Mum.ToString("D")] },
        };

        Assert.Equal([Mum], ManagedAccounts.ManagersOf(records, Child));
        Assert.Equal([Child], ManagedAccounts.ManagedBy(records, Directory(), Mum).Select(u => u.Id));
    }

    [Fact]
    public void AnAdministratorSeesTheAccountsTheyManage()
    {
        var records = new List<ManagedAccount>();
        ManagedAccounts.Set(records, Child, [Mum, Dad]);
        ManagedAccounts.Set(records, Other, [Dad]);

        var mums = ManagedAccounts.ManagedBy(records, Directory(), Mum);
        var dads = ManagedAccounts.ManagedBy(records, Directory(), Dad);

        Assert.Equal(["Child"], mums.Select(u => u.Name));
        Assert.Equal(["Child", "Other"], dads.Select(u => u.Name));
        Assert.Equal("tag-child", mums[0].ImageTag);
    }

    [Fact]
    public void SomeoneManagingNobodySeesNobody()
    {
        var records = new List<ManagedAccount>();
        ManagedAccounts.Set(records, Child, [Mum]);

        Assert.Empty(ManagedAccounts.ManagedBy(records, Directory(), Dad));
    }

    [Fact]
    public void AnAccountThatNoLongerExistsIsLeftOut()
    {
        var records = new List<ManagedAccount>();
        var gone = Guid.Parse("55555555-5555-5555-5555-555555555555");
        ManagedAccounts.Set(records, gone, [Mum]);
        ManagedAccounts.Set(records, Child, [Mum]);

        Assert.Equal([Child], ManagedAccounts.ManagedBy(records, Directory(), Mum).Select(u => u.Id));
    }

    [Fact]
    public void ARecordFromSomeoneWhoIsNoLongerAnAdministratorGivesNothing()
    {
        var records = new List<ManagedAccount>();
        ManagedAccounts.Set(records, Child, [Mum]);
        var demoted = new DirectoryUser[]
        {
            new(Mum, "Mum", null, false),
            new(Child, "Child", null, false),
        };

        Assert.Empty(ManagedAccounts.ManagedBy(records, demoted, Mum));
    }

    [Fact]
    public void ANonAdministratorCallerSeesNothingEvenIfListed()
    {
        var records = new List<ManagedAccount> { new() { UserId = Child.ToString("N"), ManagedBy = [Other.ToString("N")] } };

        Assert.Empty(ManagedAccounts.ManagedBy(records, Directory(), Other));
    }

    [Fact]
    public void AnAdministratorDoesNotSeeThemselves()
    {
        var records = new List<ManagedAccount> { new() { UserId = Mum.ToString("N"), ManagedBy = [Mum.ToString("N")] } };

        Assert.Empty(ManagedAccounts.ManagedBy(records, Directory(), Mum));
    }

    [Fact]
    public void ListsOfManagersAreCheckedBeforeTheyAreSet()
    {
        Assert.Null(ManagedAccounts.Problem(Directory(), Child, [Mum, Dad]));
        Assert.Null(ManagedAccounts.Problem(Directory(), Child, []));
        Assert.NotNull(ManagedAccounts.Problem(Directory(), Guid.NewGuid(), [Mum]));
        Assert.NotNull(ManagedAccounts.Problem(Directory(), Child, [Other]));
        Assert.NotNull(ManagedAccounts.Problem(Directory(), Child, [Guid.NewGuid()]));
        Assert.NotNull(ManagedAccounts.Problem(Directory(), Mum, [Mum]));
    }

    [Fact]
    public void AnAccountCannotHaveTooManyManagers()
    {
        var many = Enumerable.Range(0, ManagedAccounts.MaxManagers + 1).Select(_ => Guid.NewGuid()).ToList();
        var admins = many.Select(id => new DirectoryUser(id, "Admin", null, true)).ToArray();

        Assert.NotNull(ManagedAccounts.Problem(Directory(admins), Child, many));
        Assert.Null(ManagedAccounts.Problem(Directory(admins), Child, many.Take(ManagedAccounts.MaxManagers).ToList()));
    }

    [Fact]
    public void TheRecordsSurviveTheConfigRoundTrip()
    {
        var config = new PluginConfiguration();
        ManagedAccounts.Set(config.ManagedAccounts, Child, [Mum, Dad]);

        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, null, 1024, leaveOpen: true))
        using (var textWriter = new XmlTextWriter(writer))
        {
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(textWriter, config);
        }

        stream.Position = 0;
        using var reader = XmlReader.Create(stream);
        var read = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;

        Assert.Equal([Mum, Dad], ManagedAccounts.ManagersOf(read.ManagedAccounts, Child));
    }
}
