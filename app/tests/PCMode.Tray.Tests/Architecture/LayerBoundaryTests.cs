using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using PCMode.Tray.Tests.Support;

namespace PCMode.Tray.Tests.Architecture;

/// <summary>
/// Enforces the ownership boundaries of the Tray target architecture:
/// App, Elements, Platform, and UI/{Framework,Tree,Components}.
/// </summary>
public sealed class LayerBoundaryTests
{
    private static readonly string[] SupersededNamespaces =
    [
        "PCMode.Tray.Composition",
        "PCMode.Tray.Configuration",
        "PCMode.Tray.Features",
        "PCMode.Tray.Framework",
        "PCMode.Tray.Platform.Windows",
        "PCMode.Tray.UI.Construction",
        "PCMode.Tray.App.Settings",

        // Not merely unused: App.xaml/App.xaml.cs declare class App directly in
        // namespace PCMode.Tray, so "PCMode.Tray.App" cannot also work as a
        // namespace prefix -- C# will not let a type and a namespace segment
        // share the same qualified name. Any "PCMode.Tray.App.*" namespace
        // fails to compile, not just fails this check.
        "PCMode.Tray.App.Picker",
    ];

    // Namespaces owned by App/ but not nested under the assembly's root
    // namespace -- each exists because its folder's natural home
    // ("PCMode.Tray.App.<X>") collides with the App class (see
    // SupersededNamespaces above), so it sits beside App instead of under it.
    private static readonly string[] AppSiblingNamespaces =
    [
        "PCMode.Tray.Picker",
    ];

    private static readonly (string Folder, string[] Namespaces)[] FolderOwners =
    [
        ("Elements", ["PCMode.Tray.Elements"]),
        ("Platform", ["PCMode.Tray.Platform"]),
        (Path.Combine("UI", "Framework"), ["PCMode.Tray.UI.Framework"]),
        (Path.Combine("UI", "Tree"), ["PCMode.Tray.UI.Tree"]),
        (Path.Combine("UI", "Components"), ["PCMode.Tray.UI.Components"]),

        // App owns the assembly's root namespace itself, not just a nested
        // "PCMode.Tray.App" segment -- see IsOwnedByFolder, and the
        // AppSiblingNamespaces comment above for why a nested form is not
        // always available to it.
        ("App", ["PCMode.Tray", "PCMode.Tray.App", .. AppSiblingNamespaces]),
    ];

    [Fact]
    public void Superseded_ownership_namespaces_are_empty()
    {
        string[] clrViolations =
        [
            .. TrayTypes()
                .Where(type => SupersededNamespaces.Any(space => InNamespace(type, space)))
                .Select(type => type.FullName!),
        ];
        string[] sourceViolations =
        [
            .. SourceFiles(string.Empty, "*.cs")
                .SelectMany(file => Regex.Matches(
                        File.ReadAllText(file),
                        @"(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)")
                    .Select(match => (File: file, Namespace: match.Groups[1].Value)))
                .Where(declaration => SupersededNamespaces.Any(space =>
                    InNamespace(declaration.Namespace, space)))
                .Select(declaration =>
                    $"{Relative(declaration.File)} declares {declaration.Namespace}"),
        ];

        Assert.Empty(clrViolations.Concat(sourceViolations).Distinct().Order());
    }

    [Fact]
    public void Source_files_use_the_namespace_owned_by_their_folder()
    {
        var violations = new List<string>();

        foreach ((string folder, string[] owners) in FolderOwners)
        {
            foreach (string file in SourceFiles(folder, "*.cs"))
            {
                MatchCollection declarations = Regex.Matches(
                    File.ReadAllText(file),
                    @"(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)");

                if (declarations.Count == 0)
                    continue;

                foreach (Match declaration in declarations)
                {
                    string declared = declaration.Groups[1].Value;
                    if (!IsOwnedByFolder(folder, declared, owners))
                    {
                        violations.Add(
                            $"{Relative(file)} declares {declared}; " +
                            $"the folder is owned by {string.Join(" or ", owners)}.");
                    }
                }
            }
        }

        Assert.Empty(violations.Order());
    }

    [Fact]
    public void Framework_does_not_depend_on_tree_components_or_elements()
    {
        string[] forbidden =
        [
            "PCMode.Tray.UI.Tree",
            "PCMode.Tray.UI.Components",
            "PCMode.Tray.Elements",
        ];

        AssertNoReferences(
            "PCMode.Tray.UI.Framework",
            forbidden);
        AssertSourceDoesNotNameNamespaces(Path.Combine("UI", "Framework"), forbidden);
    }

    [Fact]
    public void Components_do_not_depend_on_tree_or_elements()
    {
        string[] forbidden =
        [
            "PCMode.Tray.UI.Tree",
            "PCMode.Tray.Elements",
        ];

        AssertNoReferences(
            "PCMode.Tray.UI.Components",
            forbidden);
        AssertSourceDoesNotNameNamespaces(Path.Combine("UI", "Components"), forbidden);
    }

    [Fact]
    public void Tree_does_not_depend_on_application_elements()
    {
        string[] forbidden = ["PCMode.Tray.Elements"];

        AssertNoReferences(
            "PCMode.Tray.UI.Tree",
            forbidden);
        AssertSourceDoesNotNameNamespaces(Path.Combine("UI", "Tree"), forbidden);
    }

    private static void AssertSourceDoesNotNameNamespaces(
        string relativeDirectory,
        IEnumerable<string> forbiddenNamespaces)
    {
        string[] forbidden = forbiddenNamespaces.ToArray();
        string[] violations =
        [
            .. SourceFiles(relativeDirectory, "*.cs")
                .Where(file => forbidden.Any(space =>
                    File.ReadAllText(file).Contains(space, StringComparison.Ordinal)))
                .Select(Relative),
        ];

        Assert.Empty(violations);
    }

    private static void AssertNoReferences(
        string ownerNamespace,
        IEnumerable<string> forbiddenNamespaces)
    {
        var forbidden = forbiddenNamespaces.ToArray();
        var violations = new List<string>();

        foreach (Type type in TrayTypes().Where(type => InNamespace(type, ownerNamespace)))
        {
            foreach (Type referenced in ReferencedTypes(type))
            {
                if (forbidden.Any(space => InNamespace(referenced, space)))
                {
                    violations.Add($"{type.FullName} names {referenced.FullName}");
                }
            }
        }

        Assert.Empty(violations.Distinct().Order());
    }

    private static bool InNamespace(Type type, string space) =>
        type.Namespace is { } declared
        && InNamespace(declared, space);

    private static bool InNamespace(string declared, string space) =>
        declared == space ||
        declared.StartsWith(space + ".", StringComparison.Ordinal);

    private static bool IsOwnedByFolder(
        string folder,
        string declared,
        IEnumerable<string> owners) =>
        folder == "App"
            ? declared == "PCMode.Tray" ||
              InNamespace(declared, "PCMode.Tray.App") ||
              AppSiblingNamespaces.Any(space => InNamespace(declared, space))
            : owners.Any(owner => InNamespace(declared, owner));

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags All =
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        var found = new List<Type>();

        Add(type.BaseType);

        foreach (Type contract in type.GetInterfaces())
            Add(contract);

        foreach (FieldInfo field in type.GetFields(All))
            Add(field.FieldType);

        foreach (PropertyInfo property in type.GetProperties(All))
            Add(property.PropertyType);

        foreach (EventInfo declared in type.GetEvents(All))
            Add(declared.EventHandlerType);

        foreach (MethodInfo method in type.GetMethods(All))
        {
            Add(method.ReturnType);

            foreach (ParameterInfo parameter in method.GetParameters())
                Add(parameter.ParameterType);
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(All))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
                Add(parameter.ParameterType);
        }

        return found;

        void Add(Type? candidate)
        {
            if (candidate is null)
                return;

            if (candidate.HasElementType)
            {
                Add(candidate.GetElementType());
                return;
            }

            found.Add(candidate);

            if (!candidate.IsGenericType)
                return;

            foreach (Type argument in candidate.GetGenericArguments())
                Add(argument);
        }
    }

    private static IEnumerable<Type> TrayTypes() =>
        typeof(PCMode.Tray.App).Assembly
            .GetTypes()
            .Where(type => !type.Name.Contains('<', StringComparison.Ordinal))
            .Where(type =>
                type.Namespace?.StartsWith(
                    "PCMode.Tray",
                    StringComparison.Ordinal) == true);

    private static string[] SourceFiles(string relativeDirectory, string pattern)
    {
        string root = TraySource.PathTo(relativeDirectory);

        Assert.True(Directory.Exists(root), $"Tray source directory does not exist: '{root}'.");
        return
        [
            .. Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
                .Where(file => !HasDirectory(file, "bin") && !HasDirectory(file, "obj")),
        ];
    }

    private static string Relative(string file) => TraySource.Relative(file);

    private static bool HasDirectory(string file, string directory) =>
        file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(directory, StringComparer.OrdinalIgnoreCase);
}
