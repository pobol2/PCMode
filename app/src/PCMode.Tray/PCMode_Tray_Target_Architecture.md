Yes. The clean separation is now:

```text
UI/
├─ Framework/    # how the grammar works
├─ Tree/         # where UI is allowed
└─ Components/   # terminal UI implementations
```

That removes the ambiguity in the previous version while keeping the tree authoritative. 

# PCMode Tray — Target Architecture

## Goal

PCMode Tray is built from **one strict recursive construction tree**.

The tree defines:

- what structural UI can exist;
- where it can exist;
- what may follow each structural node;
- which terminal UI components may appear at each leaf.

Application elements provide state, behaviour, and parameters.

They do not define their own UI structure.

---

# 1. Construction Grammar

```text
◆ Functional Block
  → ▣ Layout
    → ○ Region
      → ◆ child Functional Block
      or
      → ● Final UI Component
```

The recursive rule is:

```text
◆ → ▣ → ○ → ◆ → ▣ → ○ → ...
```

until a branch terminates:

```text
◆ → ▣ → ○ → ●
```

### Functional Block

Defines a reusable structural concept.

It declares:

- its layout;
- its regions;
- its required inputs.

### Layout

Arranges the regions of a Functional Block.

It does not choose what occupies them.

### Region

Defines a legal continuation in the construction tree.

It specifies which child Functional Blocks or Final UI Components are allowed.

### Final UI Component

Terminates structural recursion.

It receives parameters, state, bindings, and commands and renders the final UI.

---

# 2. Overall Architecture

```text
PCMode.Tray/
├─ App/
├─ Elements/
├─ Platform/
└─ UI/
   ├─ Framework/
   ├─ Tree/
   └─ Components/
```

---

# 3. App

`App/` owns application startup, composition, lifetime, and top-level orchestration.

```text
App/
├─ App.xaml
├─ App.xaml.cs
├─ CompositionRoot.cs
└─ ...
```

It does not define structural UI.

---

# 4. Elements

`Elements/` contains the application-specific runtime elements.

```text
Elements/
├─ Navigation/
├─ Home/
├─ Spaces/
├─ Displays/
├─ Audio/
├─ Rgb/
├─ Applications/
├─ Settings/
├─ Diagnostics/
├─ Picker/
├─ Setup/
└─ Dialogs/
```

An Element provides:

- state;
- data;
- collections;
- parameters;
- commands;
- behaviour.

An Element selects a legal node in the tree and supplies that node's inputs.

For example:

```text
Audio
   ↓
◆ ConfigurationPage
```

Audio may provide:

```text
Title
Configurations
SelectedConfiguration
EditorItems
SaveCommand
RescanCommand
```

It does not create the page structure.

---

# 5. Platform

`Platform/` owns Windows-specific integration.

```text
Platform/
├─ Input/
├─ Startup/
├─ TrayIcon/
├─ Native/
├─ Themes/
└─ ...
```

Examples:

- hotkeys;
- controller input;
- auto-start;
- single-instance handling;
- native Windows APIs;
- tray integration.

---

# 6. UI

The UI has three distinct responsibilities:

```text
UI/
├─ Framework/
├─ Tree/
└─ Components/
```

```text
Framework   = how construction works
Tree        = where UI may exist
Components  = what terminal UI is rendered
```

---

# 7. UI/Framework

`UI/Framework/` contains only the machinery required to implement and enforce the grammar.

```text
UI/Framework/
├─ FunctionalBlock.cs
├─ Region.cs
├─ RegionContract.cs
├─ FinalUiComponent.cs
└─ ...
```

The Framework handles:

- Functional Block realization;
- Layout application;
- Region discovery;
- contract validation;
- legal composition;
- hosting Final UI Components.

It contains no application-specific structure or component library.

---

# 8. UI/Tree

`UI/Tree/` is the **generic construction grammar of the Tray**.

Its filesystem mirrors the recursive structural tree.

```text
UI/Tree/
└─ Application/
   ├─ ApplicationBlock.cs
   ├─ ApplicationLayout.xaml
   │
   ├─ MainWindowRegion/
   │  └─ AppShell/
   │     ├─ AppShellBlock.cs
   │     ├─ AppShellLayout.xaml
   │     │
   │     ├─ NavigationRegion/
   │     │  └─ PageNavigation/
   │     │     ├─ PageNavigationBlock.cs
   │     │     ├─ PageNavigationLayout.xaml
   │     │     └─ ContentRegion/
   │     │
   │     └─ MainContentRegion/
   │        ├─ ListPage/
   │        │  ├─ ListPageBlock.cs
   │        │  ├─ ListPageLayout.xaml
   │        │  ├─ HeaderRegion/
   │        │  ├─ ContentRegion/
   │        │  └─ FooterRegion/
   │        │
   │        └─ ConfigurationPage/
   │           ├─ ConfigurationPageBlock.cs
   │           ├─ ConfigurationPageLayout.xaml
   │           ├─ HeaderRegion/
   │           ├─ SelectorRegion/
   │           ├─ EditorRegion/
   │           └─ FooterRegion/
   │
   ├─ PickerRegion/
   ├─ SetupRegion/
   └─ DialogRegion/
```

The tree contains generic structural concepts.

It does **not** contain:

```text
Audio/
Displays/
Home/
Spaces/
Rgb/
```

unless such a name genuinely represents a structural concept.

---

# 9. UI/Components

`UI/Components/` contains the implementations of terminal UI components.

For example:

```text
UI/Components/
├─ Inputs/
│  ├─ Selector.xaml
│  ├─ Slider.xaml
│  ├─ TextInput.xaml
│  └─ UnitInput.xaml
├─ Actions/
│  ├─ Button.xaml
│  └─ ActionGroup.xaml
├─ Status/
│  ├─ StatusBadge.xaml
│  └─ StatusText.xaml
├─ Visualizations/
│  ├─ ResponseGraph.cs
│  └─ MonitorArrangementPad.cs
└─ ...
```

Components are not structural branches.

They are reusable terminal implementations referenced by the tree.

The rule is:

```text
UI/Tree/
= WHERE a component is legal

UI/Components/
= WHAT that component renders
```

For example:

```text
○ VolumeRegion
└─ ● Slider
```

The structural location belongs to `UI/Tree/`.

The Slider implementation belongs to:

```text
UI/Components/Inputs/Slider.xaml
```

---

# 10. Connecting Elements to the Tree

An Element supplies:

```text
tree node
+
parameters
+
state
+
behaviour
```

For example:

```text
Elements/Audio
      ↓
◆ ConfigurationPage
```

The tree determines:

```text
◆ ConfigurationPage
└─ ▣ ConfigurationPageLayout
   ├─ ○ HeaderRegion
   ├─ ○ SelectorRegion
   ├─ ○ EditorRegion
   └─ ○ FooterRegion
```

The Element only supplies what those branches need.

---

# 11. Parameters and Components

Feature differences should primarily come from parameters.

For example:

```text
● Selector

label   = "Output"
choices = audioDevices
value   = selectedOutput
```

and:

```text
● Selector

label   = "Monitor"
choices = monitors
value   = selectedMonitor
```

Both use the same structural tree and the same terminal component.

Only the parameters differ.

---

# 12. Structural Variants

If the structure genuinely differs, the alternatives appear explicitly in the tree.

```text
○ SelectorRegion
├─ ◆ FlatSelector
├─ ◆ HierarchicalSelector
└─ ◆ AlternateSelector
```

An Element may choose one legal branch.

It cannot invent another structural path.

---

# 13. Migration

During migration, a generic `FinalUiComponent` may temporarily host existing UI as opaque content.

```text
◆ ConfigurationPage
→ ▣ ConfigurationPageLayout
→ ○ EditorRegion
→ ● FinalUiComponent
     Content = existing editor UI
```

The old UI beneath that boundary is temporarily opaque.

As the architecture is expanded, the boundary moves deeper:

```text
EditorRegion
→ ◆ ConfigurationEditor
→ ○ EssentialRegion
→ ● FinalUiComponent
```

Eventually the opaque legacy content disappears and the tree terminates in proper reusable components from `UI/Components/`.

---

# 14. Filesystem Rule

The three UI areas must remain distinct:

```text
UI/Framework/
```

contains construction machinery.

```text
UI/Tree/
```

physically mirrors the structural construction grammar.

```text
UI/Components/
```

contains reusable terminal UI implementations.

Do not organize `UI/Tree/` by feature.

Do not put structural blocks in `UI/Components/`.

Do not put application-specific state or behaviour in either.

---

# 15. Core Rules

1. There is one structural construction tree.
2. Every structural UI path resolves through `UI/Tree/`.
3. `UI/Tree/` mirrors that recursive grammar on disk.
4. `Elements/` provide state, behaviour, and parameters.
5. Elements do not define structural UI.
6. `UI/Framework/` contains only construction machinery.
7. `UI/Components/` contains terminal UI implementations only.
8. Functional Blocks define structural concepts and regions.
9. Layouts only arrange regions.
10. Regions constrain legal continuations.
11. Final UI Components terminate recursion.
12. Feature differences should primarily be parameters.
13. Structural variants must be explicit tree branches.
14. Custom structure must extend the tree rather than bypass it.
15. Avoid additional architectural layers unless concretely necessary.

---

# Principle

```text
Element
   ↓
selects legal tree node
   ↓
supplies parameters + state + behaviour
   ↓
◆ Functional Block
   ↓
▣ Layout
   ↓
○ Region
   ↓
◆ child Functional Block
   ↓
...
   ↓
● Final UI Component
   ↓
UI/Components implementation
```

> **Elements define the runtime content and behaviour.  
> The Tree defines the allowed structure.  
> Components implement the terminal UI.**