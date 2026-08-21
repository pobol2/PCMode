using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Television;

/// <summary>A friendly setup-page label paired with the value stored in configuration.</summary>
public sealed record TelevisionChoice(string Label, string Value);

internal static class TelevisionUiChoices
{
    private static readonly IReadOnlyDictionary<string, string> SignalLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uhddeepcolor_hdmi{n}"] = "HDMI Deep Color",
            ["hdmipcmode_hdmi{n}"] = "PC mode",
            ["gamemode_hdmi{n}"] = "Game mode",
            ["gameoptimization_hdmi{n}"] = "Game Optimizer",
            ["gameoptimization"] = "Game Optimizer (global)",
            ["freesyncoled_hdmi{n}"] = "AMD FreeSync",
            ["freesync"] = "FreeSync (global)",
            ["inputoptimization"] = "Input latency",
        };

    /// <summary>
    /// Every socket the engine will accept, and nothing this file decided.
    /// </summary>
    /// <remarks>
    /// Offering fewer than the parser accepts is how a configured HDMI 5 becomes
    /// an input the screen shows as blank and silently rewrites on the next save.
    /// The list of sockets is <see cref="LgTvSettings.HdmiInputIds"/>'s to state.
    /// </remarks>
    public static readonly IReadOnlyList<TelevisionChoice> HdmiInputs =
        [.. LgTvSettings.HdmiInputIds.Select(id =>
            new TelevisionChoice($"HDMI {id["HDMI_".Length..]}", id))];

    public static readonly IReadOnlyList<TelevisionChoice> SignalSettings =
        [.. LgTvProfile.SignalNames.Keys.Select(key =>
            new TelevisionChoice(
                SignalLabels.TryGetValue(key, out string? label) ? label : key,
                key))];

    public static readonly IReadOnlyList<TelevisionChoice> PictureModes =
    [
        new("Standard", "standard"),
        new("Vivid", "vivid"),
        new("Cinema", "cinema"),
        new("Game", "game"),
        new("Filmmaker Mode", "filmMaker"),
        new("HDR Standard", "hdrStandard"),
        new("HDR Vivid", "hdrVivid"),
        new("HDR Cinema", "hdrCinema"),
        new("HDR Game", "hdrGame"),
        new("HDR Filmmaker Mode", "hdrFilmMaker"),
    ];

    public static IReadOnlyList<TelevisionChoice> SignalValues(string key) =>
        [.. LgTvProfile.SignalValuesFor(key)
            .Select(value => new TelevisionChoice(FriendlyValue(value), value))];

    private static string FriendlyValue(string value) => value.ToLowerInvariant() switch
    {
        "auto" => "Automatic",
        "on" => "On",
        "off" => "Off",
        "standard" => "Standard",
        "boost" => "Boost",
        _ => value,
    };
}

/// <summary>One input row being edited: a name a mode can use, and the HDMI socket it means.</summary>
public sealed class TelevisionInputRow : ObservableEditableRow
{
    private string _name;
    private string _input;

    public TelevisionInputRow(string name, string input, Action onChanged)
        : base(onChanged)
    {
        _name = name;
        _input = input;
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Input
    {
        get => _input;
        set => Set(ref _input, value);
    }

    public IReadOnlyList<TelevisionChoice> HdmiChoices => TelevisionUiChoices.HdmiInputs;
}

/// <summary>One key/value row inside a profile's signal or picture-settings half.</summary>
public sealed class TelevisionSettingRow : ObservableEditableRow
{
    private string _key;
    private string _value;

    public TelevisionSettingRow(string key, string value, Action onChanged)
        : base(onChanged)
    {
        _key = key;
        _value = value;
    }

    public string Key
    {
        get => _key;
        set
        {
            if (Set(ref _key, value))
            {
                OnPropertyChanged(nameof(SignalValueChoices));
            }
        }
    }

    public IReadOnlyList<TelevisionChoice> SignalKeyChoices => TelevisionUiChoices.SignalSettings;

    public IReadOnlyList<TelevisionChoice> SignalValueChoices =>
        TelevisionUiChoices.SignalValues(_key);

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }
}

/// <summary>
/// One picture profile row: a name, its picture mode, which input it is for, and
/// its two advanced settings lists.
/// </summary>
/// <remarks>
/// WHICH INPUT THIS PROFILE IS FOR
///
/// <see cref="Input"/> is this profile's own answer to which HDMI socket its
/// advanced HDMI options are about -- "PC Gaming" says "PC", "Console Gaming"
/// says "Console" -- replacing a single answer the whole television used to give
/// once for every profile. <see cref="_inputNames"/> is a live view of the
/// television's currently configured inputs rather than a copy taken once, so
/// <see cref="InputChoices"/> and <see cref="HasMultipleInputs"/> stay correct as
/// inputs are added, removed or renamed elsewhere on the page.
/// </remarks>
public sealed class TelevisionProfileRow : ObservableEditableRow
{
    private readonly Func<IReadOnlyList<string>> _inputNames;
    private readonly Action _onChanged;
    private string _name;
    private string _pictureMode;
    private string _input;

    public TelevisionProfileRow(
        string name,
        string pictureMode,
        string input,
        Func<IReadOnlyList<string>> inputNames,
        Action onChanged)
        : base(onChanged)
    {
        _name = name;
        _pictureMode = pictureMode;
        _input = input;
        _inputNames = inputNames;
        _onChanged = onChanged;
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string PictureMode
    {
        get => _pictureMode;
        set
        {
            if (Set(ref _pictureMode, value))
            {
                OnPropertyChanged(nameof(PictureModeChoices));
            }
        }
    }

    /// <summary>Which of the television's inputs this profile's HDMI options target.</summary>
    public string Input
    {
        get => _input;
        set => Set(ref _input, value);
    }

    /// <summary>The television's currently configured input names.</summary>
    public IReadOnlyList<string> InputChoices => _inputNames();

    /// <summary>
    /// Whether this profile needs to be told which input it is for.
    /// </summary>
    /// <remarks>
    /// With at most one input there is nothing to choose between, so the field
    /// stays out of the way on the ordinary machine that has only ever plugged
    /// its television in once.
    /// </remarks>
    public bool HasMultipleInputs => _inputNames().Count > 1;

    /// <summary>Friendly presets plus any model-specific value already configured.</summary>
    public IReadOnlyList<TelevisionChoice> PictureModeChoices
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_pictureMode) ||
                TelevisionUiChoices.PictureModes.Any(choice =>
                    string.Equals(choice.Value, _pictureMode, StringComparison.OrdinalIgnoreCase)))
            {
                return TelevisionUiChoices.PictureModes;
            }

            return [.. TelevisionUiChoices.PictureModes, new TelevisionChoice(_pictureMode, _pictureMode)];
        }
    }

    /// <summary>Settings written before the display switches -- see <see cref="LgTvProfile"/>.</summary>
    public ObservableCollection<TelevisionSettingRow> Signal { get; } = [];

    /// <summary>Settings written after the display switches, alongside the picture mode.</summary>
    public ObservableCollection<TelevisionSettingRow> Settings { get; } = [];

    public void AddSignal() =>
        Signal.Add(new TelevisionSettingRow("uhddeepcolor_hdmi{n}", "on", _onChanged));

    public void RemoveSignal(TelevisionSettingRow row)
    {
        if (Signal.Remove(row))
        {
            _onChanged();
        }
    }

    public void AddSetting() => Settings.Add(new TelevisionSettingRow(string.Empty, string.Empty, _onChanged));

    public void RemoveSetting(TelevisionSettingRow row)
    {
        if (Settings.Remove(row))
        {
            _onChanged();
        }
    }

    /// <summary>Refreshes what this row shows about the television's inputs.</summary>
    /// <remarks>
    /// <see cref="InputChoices"/> and <see cref="HasMultipleInputs"/> read live
    /// through <see cref="_inputNames"/>, but WPF only redraws a binding when it
    /// is told the property changed -- so the page calls this on every row
    /// whenever the television's own input list might have.
    /// </remarks>
    internal void NotifyInputsChanged()
    {
        OnPropertyChanged(nameof(InputChoices));
        OnPropertyChanged(nameof(HasMultipleInputs));
    }
}

/// <summary>One television shown in the selector.</summary>
public sealed class TelevisionListItem : ObservableObject, IConfigurationSelectorItem
{
    private bool _isDefault;
    private string _powerState = "…";

    public TelevisionListItem(string name, bool isDefault)
    {
        Name = name;
        _isDefault = isDefault;
    }

    public string Name { get; }

    object IConfigurationSelectorItem.Key => Name;

    public bool IsDefault
    {
        get => _isDefault;
        internal set => SetProperty(ref _isDefault, value);
    }

    /// <summary>Compact live state shown in the selector.</summary>
    public string PowerState
    {
        get => _powerState;
        internal set => SetProperty(ref _powerState, value);
    }
}
