using Xunit.Abstractions;

namespace PCMode.Engine.Tests;

/// <summary>
/// A test that is skipped unless the machine was explicitly volunteered.
/// </summary>
/// <remarks>
/// Decided when tests are discovered, before anything in the class is
/// constructed, so an ordinary run never enters it. A guard inside the test body
/// would be weaker in both directions: it would report as a passing test that
/// had quietly done nothing, and a mistake in it would start rearranging
/// somebody's displays in the middle of a build.
/// </remarks>
public sealed class HierarchyHardwareFactAttribute : FactAttribute
{
    public HierarchyHardwareFactAttribute()
    {
        if (!HierarchyHardwareTest.Requested)
        {
            Skip =
                "Hardware check. Set PCMODE_HIERARCHY_HARDWARE_TEST=1 to run it: it composes a copy of this " +
                "machine's real configuration and asks the real providers what the composed targets would do.";
        }
    }
}

/// <summary>
/// This machine's own configuration, composed and walked against its real
/// hardware.
/// </summary>
/// <remarks>
/// WHAT THIS PROVES THAT THE AUTOMATED TESTS CANNOT
///
/// Everything else about composition is checked against documents written for
/// the purpose, with fake providers. This checks the one configuration that
/// actually matters to the person running it: theirs. It composes every target
/// from a copy of it, and asks the real display, audio, television, lighting
/// and application providers to read this machine and say what each target
/// would change -- which is the half of the loop no fake can stand in for.
///
/// THE REAL CONFIGURATION IS NEVER WRITTEN TO
///
/// The whole data root is copied to a temporary folder first, and everything
/// happens there: the backup, the recorded current target. The television's
/// pairing key is copied along with it, so a paired set still answers.
/// Whatever this does, %LOCALAPPDATA%\PCMode\Native is exactly as it was
/// afterwards.
///
/// NOTHING IS APPLIED UNLESS A TARGET IS NAMED
///
/// By default every target is walked as a dry run: providers are read, nothing
/// is dispatched, and the machine is not changed. Setting
/// PCMODE_HIERARCHY_TARGET to a target -- its key, its full path, or an
/// unambiguous name -- applies that one for real and then puts the machine back
/// into whatever target was recorded before, if one was.
///
/// Run it with:
///
///   $env:PCMODE_HIERARCHY_HARDWARE_TEST="1"; dotnet test app/tests/PCMode.Engine.Tests --filter "FullyQualifiedName~HierarchyHardwareTest" --logger "console;verbosity=detailed"
///
/// Add $env:PCMODE_HIERARCHY_TARGET="desk/gaming" to also apply one for real.
/// PCMODE_HIERARCHY_HARDWARE_ROOT overrides which data root is copied.
/// </remarks>
public sealed class HierarchyHardwareTest(ITestOutputHelper output)
{
    private const string Switch = "PCMODE_HIERARCHY_HARDWARE_TEST";
    private const string RootVariable = "PCMODE_HIERARCHY_HARDWARE_ROOT";
    private const string TargetVariable = "PCMODE_HIERARCHY_TARGET";

    internal static bool Requested =>
        string.Equals(Environment.GetEnvironmentVariable(Switch), "1", StringComparison.Ordinal);

    [HierarchyHardwareFact]
    public async Task This_machines_configuration_composes_and_walks_on_real_hardware()
    {
        string source = Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } named
            ? named
            : PCModePaths.DefaultRoot;

        Assert.True(
            File.Exists(PCModePaths.ConfigurationFile(source)),
            $"There is no configuration at {PCModePaths.ConfigurationFile(source)}. Set {RootVariable} to a data root.");

        string root = Copy(source);
        output.WriteLine($"Real data root:  {source}");
        output.WriteLine($"Working copy:    {root}");

        try
        {
            // ---- the real engine, over the real providers -------------------
            ModeEngine engine = EngineBootstrap.Create(root);

            Assert.NotEmpty(engine.Targets);

            output.WriteLine(string.Empty);
            output.WriteLine($"{engine.Targets.Count} target(s):");

            foreach (ConfiguredTarget target in engine.Targets)
            {
                output.WriteLine($"  {target.Key,-40} {target.DisplayPath}");
            }

            // ---- what each one would do to this machine ---------------------
            foreach (ConfiguredTarget target in engine.Targets)
            {
                TransitionOutcome dryRun = await engine.ApplyAsync(target.Key, dryRun: true);

                output.WriteLine(string.Empty);
                output.WriteLine($"{target.DisplayPath}: {dryRun.Message}");

                foreach (TransitionStep step in dryRun.Steps)
                {
                    output.WriteLine(
                        $"  {step.Status,-15} {step.ResourceName,-22} {step.Current ?? "?"} -> {step.Target}" +
                        (step.Problem is null ? string.Empty : $"  ({step.Problem})"));
                }

                // A dry run that cannot even be planned is a configuration this
                // machine could not apply, which is exactly what this is for.
                Assert.True(
                    dryRun.Status is TransitionStatus.Succeeded or TransitionStatus.Refused,
                    $"'{target.DisplayPath}' could not be walked: {dryRun.Message}");
            }

            await ApplyIfAskedAsync(engine);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Applies one target for real, then puts the machine back where it was.
    /// </summary>
    /// <remarks>
    /// Restored by re-applying whatever was recorded before, which is the
    /// product's own idea of "back": a hardware check that left the television
    /// on and the sound on the soundbar would be a worse experience than the
    /// problem it was looking for.
    /// </remarks>
    private async Task ApplyIfAskedAsync(ModeEngine engine)
    {
        if (Environment.GetEnvironmentVariable(TargetVariable) is not { Length: > 0 } requested)
        {
            output.WriteLine(string.Empty);
            output.WriteLine($"Nothing was applied. Set {TargetVariable} to apply one target for real.");
            return;
        }

        ConfiguredTarget? target = engine.ResolveTarget(requested);

        Assert.True(target is not null, $"'{requested}' does not name exactly one target.");

        string? before = engine.ReadCurrentTargetKey(out _);

        output.WriteLine(string.Empty);
        output.WriteLine($"Applying {target!.DisplayPath} for real. Recorded before: {before ?? "nothing"}.");

        try
        {
            TransitionOutcome outcome = await engine.ApplyAsync(target.Key);

            output.WriteLine($"Result: {outcome.Status} -- {outcome.Message}");

            foreach (TransitionStep step in outcome.Steps)
            {
                output.WriteLine(
                    $"  {step.Status,-15} {step.ResourceName,-22} {step.Current ?? "?"} -> {step.Target}" +
                    (step.Problem is null ? string.Empty : $"  ({step.Problem})"));
            }

            ModeHealth? health = engine.LastHealth;
            output.WriteLine($"Health: {health?.Overall.ToString() ?? "unknown"} for {health?.DisplayName}.");

            Assert.True(outcome.Succeeded, outcome.Message);
        }
        finally
        {
            if (before is not null && !string.Equals(before, target.Key, StringComparison.OrdinalIgnoreCase))
            {
                TransitionOutcome restored = await engine.ApplyAsync(before);
                output.WriteLine($"Put back: {restored.Status} -- {restored.Message}");
            }
        }
    }

    private static string Copy(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), $"PCMode.Hierarchy.Hardware.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        }

        return root;
    }

    private static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not worth failing a check over.
        }
    }
}
