using PCMode.Engine;
using PCMode.Tray.Elements.Home;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Features.Home;

/// <summary>
/// <see cref="HomeViewModel"/>'s target grid and switching, read from and
/// applied through a real <see cref="ModeEngine"/> directly, since Target has
/// no shell-wide status view model (see <c>ARCHITECTURE.md</c>'s
/// "Permanently excluded" section). No Retry/Repair or health tracking here
/// by the same reasoning -- a card only ever shows whether it is current.
/// </summary>
public sealed class HomeViewModelTests
{
    private const string TwoTargetsConfiguration = """
        {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}},
          {"id":"movie","name":"Movie","resources":{}}]}]}
        """;

    [Fact]
    public void Refresh_groups_every_enabled_target_by_space()
    {
        using var root = new TemporaryConfigurationRoot(TwoTargetsConfiguration);
        ModeEngine engine = EngineBootstrap.Create(root.Path);
        var runtime = new EngineRuntime(engine, root.Path);
        var viewModel = new HomeViewModel(runtime);

        viewModel.Refresh();

        Assert.True(viewModel.HasRuntime);
        Assert.True(viewModel.IsIdle);
        HomeTargetGroup group = Assert.Single(viewModel.TargetGroups);
        Assert.False(group.IsFavorite);
        Assert.Equal(2, group.Targets.Count);
        Assert.All(group.Targets, target => Assert.False(target.IsCurrent));
    }

    [Fact]
    public void Refresh_without_a_runtime_leaves_the_grid_empty()
    {
        var viewModel = new HomeViewModel(runtime: null);

        viewModel.Refresh();

        Assert.False(viewModel.HasRuntime);
        Assert.Empty(viewModel.TargetGroups);
    }

    [Fact]
    public async Task ApplyAsync_reports_the_outcome_and_leaves_the_view_model_idle()
    {
        using var root = new TemporaryConfigurationRoot(TwoTargetsConfiguration);
        // The activity sets no resources, so the engine refuses the switch --
        // deliberately, so this test never touches real hardware providers
        // the way EngineBootstrap.Create's real ones would on a successful
        // apply. What belongs here is HomeViewModel's own busy/message
        // bookkeeping around the call, not ModeEngine's transition logic
        // (covered in PCMode.Engine.Tests).
        ModeEngine engine = EngineBootstrap.Create(root.Path);
        var runtime = new EngineRuntime(engine, root.Path);
        var viewModel = new HomeViewModel(runtime);
        viewModel.Refresh();

        HomeTargetCard movie = viewModel.TargetGroups
            .Single()
            .Targets
            .Single(target => string.Equals(target.Name, "Movie", StringComparison.OrdinalIgnoreCase));

        await viewModel.ApplyAsync(movie.Key);

        Assert.True(viewModel.IsIdle);
        Assert.NotNull(viewModel.StatusMessage);
    }
}
