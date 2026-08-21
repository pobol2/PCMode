using System.IO;
using PCMode.Engine;
using PCMode.Tray.Configuration;

namespace PCMode.Tray.Tests;

/// <summary>
/// The workspace a page-level test hands its view model.
/// </summary>
/// <remarks>
/// A real <see cref="ConfigurationWorkspace"/> over a temporary folder, because
/// what page-construction and lifecycle tests want is what a page actually
/// does when it reads and writes a real file, not a stand-in for one.
///
/// Use <see cref="FakeWorkspace"/> instead when the point is what a caller
/// does with the workspace's contract in isolation -- above all
/// <see cref="IConfigurationWorkspace.Apply"/> -- rather than what reaches
/// disk.
/// </remarks>
internal static class Workspaces
{
    public static IConfigurationWorkspace At(string root) => ConfigurationWorkspace.For(root);

    public static IConfigurationWorkspace At(string root, Func<RuntimeActivationOutcome> activate) =>
        ConfigurationWorkspace.For(root, activate: activate);
}

/// <summary>
/// A workspace with no configuration file behind it, and no engine to
/// activate, for the rules that need only the shared contract.
/// </summary>
internal sealed class FakeWorkspace(
    (ModeConfiguration Configuration, IReadOnlyList<IModeResourceProvider> Providers)? snapshot = null,
    Func<string, string>? apply = null) : IConfigurationWorkspace
{
    public string? Root => null;

    public int ApplyCalls { get; private set; }

    public List<string> Notes { get; } = [];

    public List<string> Warnings { get; } = [];

    public IReadOnlyList<IModeResourceProvider> ReadProviders() => Read().Providers;

    public (ModeConfiguration Configuration, IReadOnlyList<IModeResourceProvider> Providers) Read() =>
        snapshot ?? throw new ModeConfigurationException("This workspace has no configuration.");

    public string Apply(string saved)
    {
        ApplyCalls++;
        return apply?.Invoke(saved) ?? saved;
    }

    public void Note(string category, string message) => Notes.Add(message);

    public void Warn(string category, string message) => Warnings.Add(message);

    public void Fault(string category, string message, Exception exception) => Warnings.Add(message);
}

/// <summary>A configuration folder that goes away with the test.</summary>
internal sealed class TemporaryConfigurationRoot : IDisposable
{
    public TemporaryConfigurationRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"PCMode.Tray.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public TemporaryConfigurationRoot(string document)
        : this()
    {
        Write(document);
    }

    public string Path { get; }

    public string ConfigurationFile => PCModePaths.ConfigurationFile(Path);

    public void Write(string json) => File.WriteAllText(ConfigurationFile, json);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
