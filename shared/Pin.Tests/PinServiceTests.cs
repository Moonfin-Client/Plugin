using System;
using System.Collections.Generic;
using Xunit;

namespace Moonfin.Pin.Tests;

public class PinServiceTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class MemoryStore : IPinStore
    {
        public Dictionary<Guid, PinRecord> Records { get; } = new();

        public PinRecord? Load(Guid userId) =>
            Records.TryGetValue(userId, out var r) ? Clone(r) : null;

        public void Save(Guid userId, PinRecord record) => Records[userId] = Clone(record);

        public void Delete(Guid userId) => Records.Remove(userId);

        private static PinRecord Clone(PinRecord r) => new()
        {
            Salt = r.Salt, Hash = r.Hash, Iterations = r.Iterations, Failed = r.Failed,
            LockedUntilUtc = r.LockedUntilUtc, UpdatedUtc = r.UpdatedUtc,
        };
    }

    private readonly MemoryStore _store = new();
    private DateTime _now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private PinService Service() => new(_store, () => _now);

    [Theory]
    [InlineData("1234", true)]
    [InlineData("0000", true)]
    [InlineData("123", false)]
    [InlineData("12345", false)]
    [InlineData("12a4", false)]
    [InlineData("١٢٣٤", false)]
    [InlineData(" 123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void APinIsExactlyFourAsciiDigits(string? pin, bool expected) =>
        Assert.Equal(expected, PinService.IsPinShaped(pin));

    [Fact]
    public void ARightPinVerifiesAndAWrongOneDoesNot()
    {
        var pins = Service();
        Assert.True(pins.Set(User, "4821"));

        Assert.Equal(PinCheck.Valid, pins.Verify(User, "4821").Check);
        Assert.Equal(PinCheck.Wrong, pins.Verify(User, "1234").Check);
        Assert.Equal(PinCheck.Wrong, pins.Verify(User, "abcd").Check);
        Assert.Equal(PinCheck.Wrong, pins.Verify(User, null).Check);
    }

    [Fact]
    public void ThePinIsNeverStored()
    {
        var pins = Service();
        pins.Set(User, "4821");

        var record = _store.Records[User];
        Assert.DoesNotContain("4821", record.Hash);
        Assert.DoesNotContain("4821", record.Salt);
        Assert.NotEmpty(record.Hash);
        Assert.True(record.Iterations >= 100_000);
    }

    [Fact]
    public void TheSamePinGetsADifferentHashEachTime()
    {
        var pins = Service();
        pins.Set(User, "4821");
        pins.Set(Other, "4821");

        Assert.NotEqual(_store.Records[User].Hash, _store.Records[Other].Hash);
        Assert.NotEqual(_store.Records[User].Salt, _store.Records[Other].Salt);
    }

    [Fact]
    public void ASetPinThatIsNotFourDigitsIsRefusedAndChangesNothing()
    {
        var pins = Service();
        pins.Set(User, "4821");

        Assert.False(pins.Set(User, "12"));
        Assert.False(pins.Set(User, "12345"));
        Assert.False(pins.Set(User, null));

        Assert.Equal(PinCheck.Valid, pins.Verify(User, "4821").Check);
    }

    [Fact]
    public void SettingAgainReplacesThePin()
    {
        var pins = Service();
        pins.Set(User, "4821");
        pins.Set(User, "9999");

        Assert.Equal(PinCheck.Wrong, pins.Verify(User, "4821").Check);
        Assert.Equal(PinCheck.Valid, pins.Verify(User, "9999").Check);
    }

    [Fact]
    public void PinsBelongToTheirUser()
    {
        var pins = Service();
        pins.Set(User, "4821");

        Assert.True(pins.HasPin(User));
        Assert.False(pins.HasPin(Other));
        Assert.Equal(PinCheck.NoPin, pins.Verify(Other, "4821").Check);
    }

    [Fact]
    public void RemovingThePinLeavesNone()
    {
        var pins = Service();
        pins.Set(User, "4821");

        pins.Remove(User);

        Assert.False(pins.HasPin(User));
        Assert.Equal(PinCheck.NoPin, pins.Verify(User, "4821").Check);
    }

    [Fact]
    public void ThePinIsLockedAfterTheFreeAttemptsAndTheWaitGrows()
    {
        var pins = Service();
        pins.Set(User, "4821");

        for (var i = 0; i < PinService.FreeAttempts; i++)
        {
            Assert.Equal(PinCheck.Wrong, pins.Verify(User, "0000").Check);
        }

        var sixth = pins.Verify(User, "0000");
        Assert.Equal(PinCheck.Locked, sixth.Check);
        Assert.Equal(PinService.LockoutStepSeconds, sixth.RetryAfterSeconds);

        _now = _now.AddSeconds(PinService.LockoutStepSeconds);
        var seventh = pins.Verify(User, "0000");
        Assert.Equal(PinCheck.Locked, seventh.Check);
        Assert.Equal(PinService.LockoutStepSeconds * 2, seventh.RetryAfterSeconds);
    }

    [Fact]
    public void WhileLockedEvenTheRightPinIsNotLookedAt()
    {
        var pins = Service();
        pins.Set(User, "4821");
        for (var i = 0; i <= PinService.FreeAttempts; i++)
        {
            pins.Verify(User, "0000");
        }

        var locked = pins.Verify(User, "4821");

        Assert.Equal(PinCheck.Locked, locked.Check);
        Assert.False(locked.IsValid);
    }

    [Fact]
    public void WaitingOutTheLockoutLetsTheRightPinIn()
    {
        var pins = Service();
        pins.Set(User, "4821");
        for (var i = 0; i <= PinService.FreeAttempts; i++)
        {
            pins.Verify(User, "0000");
        }

        _now = _now.AddSeconds(PinService.LockoutStepSeconds);

        Assert.Equal(PinCheck.Valid, pins.Verify(User, "4821").Check);
        Assert.Equal(0, _store.Records[User].Failed);
        Assert.Null(_store.Records[User].LockedUntilUtc);
    }

    [Fact]
    public void TheCountIsKeptOnTheServerSoANewServiceStillSeesIt()
    {
        var pins = Service();
        pins.Set(User, "4821");
        for (var i = 0; i <= PinService.FreeAttempts; i++)
        {
            pins.Verify(User, "0000");
        }

        Assert.Equal(PinCheck.Locked, Service().Verify(User, "4821").Check);
    }

    [Fact]
    public void OneUsersGuessesDoNotLockAnother()
    {
        var pins = Service();
        pins.Set(User, "4821");
        pins.Set(Other, "4821");
        for (var i = 0; i <= PinService.FreeAttempts; i++)
        {
            pins.Verify(User, "0000");
        }

        Assert.Equal(PinCheck.Valid, pins.Verify(Other, "4821").Check);
    }

    [Fact]
    public void TheWaitStopsGrowingAtTheLimit()
    {
        Assert.Equal(0, PinService.LockoutSeconds(PinService.FreeAttempts));
        Assert.Equal(PinService.LockoutStepSeconds, PinService.LockoutSeconds(PinService.FreeAttempts + 1));
        Assert.Equal(PinService.MaxLockoutSeconds, PinService.LockoutSeconds(1000));
    }

    [Fact]
    public void SettingANewPinClearsAnyLockout()
    {
        var pins = Service();
        pins.Set(User, "4821");
        for (var i = 0; i <= PinService.FreeAttempts; i++)
        {
            pins.Verify(User, "0000");
        }

        pins.Set(User, "7777");

        Assert.Equal(PinCheck.Valid, pins.Verify(User, "7777").Check);
    }

    [Fact]
    public void ACorruptRecordNeverVerifies()
    {
        _store.Records[User] = new PinRecord { Salt = "not base64!", Hash = "also not", Iterations = 1000 };

        Assert.Equal(PinCheck.Wrong, Service().Verify(User, "4821").Check);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void ARecordWithAnOutOfRangeIterationCountNeverVerifies(int iterations)
    {
        _store.Records[User] = new PinRecord
        {
            Salt = Convert.ToBase64String(new byte[16]),
            Hash = Convert.ToBase64String(new byte[32]),
            Iterations = iterations,
        };

        Assert.Equal(PinCheck.Wrong, Service().Verify(User, "4821").Check);
    }
}
