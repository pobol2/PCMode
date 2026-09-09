using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Suppresses Windows notifications while a mode wants the room left alone.
/// </summary>
/// <remarks>
/// OFF UNLESS SOMEBODY ASKED FOR IT
///
/// This is the only resource here that is disabled by default, and the reason is
/// the interface rather than the feature. What Windows actually checks before
/// showing a toast is an undocumented registry value; it works, it has moved
/// between Windows builds, and there is no supported way to ask for the same
/// thing. A resource that quietly wrote to it on every machine that installed
/// this would be making a change nobody consented to, in a place nobody would
/// think to look.
///
/// So an unconfigured machine reports <see cref="AvailabilityState.Disabled"/>,
/// which is the settled answer that lets an optional resource be left out
/// cleanly and reported. A mode that requires it on a machine that has not asked
/// for it is refused by name, which is the correct outcome: the mode asked for
/// something this machine has been told not to do.
///
/// ONE INVERSION, IN ONE PLACE
///
/// The setting says whether toasts are allowed. Do Not Disturb is the opposite of
/// it. That flip happens here and nowhere else -- see
/// <see cref="INotificationSetting"/> -- because two places to invert a boolean is
/// one place to get it backwards, and getting it backwards means a mode that asks
/// for quiet turning notifications on.
/// </remarks>
public sealed class DoNotDisturbProvider : IConfigurableProvider, IConvergentProvider, IAvailableProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "doNotDisturb";

    /// <summary>Where this provider's settings live in the document.</summary>
    public const string SettingsPath = "providers.doNotDisturb";

    public const string On = "On";
    public const string Off = "Off";

    private readonly INotificationSetting _setting;

    private bool _enabled;

    private bool _refresh = true;

    public DoNotDisturbProvider(INotificationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _setting = setting;
    }

    public string Key => ResourceKey;

    public string DisplayName => "Do Not Disturb";

    /// <summary>Whether this machine has opted in.</summary>
    public bool Enabled => _enabled;

    public bool TryConfigure(JsonElement? settings, out string? problem)
    {
        _enabled = false;
        _refresh = true;

        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            // Not configured is the ordinary case and the default one. It leaves
            // the resource disabled rather than refusing the document: a machine
            // that never mentions Do Not Disturb has not made a mistake.
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}' must be an object.";
            return false;
        }

        bool enabled = false;
        bool refresh = true;

        foreach (JsonProperty field in element.EnumerateObject())
        {
            if (field.NameEquals("enabled"))
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problem = $"'{SettingsPath}.enabled' must be true or false.";
                    return false;
                }

                enabled = field.Value.ValueKind == JsonValueKind.True;
            }
            else if (field.NameEquals("refresh"))
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problem = $"'{SettingsPath}.refresh' must be true or false.";
                    return false;
                }

                refresh = field.Value.ValueKind == JsonValueKind.True;
            }
            else
            {
                // Refused rather than ignored, like every other unknown field.
                problem = $"'{SettingsPath}' does not understand '{field.Name}'. It takes 'enabled' and 'refresh'.";
                return false;
            }
        }

        _enabled = enabled;
        _refresh = refresh;
        problem = null;
        return true;
    }

    /// <summary>
    /// Whether this machine has agreed to have its notification setting written.
    /// </summary>
    /// <remarks>
    /// <see cref="AvailabilityState.Disabled"/> and not
    /// <see cref="AvailabilityState.Unavailable"/>: the setting is there on every
    /// Windows machine, and what is missing is permission rather than the
    /// facility. They are told apart because they call for different things --
    /// one is a machine that cannot do this, the other is a machine that has been
    /// told not to, and only the second is fixed by editing a file.
    /// </remarks>
    public ResourceAvailability AvailabilityOf(string target) =>
        _enabled
            ? ResourceAvailability.Available
            : ResourceAvailability.Disabled(
                $"Do Not Disturb is switched off. It writes a Windows notification setting that "
                    + $"Microsoft does not document, so it stays off until '{SettingsPath}.enabled' says otherwise."
            );

    /// <summary>
    /// Written once, and confirmed, with a second attempt for a value that did
    /// not take.
    /// </summary>
    /// <remarks>
    /// Verified because it genuinely can be: the same value that was written is
    /// the one that is read back. The readback is immediate -- there is nothing
    /// to negotiate and nothing to wait for -- so the poll is short and the
    /// second attempt exists only for a write that lost a race with whatever else
    /// was touching the same value.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) =>
        new()
        {
            Verify = true,
            MaxAttempts = 2,
            RetryInterval = TimeSpan.FromMilliseconds(500),
            PollInterval = TimeSpan.FromMilliseconds(100),
        };

    public bool IsValidValue(string value, out string? problem)
    {
        if (IsOn(value) || IsOff(value))
        {
            problem = null;
            return true;
        }

        problem = $"'{value}' is not a Do Not Disturb state. Use {On} or {Off}.";
        return false;
    }

    public string? Read()
    {
        if (!_enabled)
        {
            // Not asked for, so not looked at. A disabled resource that read the
            // registry anyway would be inspecting a machine that has told it not
            // to touch this at all.
            return null;
        }

        bool? allowed;

        try
        {
            allowed = _setting.ToastsAllowed();
        }
        catch (Exception)
        {
            return null;
        }

        return allowed switch
        {
            true => Off,
            false => On,
            _ => null,
        };
    }

    public void Apply(string value, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            // Unreachable through the engine, which drops a disabled resource
            // while planning. Kept because the whole point of this one being
            // opt-in is that nothing writes the value without being asked.
            throw new InvalidOperationException(
                $"Do Not Disturb is switched off on this machine. Set '{SettingsPath}.enabled' to true first."
            );
        }

        bool quiet = IsOn(value);

        if (!quiet && !IsOff(value))
        {
            // No fall-through to a default. A typo like 'Onn' used to mean "not
            // On", which wrote the opposite of what was meant and left the
            // machine noisy in the one mode that asked for silence.
            throw new ArgumentException($"'{value}' is not a Do Not Disturb state.", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();

        _setting.AllowToasts(!quiet);

        if (_refresh)
        {
            try
            {
                _setting.Refresh();
            }
            catch (Exception)
            {
                // The setting has already been changed. Failing the resource
                // because Windows could not be nudged into noticing promptly
                // would report a write that worked as a write that did not --
                // and the readback, which is the thing that decides, is about to
                // confirm it either way.
            }
        }
    }

    private static bool IsOn(string value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value.Trim(), On, StringComparison.OrdinalIgnoreCase);

    private static bool IsOff(string value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value.Trim(), Off, StringComparison.OrdinalIgnoreCase);
}
