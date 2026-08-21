using System.Text;

namespace PCMode.Engine.Tests;

/// <summary>
/// Where the television's pairing key is kept, and what it looks like on disk.
/// </summary>
/// <remarks>
/// This one does encrypt for real, because what is being checked is precisely
/// that it encrypts. It writes only into a temporary folder that goes away with
/// the test -- never the real profile -- and touches no hardware.
/// </remarks>
public sealed class LgTvCredentialsTests
{
    private const string Host = "192.0.2.31";

    [Fact]
    public void Keeps_a_credential_and_gives_it_back()
    {
        using var root = new TemporaryRoot();

        var store = new ProtectedLgTvCredentials(root.Path);

        Assert.True(store.TryWrite(Host, new LgTvCredential("secret-key", "AABB"), out string? problem));
        Assert.Null(problem);

        LgTvCredential stored = store.Read(Host)!;

        Assert.Equal("secret-key", stored.ClientKey);
        Assert.Equal("AABB", stored.CertificateFingerprint);
    }

    [Fact]
    public void Never_writes_the_key_where_anything_could_read_it()
    {
        // The claim the whole class exists to make. config.json gets edited,
        // copied, put in backups and pasted into messages asking for help; a
        // credential for a device on somebody's network does not go in it, and
        // does not go anywhere else in the clear either.
        using var root = new TemporaryRoot();

        Assert.True(new ProtectedLgTvCredentials(root.Path)
            .TryWrite(Host, new LgTvCredential("secret-key", "AABB"), out _));

        foreach (string file in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories))
        {
            byte[] written = File.ReadAllBytes(file);

            Assert.DoesNotContain("secret-key", Encoding.UTF8.GetString(written), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-key", Encoding.Unicode.GetString(written), StringComparison.Ordinal);

            // Nor is the television's address the file's name, which would tell
            // anything that could list the folder what is on this network.
            Assert.DoesNotContain(Host, Path.GetFileName(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_credential_does_not_open_for_a_different_television()
    {
        // The entropy binds a file to the television it was issued for, so one
        // renamed or copied between roots fails to open rather than quietly
        // offering another television's key.
        using var root = new TemporaryRoot();

        var store = new ProtectedLgTvCredentials(root.Path);

        Assert.True(store.TryWrite(Host, new LgTvCredential("secret-key", "AABB"), out _));

        Assert.Null(store.Read("192.0.2.99"));
    }

    [Fact]
    public void Reads_nothing_rather_than_failing_when_the_file_is_not_one_of_ours()
    {
        // A profile copied from another machine, or a truncated write. Both mean
        // "there is no key", and pairing again is the way out of either -- where
        // throwing would leave a television that can never be used again without
        // somebody finding and deleting a file.
        using var root = new TemporaryRoot();

        var store = new ProtectedLgTvCredentials(root.Path);

        Assert.True(store.TryWrite(Host, new LgTvCredential("secret-key", "AABB"), out _));

        string file = Directory.EnumerateFiles(root.Path, "*.cred", SearchOption.AllDirectories).Single();

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        Assert.Null(store.Read(Host));
    }

    [Fact]
    public void Keeps_the_previous_credential_when_a_new_one_cannot_be_written()
    {
        // Written aside and moved into place, so an interrupted write leaves the
        // old credential intact rather than a file that decrypts to nothing.
        using var root = new TemporaryRoot();

        var store = new ProtectedLgTvCredentials(root.Path);

        Assert.True(store.TryWrite(Host, new LgTvCredential("first-key", "AABB"), out _));
        Assert.True(store.TryWrite(Host, new LgTvCredential("second-key", "CCDD"), out _));

        Assert.Equal("second-key", store.Read(Host)!.ClientKey);
    }
}
