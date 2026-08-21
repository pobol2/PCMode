namespace PCMode.Engine.Tests;

/// <summary>
/// <see cref="LgTvConnection.TryPair"/> -- the settings screen's "Pair" action.
/// </summary>
public sealed class LgTvConnectionPairingTests
{
    private const string Host = "192.0.2.31";
    private const string Mac = "00:00:5E:00:53:31";

    [Fact]
    public void Pairs_and_stores_the_issued_key_without_switching_anything()
    {
        var set = new FakeTelevision();
        var keys = new FakeCredentials();
        LgTvConnection connection = Configured(set, keys);

        bool paired = connection.TryPair(out string? problem);

        Assert.True(paired, problem);
        Assert.Null(problem);
        Assert.NotNull(keys.Read(Host));
        Assert.Empty(set.Woken);
    }

    [Fact]
    public void Reports_a_television_that_is_off_rather_than_throwing()
    {
        var set = new FakeTelevision { Reachable = false };
        LgTvConnection connection = Configured(set, new FakeCredentials());

        bool paired = connection.TryPair(out string? problem);

        Assert.False(paired);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Refuses_when_no_television_is_configured()
    {
        LgTvConnection connection = new(new FakeTelevision(), new FakeCredentials());
        Assert.True(connection.TryConfigure(null, out _));

        bool paired = connection.TryPair(out string? problem);

        Assert.False(paired);
        Assert.Contains("No television", problem, StringComparison.Ordinal);
    }

    private static LgTvConnection Configured(FakeTelevision set, FakeCredentials keys)
    {
        var connection = new LgTvConnection(set, keys);

        Assert.True(connection.TryConfigure(
            System.Text.Json.JsonDocument.Parse(
                $$"""{"host":"{{Host}}","macAddress":"{{Mac}}"}""").RootElement,
            out string? problem), problem);

        return connection;
    }
}
