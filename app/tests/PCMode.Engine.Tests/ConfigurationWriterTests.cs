namespace PCMode.Engine.Tests;

/// <summary>
/// The one path every configuration write goes through: atomic, one backup
/// generation deep, and recoverable from itself.
/// </summary>
/// <remarks>
/// Every <c>*ConfigurationStore</c> test exercises this indirectly, through a
/// successful save. What none of them check is the writer's own contract --
/// that a write that cannot land leaves the previous file exactly as it was,
/// that the backup really is only one generation, and that restoring from it
/// does not throw the recovery away in the same step it uses it.
/// </remarks>
public sealed class ConfigurationWriterTests
{
    [Fact]
    public void First_write_to_a_path_that_never_existed_creates_no_backup()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");

        ConfigurationWriter.WriteAtomically(path, "one");

        Assert.Equal("one", File.ReadAllText(path));
        Assert.False(File.Exists(ConfigurationWriter.BackupPath(path)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_second_write_backs_up_exactly_what_the_first_one_left()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");

        ConfigurationWriter.WriteAtomically(path, "one");
        ConfigurationWriter.WriteAtomically(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("one", File.ReadAllText(ConfigurationWriter.BackupPath(path)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>
    /// The backup is one generation, not a history: the third write pushes the
    /// first generation out rather than keeping it alongside the second.
    /// </summary>
    [Fact]
    public void The_backup_holds_only_the_most_recent_previous_generation()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");

        ConfigurationWriter.WriteAtomically(path, "one");
        ConfigurationWriter.WriteAtomically(path, "two");
        ConfigurationWriter.WriteAtomically(path, "three");

        Assert.Equal("three", File.ReadAllText(path));
        Assert.Equal("two", File.ReadAllText(ConfigurationWriter.BackupPath(path)));
    }

    [Fact]
    public void Writing_creates_the_folder_when_it_does_not_exist_yet()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "nested", "deeper", "config.json");

        ConfigurationWriter.WriteAtomically(path, "content");

        Assert.Equal("content", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_a_blank_path(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => ConfigurationWriter.WriteAtomically(path!, "content"));
    }

    [Fact]
    public void Refuses_null_content()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");

        Assert.Throws<ArgumentNullException>(() => ConfigurationWriter.WriteAtomically(path, null!));
    }

    /// <summary>
    /// A write that cannot land -- here, a temp file the writer cannot create
    /// because something else already holds it open -- leaves the file everyone
    /// else reads exactly as it was, never half-written and never missing.
    /// </summary>
    [Fact]
    public void A_write_that_cannot_complete_leaves_the_previous_file_untouched()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");
        ConfigurationWriter.WriteAtomically(path, "good");

        using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => ConfigurationWriter.WriteAtomically(path, "bad"));
        }

        Assert.Equal("good", File.ReadAllText(path));
        Assert.False(File.Exists(ConfigurationWriter.BackupPath(path)));
    }

    [Fact]
    public void TryReadBackup_reports_false_with_no_backup_on_disk()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");
        ConfigurationWriter.WriteAtomically(path, "only generation");

        bool found = ConfigurationWriter.TryReadBackup(path, out string? content);

        Assert.False(found);
        Assert.Null(content);
    }

    [Fact]
    public void TryReadBackup_reports_false_rather_than_throwing_when_the_backup_cannot_be_read()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");
        ConfigurationWriter.WriteAtomically(path, "one");
        ConfigurationWriter.WriteAtomically(path, "two");

        using (new FileStream(ConfigurationWriter.BackupPath(path), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool found = ConfigurationWriter.TryReadBackup(path, out string? content);

            Assert.False(found);
            Assert.Null(content);
        }
    }

    [Fact]
    public void TryRestoreBackup_explains_itself_when_there_is_nothing_to_restore()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");
        ConfigurationWriter.WriteAtomically(path, "only generation");

        bool restored = ConfigurationWriter.TryRestoreBackup(path, out string? problem);

        Assert.False(restored);
        Assert.NotNull(problem);
        Assert.Contains(ConfigurationWriter.BackupPath(path), problem, StringComparison.Ordinal);
        Assert.Equal("only generation", File.ReadAllText(path));
    }

    /// <summary>
    /// The scenario the whole mechanism exists for: a candidate that validated
    /// and saved cleanly, then turned out to be wrong once it was the file
    /// everything read.
    /// </summary>
    [Fact]
    public void TryRestoreBackup_puts_the_previous_generation_back_in_place()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");
        ConfigurationWriter.WriteAtomically(path, "good");
        ConfigurationWriter.WriteAtomically(path, "bad candidate");

        bool restored = ConfigurationWriter.TryRestoreBackup(path, out string? problem);

        Assert.True(restored);
        Assert.Null(problem);
        Assert.Equal("good", File.ReadAllText(path));
    }

    /// <summary>
    /// A restore is exactly as safe as any other write: what it just replaced
    /// becomes the new backup rather than being discarded, so undoing a restore
    /// that turns out to be wrong is still possible.
    /// </summary>
    [Fact]
    public void Restoring_rotates_the_bad_candidate_into_the_backup_slot_in_turn()
    {
        using var root = new TemporaryRoot();
        string path = Path.Combine(root.Path, "config.json");
        ConfigurationWriter.WriteAtomically(path, "good");
        ConfigurationWriter.WriteAtomically(path, "bad candidate");

        Assert.True(ConfigurationWriter.TryRestoreBackup(path, out _));

        Assert.Equal("good", File.ReadAllText(path));
        Assert.Equal("bad candidate", File.ReadAllText(ConfigurationWriter.BackupPath(path)));
    }

    [Fact]
    public void BackupPath_is_the_configuration_path_with_a_bak_suffix()
    {
        Assert.Equal(@"C:\Data\config.json.bak", ConfigurationWriter.BackupPath(@"C:\Data\config.json"));
    }
}
