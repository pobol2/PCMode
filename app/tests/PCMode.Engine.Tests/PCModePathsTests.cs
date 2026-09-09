using System.IO;

namespace PCMode.Engine.Tests;

/// <summary>
/// Where the real installation keeps its files.
/// </summary>
/// <remarks>
/// Cheap to get wrong and expensive to notice: nothing fails when an
/// application writes its state one folder too high, it simply leaves files
/// among somebody else's, and the first person to find out is whoever is
/// clearing up what they think is one program's data.
/// </remarks>
public sealed class PCModePathsTests
{
    [Fact]
    public void Keeps_its_files_in_a_folder_of_its_own()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCMode",
            "Native"
        );

        Assert.Equal(expected, PCModePaths.DefaultRoot);
    }

    [Fact]
    public void Never_puts_anything_directly_in_the_shared_folder()
    {
        // Everything this application writes is below the shared parent folder,
        // never in it.
        string shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PCModePaths.FolderName
        );

        Assert.NotEqual(shared, PCModePaths.DefaultRoot);
        Assert.Equal(shared, Path.GetDirectoryName(PCModePaths.DefaultRoot));

        foreach (
            string file in new[]
            {
                PCModePaths.ConfigurationFile(PCModePaths.DefaultRoot),
                PCModePaths.StateFile(PCModePaths.DefaultRoot),
            }
        )
        {
            Assert.NotEqual(shared, Path.GetDirectoryName(file));
        }
    }

    [Fact]
    public void Puts_both_files_under_whatever_root_it_is_given()
    {
        // The override every test and the --data-root switch rely on. A path
        // that ignored it would send a test's writes into the real profile.
        const string root = @"C:\elsewhere";

        Assert.Equal(root, Path.GetDirectoryName(PCModePaths.ConfigurationFile(root)));
        Assert.Equal(root, Path.GetDirectoryName(PCModePaths.StateFile(root)));
        Assert.NotEqual(PCModePaths.ConfigurationFile(root), PCModePaths.StateFile(root));
    }
}
