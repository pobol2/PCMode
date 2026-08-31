using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Tests.Presentation.Configuration;

/// <summary>
/// The standard entity and collection actions, as the commands a Layout binds
/// to.
/// </summary>
/// <remarks>
/// This dispatch used to live in the shared page control's click handlers,
/// where the dialog paths could not be tested at all: Create/Rename/Duplicate/
/// Delete opened a real blocking modal, so only their null-delegate fallbacks
/// were ever exercised. Now the prompts arrive through
/// <see cref="IConfigurationPrompts"/> and every path is reachable, including
/// the guard that runs before an action would discard an edit in progress.
///
/// No WPF here: none of this was ever about arrangement.
/// </remarks>
public sealed class ConfigurationPageCommandsTests
{
    [Fact]
    public void A_null_delegate_hides_its_action_rather_than_disabling_it()
    {
        var page = new FakePage
        {
            ActionsValue = new EntityActions { EntityNoun = "sample" },
        };

        Assert.False(page.Commands.ShowCreate);
        Assert.False(page.Commands.ShowRename);
        Assert.False(page.Commands.ShowDuplicate);
        Assert.False(page.Commands.ShowDelete);
        Assert.False(page.Commands.ShowActivate);
        Assert.False(page.Commands.ShowActions);
        Assert.False(page.Commands.Create.CanExecute(null));
    }

    [Fact]
    public void A_supplied_delegate_shows_its_action_and_can_run()
    {
        var page = new FakePage
        {
            ActionsValue = new EntityActions
            {
                EntityNoun = "sample",
                Create = _ => null,
                Rename = _ => null,
                Duplicate = _ => null,
                Delete = () => { },
            },
        };

        Assert.True(page.Commands.ShowCreate);
        Assert.True(page.Commands.ShowActions);
        Assert.True(page.Commands.Create.CanExecute(null));
    }

    [Fact]
    public void Create_asks_for_a_name_and_applies_it()
    {
        var prompts = new ScriptedPrompts { Name = "Desk" };
        string? created = null;
        var page = new FakePage(prompts)
        {
            ActionsValue = new EntityActions
            {
                EntityNoun = "space",
                Create = name => { created = name; return null; },
            },
        };

        page.Commands.Create.Execute(null);

        Assert.Equal("Desk", created);
        Assert.Equal("New space", prompts.LastTitle);
        Assert.Contains("space", prompts.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_is_abandoned_when_the_unsaved_changes_guard_refuses()
    {
        var prompts = new ScriptedPrompts { Name = "Desk", AllowDiscard = false };
        bool created = false;
        var page = new FakePage(prompts)
        {
            Dirty = true,
            ActionsValue = new EntityActions
            {
                EntityNoun = "space",
                Create = _ => { created = true; return null; },
            },
        };

        page.Commands.Create.Execute(null);

        Assert.False(created);
        Assert.Null(prompts.LastTitle);
    }

    [Fact]
    public void Rename_offers_the_current_name_and_does_not_run_the_discard_guard()
    {
        // Rename edits the entity already open, so it is not navigating away
        // from the in-progress edit and must not offer to discard it.
        var prompts = new ScriptedPrompts { Name = "Desk 2", AllowDiscard = false };
        var page = new FakePage(prompts)
        {
            Dirty = true,
            ActionsValue = new EntityActions
            {
                EntityNoun = "space",
                CurrentDisplayName = "Desk",
                Rename = _ => null,
            },
        };

        page.Commands.Rename.Execute(null);

        Assert.Equal("Desk", prompts.LastInitialValue);
        Assert.Equal("Rename space", prompts.LastTitle);
    }

    [Fact]
    public void Duplicate_offers_a_copy_name()
    {
        var prompts = new ScriptedPrompts { Name = "Desk copy" };
        var page = new FakePage(prompts)
        {
            ActionsValue = new EntityActions
            {
                EntityNoun = "space",
                CurrentDisplayName = "Desk",
                Duplicate = _ => null,
            },
        };

        page.Commands.Duplicate.Execute(null);

        Assert.Equal("Desk copy", prompts.LastInitialValue);
    }

    [Fact]
    public void Delete_runs_only_after_the_confirmation_is_accepted()
    {
        var prompts = new ScriptedPrompts { ConfirmAnswer = false };
        int deletes = 0;
        var page = new FakePage(prompts)
        {
            ActionsValue = new EntityActions
            {
                EntityNoun = "space",
                CurrentDisplayName = "Desk",
                Delete = () => deletes++,
            },
        };

        page.Commands.Delete.Execute(null);
        Assert.Equal(0, deletes);

        prompts.ConfirmAnswer = true;
        page.Commands.Delete.Execute(null);
        Assert.Equal(1, deletes);
        Assert.Contains("Desk", prompts.LastConfirmMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_and_discard_follow_the_page_state()
    {
        var page = new FakePage { Dirty = true, Saveable = true };

        Assert.True(page.Commands.Save.CanExecute(null));
        Assert.True(page.Commands.Discard.CanExecute(null));

        page.Commands.Discard.Execute(null);
        Assert.Equal(1, page.Discards);

        page.Commands.Save.Execute(null);
        Assert.Equal(1, page.Saves);
    }

    [Fact]
    public void The_add_label_is_generated_from_the_item_noun()
    {
        var page = new FakePage
        {
            EssentialActionsValue = new EssentialCollectionActions
            {
                ItemNoun = "output",
                Add = () => { },
                CanAdd = true,
            },
        };

        Assert.Equal("Add output", page.Commands.AddEssentialLabel);
        Assert.True(page.Commands.ShowAddEssential);
        Assert.True(page.Commands.AddEssential.CanExecute(null));
    }

    [Fact]
    public void Remove_hands_the_row_itself_back_to_the_page()
    {
        object? removed = null;
        var page = new FakePage
        {
            EssentialActionsValue = new EssentialCollectionActions
            {
                ItemNoun = "output",
                Remove = item => removed = item,
            },
        };

        var row = new object();
        page.Commands.RemoveEssential.Execute(row);

        Assert.Same(row, removed);
    }

    [Fact]
    public void The_empty_state_text_is_generated_from_the_entity_noun()
    {
        var page = new FakePage
        {
            ActionsValue = new EntityActions { EntityNoun = "lighting scene" },
        };

        Assert.Contains("lighting scene", page.Commands.EmptyStateText, StringComparison.Ordinal);
    }

    private sealed class FakePage : ConfigurationPageViewModelBase
    {
        public FakePage(IConfigurationPrompts? prompts = null)
            : base(new FakeWorkspace(), prompts)
        {
        }

        public bool Dirty { get; init; }

        public bool Saveable { get; init; }

        public int Saves { get; private set; }

        public int Discards { get; private set; }

        public override bool HasUnsavedChanges => Dirty;

        public override bool CanSave => Saveable;

        public EntityActions? ActionsValue { get; init; }

        public override EntityActions? Actions => ActionsValue;

        public EssentialCollectionActions? EssentialActionsValue { get; init; }

        public override EssentialCollectionActions? EssentialActions => EssentialActionsValue;

        public override void SelectKey(object key) => SelectedKey = key;

        public override void Refresh()
        {
        }

        protected override string? SaveChangesCore()
        {
            Saves++;
            return null;
        }

        protected override void DiscardChangesCore() => Discards++;
    }

    /// <summary>Answers every prompt from a script, and records what it was asked.</summary>
    private sealed class ScriptedPrompts : IConfigurationPrompts
    {
        public string? Name { get; init; }

        public bool AllowDiscard { get; init; } = true;

        public bool ConfirmAnswer { get; set; }

        public string? LastTitle { get; private set; }

        public string? LastMessage { get; private set; }

        public string? LastInitialValue { get; private set; }

        public string? LastConfirmMessage { get; private set; }

        public void PromptForName(string title, string message, string initialValue, Func<string, string?> apply)
        {
            LastTitle = title;
            LastMessage = message;
            LastInitialValue = initialValue;

            if (Name is not null)
            {
                apply(Name);
            }
        }

        public void PromptForNameAsync(string title, string message, string initialValue, Func<string, Task<string?>> apply)
        {
            LastTitle = title;
            LastMessage = message;
            LastInitialValue = initialValue;
        }

        public bool Confirm(string message)
        {
            LastConfirmMessage = message;
            return ConfirmAnswer;
        }

        public bool ConfirmDiscardIfDirty(IUnsavedChanges source) =>
            !source.HasUnsavedChanges || AllowDiscard;
    }
}
