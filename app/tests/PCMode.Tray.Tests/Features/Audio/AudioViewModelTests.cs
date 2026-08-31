using System.ComponentModel;
using PCMode.Engine;
using PCMode.Tray.Elements.Audio;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Features.Audio;

/// <summary>
/// The Audio page's editor, over a real configuration file.
/// </summary>
/// <remarks>
/// The integration the rest of the audio work rests on: what the page shows is
/// what <see cref="AudioProvider"/> parsed, and what it saves is a document that
/// same provider reads back unchanged. A page that quietly dropped a route's
/// delay or its required flag on the way through would look right and play
/// wrong.
///
/// No Host and no sound card are involved. The provider these view models build
/// has no audio runtime -- see <see cref="IAudioRuntime.None"/> -- so its targets
/// report as unavailable, which is exactly the state a machine without the
/// PCMode audio subsystem is in.
/// </remarks>
public sealed class AudioViewModelTests
{
    private const string Configuration = """
        {
          "schemaVersion": 1,
          "providers": {
            "audio": {
              "devices": {
                "Speakers": "Desk speakers (Realtek(R) Audio)",
                "Soundbar": "LG Soundbar (Intel(R) Display Audio)"
              },
              "targets": {
                "Party": { "outputs": [
                  { "output": "Soundbar", "delayMs": 0, "gain": 1, "volume": 0.5 },
                  { "output": "Speakers", "delayMs": 40, "gain": 0.85 }
                ] }
              }
            }
          },
          "spaces": [ { "id": "desk", "name": "Desk", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": {} }
          ] } ]
        }
        """;

    private const string ConfigurationWithProfile = """
        {
          "schemaVersion": 1,
          "providers": {
            "audio": {
              "devices": {
                "Speakers": "Desk speakers (Realtek(R) Audio)",
                "Soundbar": "LG Soundbar (Intel(R) Display Audio)"
              },
              "targets": {
                "Party": { "outputs": [
                  { "output": "Soundbar", "delayMs": 0, "gain": 1, "volume": 0.5, "profile": "Warm" },
                  { "output": "Speakers", "delayMs": 40, "gain": 0.85 }
                ] }
              },
              "profiles": {
                "Warm": { "preampGainDb": -6, "bands": [] }
              }
            }
          },
          "spaces": [ { "id": "desk", "name": "Desk", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": {} }
          ] } ]
        }
        """;

    [Fact]
    public void Lists_only_audio_targets_in_the_selector_and_every_output_as_a_route_choice()
    {
        // The left selector holds Audio Targets only -- the named outputs
        // ("Soundbar", "Speakers") are configured under Audio outputs below,
        // not selected here. They still populate OutputChoices, since a
        // target's own routes are chosen from them.
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);

        Assert.Equal(["Party"], viewModel.SelectorItems.Select(item => item.Title));
        Assert.Equal(["Soundbar", "Speakers"], viewModel.OutputChoices);
    }

    [Fact]
    public void Shows_a_target_route_for_route_exactly_as_it_was_written()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        Assert.True(viewModel.IsTargetSelected);
        Assert.Equal("Party", viewModel.TargetName);
        Assert.Equal(2, viewModel.Routes.Count);

        Assert.Equal("Soundbar", viewModel.Routes[0].Output);
        Assert.Equal(0, viewModel.Routes[0].DelayMs);
        Assert.Equal(0.5, viewModel.Routes[0].Volume);

        Assert.Equal("Speakers", viewModel.Routes[1].Output);
        Assert.Equal(40, viewModel.Routes[1].DelayMs);

        // One Advanced card per route, in route order. The reference route
        // is the baseline the others align to, so it shows no alignment card
        // of its own.
        AudioProfileCard profileCard = viewModel.ProfileCards[0];
        Assert.Equal(viewModel.Routes[0].ProfileName, profileCard.Title);
        Assert.Equal("Soundbar", profileCard.Subtitle);

        Assert.Equal(2, viewModel.AlignmentCards.Count);
        Assert.False(viewModel.AlignmentCards[0].IsVisible);
        Assert.True(viewModel.AlignmentCards[1].IsVisible);
        Assert.Same(viewModel.Routes[1], viewModel.AlignmentCards[1].Fields);
        Assert.Equal(0.85, viewModel.Routes[1].Gain);

        // Speakers' route left "volume" out of the configuration, but the
        // Windows volume field is always active now -- it loads with a
        // default rather than nothing to show.
        Assert.Equal(AudioRouteRow.DefaultVolume, viewModel.Routes[1].Volume);

        // Loading is not editing: the Save button must not be live because
        // somebody clicked a name in the list.
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.False(viewModel.CanSave);
    }

    [Fact]
    public void Saves_a_calibration_change_that_the_provider_then_reads_back()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);
        // This provider has no audio runtime (see the class remarks), so the
        // target can never be the one actually running and AudioViewModel's
        // live push never fires -- only the save path is exercised here. See
        // AudioTargetTests.Recalibrate_resolves_the_route_s_endpoint_fresh_and_hands_it_to_the_runtime
        // for the live-push half.

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        viewModel.Routes[1].DelayMs = 68;
        viewModel.Routes[1].Gain = 0.75;

        Assert.True(viewModel.HasUnsavedChanges);
        Assert.True(viewModel.CanSave);
        Assert.True(viewModel.TrySaveChanges());

        AudioRouteSettings saved = Read(root).Routes[1];

        Assert.Equal(68, saved.DelayMs);
        Assert.Equal(0.75, saved.Gain);
    }

    [Fact]
    public void Shows_the_alignment_section_only_for_two_or_more_routes_with_the_first_route_as_reference()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        Assert.True(viewModel.ShowAlignment);
        Assert.Equal("Soundbar", viewModel.ReferenceOutputName);
        Assert.True(viewModel.Routes[0].IsReference);
        Assert.False(viewModel.Routes[1].IsReference);

        viewModel.RemoveRoute(viewModel.Routes[1]);

        Assert.False(viewModel.ShowAlignment);
        Assert.True(viewModel.Routes[0].IsReference);
    }

    [Fact]
    public void Selecting_the_other_reference_reorders_routes_and_moves_the_alignment_to_the_non_reference_output()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow previousReference = viewModel.Routes[0];
        AudioRouteRow selectedReference = viewModel.Routes[1];

        selectedReference.DelayMs = 68;
        selectedReference.Gain = 0.75;

        viewModel.SelectReferenceRoute(selectedReference);

        Assert.Same(selectedReference, viewModel.Routes[0]);
        Assert.True(selectedReference.IsReference);
        Assert.Equal(0, selectedReference.DelayMs);
        Assert.Equal(1.0, selectedReference.Gain);

        Assert.Same(previousReference, viewModel.Routes[1]);
        Assert.False(previousReference.IsReference);
        Assert.Equal(68, previousReference.DelayMs);
        Assert.Equal(0.75, previousReference.Gain);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void A_route_s_level_offset_reads_as_zero_dB_at_unity_gain()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        // The reference route was saved with gain 1.0 (unity): the alignment
        // section reads that as its baseline 0 dB, with no separate
        // relative-to-reference computation needed.
        Assert.Equal(0.0, viewModel.Routes[0].LevelOffsetDb, 1);

        viewModel.Routes[1].Gain = 2.0;

        Assert.Equal(6.0, viewModel.Routes[1].LevelOffsetDb, 1);
    }

    [Fact]
    public void Adds_and_removes_a_route_within_the_two_the_page_offers()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        // Two outputs are configured and both are already used, so there is
        // nothing left to add.
        Assert.False(viewModel.CanAddRoute);
        Assert.True(viewModel.CanRemoveRoute);

        viewModel.RemoveRoute(viewModel.Routes[1]);

        Assert.Single(viewModel.Routes);
        Assert.True(viewModel.CanAddRoute);

        // And one route is the minimum: a target with none is not a target.
        Assert.False(viewModel.CanRemoveRoute);

        Assert.True(viewModel.TrySaveChanges());
        Assert.Equal(["Soundbar"], Read(root).Outputs);
    }

    [Fact]
    public void Route_pickers_offer_each_output_at_most_once()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow soundbar = viewModel.Routes[0];
        AudioRouteRow speakers = viewModel.Routes[1];

        Assert.Equal(["Soundbar"], soundbar.AvailableSelections);
        Assert.Equal(["Speakers"], speakers.AvailableSelections);
        Assert.NotSame(viewModel.OutputChoices, soundbar.AvailableSelections);
        Assert.NotSame(soundbar.AvailableSelections, speakers.AvailableSelections);

        viewModel.RemoveRoute(speakers);

        Assert.Equal(["Soundbar", "Speakers"], soundbar.AvailableSelections);

        viewModel.AddRoute();

        Assert.Equal("Speakers", viewModel.Routes[1].Output);
        Assert.Equal(["Soundbar"], soundbar.AvailableSelections);
        Assert.Equal(["Speakers"], viewModel.Routes[1].AvailableSelections);
    }

    [Fact]
    public void Refuses_to_save_a_target_whose_name_is_already_an_output()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);
        // A mode names one value for the audio resource, and it would mean two
        // different things.

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        viewModel.TargetName = "Speakers";

        Assert.NotNull(viewModel.ValidationProblem);
        Assert.False(viewModel.CanSave);
    }

    [Fact]
    public void Creates_a_new_target_that_cannot_be_activated_before_it_is_saved()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        Assert.Null(viewModel.CreateTarget("Kitchen"));

        Assert.True(viewModel.IsTargetSelected);
        Assert.True(viewModel.IsNewTarget);
        Assert.Equal("Kitchen", viewModel.TargetName);
        Assert.Single(viewModel.Routes);

        // Nothing to activate: the target does not exist in the document yet, so
        // the provider has no such value.
        Assert.False(viewModel.CanActivateSelected);

        Assert.True(viewModel.CanSave);
        Assert.True(viewModel.TrySaveChanges());

        Assert.Equal(2, ReadAll(root).Count);
    }

    [Fact]
    public void Create_refuses_an_empty_name_and_creates_nothing()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        int resourceCount = viewModel.SelectorItems.Count;

        Assert.Equal(
            "Give the audio target a name.",
            viewModel.CreateTarget("   "));

        Assert.False(viewModel.IsNewTarget);
        Assert.Equal(resourceCount, viewModel.SelectorItems.Count);
    }

    [Fact]
    public void Create_refuses_a_name_already_used_by_a_target_or_an_output()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);

        // Same validation RenameSelected/DuplicateSelected already apply --
        // a target cannot share a name with an existing target or an output.
        Assert.NotNull(viewModel.CreateTarget("Party"));
        Assert.False(viewModel.IsNewTarget);

        Assert.NotNull(viewModel.CreateTarget("Soundbar"));
        Assert.False(viewModel.IsNewTarget);
    }

    [Fact]
    public void Creating_a_target_marks_the_page_dirty_so_navigating_away_is_guarded()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);

        Assert.False(viewModel.HasUnsavedChanges);

        Assert.Null(viewModel.CreateTarget("Kitchen"));

        // The shell's navigation guard (ConfirmDiscardIfDirty) and the
        // footer's Save/Discard buttons all read this one flag -- an unsaved
        // new target has to trip it exactly like any other unsaved edit,
        // matching Displays' StartNewLayout and Rgb's CreateScene.
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void Renaming_a_saved_target_defers_the_write_to_disk_until_save()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        Assert.Null(viewModel.RenameSelected("Lounge"));

        Assert.Equal("Lounge", viewModel.TargetName);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.False(viewModel.IsNewTarget);

        // Nothing on disk yet -- still "Party", not "Lounge" -- until Save.
        Assert.Equal(["Party"], ReadAll(root).Select(target => target.Name));

        Assert.True(viewModel.TrySaveChanges());

        Assert.Equal(["Lounge"], ReadAll(root).Select(target => target.Name));
    }

    [Fact]
    public void Duplicating_the_open_target_clones_its_working_copy_into_an_unsaved_draft()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        // An edit still unsaved on the source: the duplicate must clone what
        // is on screen, not only what is on disk.
        viewModel.Routes[0].DelayMs = 12;

        Assert.Null(viewModel.DuplicateSelected("Party copy"));

        Assert.True(viewModel.IsNewTarget);
        Assert.Equal("Party copy", viewModel.TargetName);
        Assert.Equal(2, viewModel.Routes.Count);
        Assert.Equal(12, viewModel.Routes[0].DelayMs);

        // Nothing written for either target yet.
        Assert.Single(ReadAll(root));

        Assert.True(viewModel.TrySaveChanges());

        Assert.Equal(2, ReadAll(root).Count);
        Assert.Contains(ReadAll(root), target => target.Name == "Party copy");
    }

    [Fact]
    public async Task Live_status_refresh_does_not_remove_an_unsaved_new_target()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);
        // The reported regression: creating a target and leaving it unsaved
        // for the length of one live-status tick (~3s on the real page) used
        // to rebuild the resource list from the last-saved document, which
        // does not contain the new target yet -- silently dropping it from
        // the selector as if Create had never been clicked.

        AudioViewModel viewModel = Load(root);
        Assert.Null(viewModel.CreateTarget("Kitchen"));

        await viewModel.RefreshLiveStatusAsync();

        Assert.True(viewModel.IsTargetSelected);
        Assert.True(viewModel.IsNewTarget);
        Assert.Equal("Kitchen", viewModel.TargetName);
        Assert.Single(viewModel.Routes);
        Assert.Contains(
            viewModel.SelectorItems,
            item => item.Title == "Kitchen");
    }

    [Fact]
    public void Says_the_audio_engine_is_missing_rather_than_blaming_the_target()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);
        // A machine without the PCMode audio subsystem still uses its single
        // outputs normally, and the page has to say which of the two is wrong.

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        Assert.False(viewModel.IsTargetRunning);
        Assert.Contains("not available", viewModel.EngineText!, StringComparison.Ordinal);
        Assert.Contains("cannot play", viewModel.TargetStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_device_and_runtime_refresh_does_not_discard_an_audio_target_edit()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");
        viewModel.Routes[1].DelayMs = 72;

        await viewModel.RefreshLiveStatusAsync();

        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal(72, viewModel.Routes[1].DelayMs);
    }

    [Fact]
    public void VolumePercent_reads_and_writes_the_same_value_as_Volume_in_percent()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];

        Assert.Equal(50, row.VolumePercent);

        row.VolumePercent = 65;

        Assert.Equal(0.65, row.Volume, 3);
        Assert.Equal(65, row.VolumePercent);
    }

    [Fact]
    public void VolumePercent_clamps_to_the_0_to_100_range()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];

        row.VolumePercent = 150;
        Assert.Equal(100, row.VolumePercent);
        Assert.Equal(1.0, row.Volume);

        row.VolumePercent = -20;
        Assert.Equal(0, row.VolumePercent);
        Assert.Equal(0.0, row.Volume);
    }

    [Fact]
    public void Windows_volume_is_always_saved_even_for_a_route_that_had_none_configured()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);
        // Windows volume has no opt-in toggle any more -- every route always
        // carries one, so saving one now always writes an explicit value even
        // for a route ("Speakers") that was never given one before.

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        viewModel.Routes[1].DelayMs = 55;

        Assert.True(viewModel.TrySaveChanges());

        AudioRouteSettings saved = Read(root).Routes[1];

        Assert.Equal(55, saved.DelayMs);
        Assert.Equal(AudioRouteRow.DefaultVolume, saved.Volume);
    }

    [Fact]
    public void Editing_preamp_recomputes_headroom_from_the_combined_response()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        viewModel.LoadProfileEditor(row);

        // Warm has no bands: a flat profile's own peak is 0 dB, so headroom is
        // just -(Preamp + 0) -- Preamp came back as -6 from the saved profile.
        Assert.Equal(0.0, row.PeakResponseDb, 3);
        Assert.Equal(6.0, row.HeadroomDb, 3);
        Assert.False(row.IsHeadroomWarning);
        Assert.NotEmpty(row.ResponseCurve);

        row.ProfilePreampGainDb = 3;

        Assert.Equal(-3.0, row.HeadroomDb, 3);
        Assert.True(row.IsHeadroomWarning);

        row.ProfilePreampGainDb = -1;

        Assert.Equal(1.0, row.HeadroomDb, 3);
        Assert.False(row.IsHeadroomWarning);
    }

    [Fact]
    public void Adding_an_eq_band_recomputes_peak_response_from_the_combined_curve_not_one_band_alone()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        viewModel.LoadProfileEditor(row);
        row.ProfilePreampGainDb = 0;

        viewModel.AddProfileBand(row);

        Assert.Single(row.BandCurves);

        double peakBeforeBoost = row.PeakResponseDb;

        row.ProfileBands[0].GainDb = 6;

        double peakAfterBoost = row.PeakResponseDb;

        // The combined curve's own peak rose with the band, and headroom and
        // its warning are derived from that same combined peak (R_max),
        // exactly -(Preamp + peakAfterBoost) with Preamp at 0 here -- not
        // read off the band in isolation.
        Assert.True(peakAfterBoost > peakBeforeBoost + 4.0);
        Assert.True(peakAfterBoost <= 6.01);
        Assert.Equal(-peakAfterBoost, row.HeadroomDb, 6);
        Assert.True(row.IsHeadroomWarning);
    }

    [Fact]
    public void Editing_preamp_raises_PropertyChanged_for_the_bound_headroom_text_and_status()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);
        // A GUI bug that Editing_preamp_recomputes_headroom_from_the_combined_response
        // above cannot catch: HeadroomDb/IsHeadroomWarning updating correctly
        // internally is not enough if the page's TextBlocks are bound to the
        // derived HeadroomText/HeadroomStatusText/HeadroomStatusState and
        // nothing raises PropertyChanged for those -- WPF would keep showing
        // whatever they read as when the row was first created (0.0 dB, "OK").

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        viewModel.LoadProfileEditor(row);

        var raised = new HashSet<string>();
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is string name)
            {
                raised.Add(name);
            }
        };

        // Warm has no bands, so Preamp alone crossing 0 dB total flips the
        // warning -- Peak response itself does not move (it is band-only).
        row.ProfilePreampGainDb = 5;

        Assert.Contains(nameof(AudioRouteRow.HeadroomText), raised);
        Assert.Contains(nameof(AudioRouteRow.HeadroomStatusText), raised);
        Assert.Contains(nameof(AudioRouteRow.HeadroomStatusState), raised);
    }

    [Fact]
    public void Editing_an_eq_band_raises_PropertyChanged_for_the_bound_peak_response_text()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        viewModel.LoadProfileEditor(row);
        viewModel.AddProfileBand(row);

        var raised = new HashSet<string>();
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is string name)
            {
                raised.Add(name);
            }
        };

        row.ProfileBands[0].GainDb = 6;

        Assert.Contains(nameof(AudioRouteRow.PeakResponseText), raised);
    }

    [Fact]
    public void Editing_a_processing_profile_dirties_the_page_without_touching_route_wiring()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        viewModel.LoadProfileEditor(row);

        Assert.False(viewModel.HasUnsavedChanges);

        row.ProfilePreampGainDb = -15;

        // The page is dirty -- CanActivateSelected, the footer's Save/Discard
        // buttons and the shell's navigation guard all read this one flag --
        // but nothing about the route itself (which output, which profile it
        // references) was touched.
        Assert.True(row.IsProfileDirty);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal("Soundbar", row.Output);
        Assert.Equal("Warm", row.SavedProfileName);
    }

    [Fact]
    public async Task Live_status_refresh_does_not_collapse_or_discard_an_open_profile_edit()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);
        // The reported bug: opening the processing-profile editor, changing
        // Preamp, and leaving it unsaved for the length of one live-status
        // tick (~3s on the real page) used to rebuild the route list from the
        // last-saved document, collapsing the editor and reverting the edit.

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        viewModel.LoadProfileEditor(row);
        row.ProfilePreampGainDb = -15;

        await viewModel.RefreshLiveStatusAsync();

        Assert.True(row.IsProfileEditorExpanded);
        Assert.Equal(-15, row.ProfilePreampGainDb);
        Assert.True(row.IsProfileDirty);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal("Soundbar", row.Output);
        Assert.Equal("Warm", row.ProfileName);
        Assert.Same(row, viewModel.Routes[0]);
    }

    [Fact]
    public async Task Live_status_refresh_does_not_blank_or_dirty_an_untouched_route_s_output_and_profile()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);
        // The regression the previous fix introduced: RefreshLiveStatusOnly
        // rereads the device/profile lists into OutputChoices/ProfileChoices
        // every tick without rebuilding Routes any more, and a route's Output/
        // ProfileName ComboBox stays bound, TwoWay, to those same collections.

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];

        Assert.Equal("Soundbar", row.Output);
        Assert.Equal("Warm", row.ProfileName);
        Assert.False(viewModel.HasUnsavedChanges);

        await viewModel.RefreshLiveStatusAsync();

        Assert.Equal("Soundbar", row.Output);
        Assert.Equal("Warm", row.ProfileName);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task Live_status_refresh_patches_output_and_profile_choices_without_ever_removing_the_selected_value()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);
        // The actual mechanism behind the blanking: ObservableCollection.Clear
        // followed by re-adding the same strings still raises a Reset (or a
        // Remove) that WPF's TwoWay SelectedItem binding reacts to immediately,
        // nulling the selection before the re-add ever happens. Asserting on
        // CollectionChanged, rather than only on the row's final Output/
        // ProfileName, is what would have caught that -- the row-level checks
        // alone can pass even when the collection briefly went empty, because
        // nothing in this test's object graph has a real ComboBox subscribed
        // to push a transient null back through the binding.

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        string output = row.Output!;
        string profileName = row.ProfileName;

        bool outputChoiceDisappeared = false;
        bool profileChoiceDisappeared = false;

        viewModel.OutputChoices.CollectionChanged += (_, _) =>
            outputChoiceDisappeared |= !viewModel.OutputChoices.Contains(output);

        viewModel.ProfileChoices.CollectionChanged += (_, _) =>
            profileChoiceDisappeared |= !viewModel.ProfileChoices.Contains(profileName);

        await viewModel.RefreshLiveStatusAsync();

        Assert.False(outputChoiceDisappeared);
        Assert.False(profileChoiceDisappeared);
    }

    [Fact]
    public void Saving_the_target_also_saves_a_dirty_processing_profile()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        viewModel.LoadProfileEditor(row);
        row.ProfilePreampGainDb = -15;

        Assert.True(viewModel.CanSave);
        Assert.True(viewModel.TrySaveChanges());

        Assert.False(row.IsProfileDirty);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Equal(-15f, ReadProfile(root, "Warm").PreampGainDb);
    }

    [Fact]
    public void Discarding_reverts_an_unsaved_processing_profile_edit()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithProfile);

        AudioViewModel viewModel = Load(root);
        viewModel.SelectKey("target:Party");

        AudioRouteRow row = viewModel.Routes[0];
        viewModel.LoadProfileEditor(row);
        row.ProfilePreampGainDb = -15;

        viewModel.DiscardChanges();

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Equal(-6f, ReadProfile(root, "Warm").PreampGainDb);

        AudioRouteRow reloaded = viewModel.Routes[0];
        viewModel.LoadProfileEditor(reloaded);

        Assert.Equal(-6, reloaded.ProfilePreampGainDb);
    }

    private static AudioViewModel Load(TemporaryConfigurationRoot root)
    {
        var viewModel = new AudioViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        return viewModel;
    }

    private static AudioTargetSettings Read(TemporaryConfigurationRoot root) =>
        Assert.Single(ReadAll(root), target => string.Equals(target.Name, "Party", StringComparison.Ordinal));

    private static IReadOnlyList<AudioTargetSettings> ReadAll(TemporaryConfigurationRoot root)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);

        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root.Path, json);

        _ = ModeConfiguration.Parse(json, providers);

        return providers.OfType<AudioProvider>().Single().Targets;
    }

    private static AudioProcessingProfileSettings ReadProfile(TemporaryConfigurationRoot root, string name)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);

        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root.Path, json);

        _ = ModeConfiguration.Parse(json, providers);

        AudioProcessingProfileSettings? profile = providers.OfType<AudioProvider>().Single().Profile(name);

        Assert.NotNull(profile);

        return profile;
    }
}
