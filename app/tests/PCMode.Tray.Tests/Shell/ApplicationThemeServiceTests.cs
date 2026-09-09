using System.Windows;
using System.Windows.Media;
using PCMode.Tray.Platform.Themes;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Shell;

/// <summary>
/// <see cref="ApplicationThemeService"/>'s contract. Both palettes are
/// namespaced -- <c>Dark.</c>-prefixed and <c>Light.</c>-prefixed -- and
/// neither is present unprefixed until <see cref="ApplicationThemeService.Apply"/>
/// writes the resolved theme's copy of each key (see the class remarks on
/// <see cref="ApplicationThemeService"/>).
///
/// These build only plain <see cref="ResourceDictionary"/>/<see cref="SolidColorBrush"/>
/// object graphs, never a XAML-backed window or page, so unlike
/// <see cref="Wpf"/>'s other callers they carry no risk from concurrent
/// pack-URI resource loading and stay out of
/// <see cref="WpfCollection"/> on purpose.
/// </summary>
public sealed class ApplicationThemeServiceTests
{
    [Theory]
    [InlineData(ApplicationTheme.Light, EffectiveTheme.Dark, EffectiveTheme.Light)]
    [InlineData(ApplicationTheme.Dark, EffectiveTheme.Light, EffectiveTheme.Dark)]
    [InlineData(ApplicationTheme.System, EffectiveTheme.Light, EffectiveTheme.Light)]
    [InlineData(ApplicationTheme.System, EffectiveTheme.Dark, EffectiveTheme.Dark)]
    public void Resolves_explicit_and_system_theme_choices(
        ApplicationTheme selected,
        EffectiveTheme windows,
        EffectiveTheme expected
    )
    {
        Assert.Equal(expected, ApplicationThemeService.Resolve(selected, windows));
    }

    [Fact]
    public void Applying_a_new_theme_replaces_semantic_resources_immediately()
    {
        Wpf.Run(() =>
        {
            ResourceDictionary resources = Palette();
            var system = new FakeSystemTheme(EffectiveTheme.Light);
            var service = new ApplicationThemeService(resources, system);

            service.Apply(ApplicationTheme.Light);
            System.Windows.Media.Color light = ((SolidColorBrush)resources["WindowBackgroundBrush"]).Color;

            service.Apply(ApplicationTheme.Dark);
            System.Windows.Media.Color dark = ((SolidColorBrush)resources["WindowBackgroundBrush"]).Color;

            Assert.Equal(ApplicationTheme.Dark, service.Selected);
            Assert.Equal(EffectiveTheme.Dark, service.Effective);
            Assert.NotEqual(light, dark);
            Assert.Empty(resources.MergedDictionaries);
        });
    }

    [Fact]
    public void Does_not_add_or_reload_resource_dictionaries_when_switching()
    {
        Wpf.Run(() =>
        {
            ResourceDictionary resources = Palette();
            resources.MergedDictionaries.Add(new ResourceDictionary());
            var service = new ApplicationThemeService(resources, new FakeSystemTheme(EffectiveTheme.Light));

            service.Apply(ApplicationTheme.Light);

            Assert.Single(resources.MergedDictionaries);
            Assert.Null(resources.MergedDictionaries[0].Source);
        });
    }

    [Fact]
    public void System_is_resolved_again_when_settings_are_applied_again()
    {
        Wpf.Run(() =>
        {
            ResourceDictionary resources = Palette();
            var system = new FakeSystemTheme(EffectiveTheme.Dark);
            var service = new ApplicationThemeService(resources, system);

            service.Apply(ApplicationTheme.System);
            Assert.Equal(EffectiveTheme.Dark, service.Effective);

            system.Value = EffectiveTheme.Light;
            service.Apply(ApplicationTheme.System);

            Assert.Equal(EffectiveTheme.Light, service.Effective);
            Assert.Equal(Colors.White, ((SolidColorBrush)resources["WindowBackgroundBrush"]).Color);
        });
    }

    [Fact]
    public void Applying_a_theme_notifies_non_wpf_surfaces_after_resources_change()
    {
        Wpf.Run(() =>
        {
            ResourceDictionary resources = Palette();
            var service = new ApplicationThemeService(resources, new FakeSystemTheme(EffectiveTheme.Light));
            System.Windows.Media.Color colorSeenBySubscriber = Colors.Transparent;

            service.ThemeChanged += (_, _) =>
                colorSeenBySubscriber = ((SolidColorBrush)resources["WindowBackgroundBrush"]).Color;

            service.Apply(ApplicationTheme.Dark);

            Assert.Equal(Colors.Black, colorSeenBySubscriber);
        });
    }

    [Fact]
    public void System_theme_tracks_windows_changes_but_explicit_themes_do_not()
    {
        Wpf.Run(() =>
        {
            ResourceDictionary resources = Palette();
            var system = new ObservableFakeSystemTheme(EffectiveTheme.Light);
            using var service = new ApplicationThemeService(resources, system);

            service.Apply(ApplicationTheme.System);
            system.Set(EffectiveTheme.Dark);
            Assert.Equal(EffectiveTheme.Dark, service.Effective);

            service.Apply(ApplicationTheme.Light);
            system.Set(EffectiveTheme.Dark);
            Assert.Equal(EffectiveTheme.Light, service.Effective);
        });
    }

    [Fact]
    public void Both_dictionaries_define_the_complete_semantic_palette()
    {
        Wpf.Run(() =>
        {
            ResourceDictionary resources = Palette();
            _ = new ApplicationThemeService(resources, new FakeSystemTheme(EffectiveTheme.Light));

            Assert.All(
                ApplicationThemeService.SemanticResourceKeys,
                key =>
                {
                    Assert.IsType<SolidColorBrush>(resources[$"Dark.{key}"]);
                    Assert.IsType<SolidColorBrush>(resources[$"Light.{key}"]);
                }
            );
        });
    }

    /// <summary><c>Dark.</c>-prefixed keys are Black; <c>Light.</c>-prefixed keys are White. Unprefixed
    /// keys are absent until <see cref="ApplicationThemeService.Apply"/> writes them, matching production
    /// resources: neither <c>Dark.xaml</c> nor <c>Light.xaml</c> defines an unprefixed key.</summary>
    private static ResourceDictionary Palette()
    {
        var resources = new ResourceDictionary();

        foreach (string key in ApplicationThemeService.SemanticResourceKeys)
        {
            resources[$"Dark.{key}"] = new SolidColorBrush(Colors.Black);
            resources[$"Light.{key}"] = new SolidColorBrush(Colors.White);
        }

        return resources;
    }

    private sealed class FakeSystemTheme(EffectiveTheme value) : ISystemThemePreference
    {
        public EffectiveTheme Value { get; set; } = value;

        public EffectiveTheme Read() => Value;
    }

    private sealed class ObservableFakeSystemTheme(EffectiveTheme value)
        : ISystemThemePreference,
            ISystemThemeChangeSource
    {
        private EffectiveTheme _value = value;

        public event EventHandler? Changed;

        public EffectiveTheme Read() => _value;

        public void Set(EffectiveTheme value)
        {
            _value = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
