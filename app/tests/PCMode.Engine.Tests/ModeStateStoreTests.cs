namespace PCMode.Engine.Tests;

/// <summary>
/// PCMode's record of what mode it is in.
/// </summary>
public sealed class ModeStateStoreTests
{
    [Fact]
    public void Reports_no_mode_on_a_machine_that_has_never_transitioned()
    {
        // The ordinary state of a fresh install, and not a problem: reporting
        // one would put a warning in front of somebody on first launch.
        using var root = new TemporaryRoot();

        Assert.Null(new ModeStateStore(root.StateFile).Read(out string? problem));
        Assert.Null(problem);
    }

    [Fact]
    public void Reads_back_what_it_wrote()
    {
        using var root = new TemporaryRoot();
        var store = new ModeStateStore(root.StateFile);
        DateTimeOffset when = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.True(store.TryWrite("Movie", when, out _));

        RecordedMode? recorded = store.Read(out string? problem);

        Assert.Null(problem);
        Assert.Equal("Movie", recorded!.Mode);
        Assert.Equal(when, recorded.RecordedUtc);
    }

    [Fact]
    public void Creates_the_folder_it_writes_into()
    {
        using var root = new TemporaryRoot();
        // First run writes state before anything else has made the directory.
        string nested = Path.Combine(root.Path, "deeper", "state.json");

        Assert.True(new ModeStateStore(nested).TryWrite("Desktop", DateTimeOffset.UtcNow, out _));
        Assert.True(File.Exists(nested));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":99,"mode":"Movie"}""")]
    public void Treats_a_document_it_cannot_use_as_no_mode_at_all(string json)
    {
        using var root = new TemporaryRoot();
        // The application has to start on a machine where something else has
        // been writing files. A crash here would be a shell that will not open
        // because of a file it could simply have ignored.
        File.WriteAllText(root.StateFile, json);

        Assert.Null(new ModeStateStore(root.StateFile).Read(out string? problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Explains_a_write_it_could_not_make_instead_of_throwing()
    {
        using var root = new TemporaryRoot();
        // Called after the machine has already been changed. An exception here
        // would turn a transition that genuinely happened into one reported as
        // failed, which is the one thing the state write must never do.
        Directory.CreateDirectory(root.StateFile);

        Assert.False(new ModeStateStore(root.StateFile).TryWrite("Movie", DateTimeOffset.UtcNow, out string? problem));
        Assert.NotNull(problem);
    }
}
