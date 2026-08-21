# Main development guidelines

## Core concepts

- **Feature** — a self-contained functional capability of PCMode, such as Audio, Applications, Spaces, TV, or Diagnostics.
- **Page** — a user-facing screen for part of a feature. A feature may have one page, several pages, or no full page.
- **Page family** — a shared interaction and layout model used by multiple pages, such as `ListPageShell` or `ConfigurationPageShell`.

## Development principles

1. **Keep ownership clear.**  
   Feature-specific behavior stays in the feature. Shared application, page, lifecycle, state, navigation, or UI mechanics belong in shared infrastructure.

2. **Reuse established page families.**  
   Pages use the page family that matches their interaction model. Create a new family only when multiple real pages need a genuinely different shared structure or behavior.

3. **Use one application runtime.**  
   The Tray, Picker, Main UI, hotkeys, controller input, and other control surfaces use the same runtime. The runtime owns live application state and delegates execution to the Engine.

4. **Keep the architecture simple and explicit.**  
   Prefer straightforward inheritance, constructor injection, factories, and registration. Generalize only when multiple real features or pages demonstrate the need.

5. **Protect shared contracts with focused tests.**  
   Test behavior that multiple features depend on, not incidental UI implementation details.

## Core rules

> **Create a feature for a new capability. Create a page when that capability needs a distinct user-facing screen.**

> **Adding or modifying a feature should mostly mean implementing the feature itself, not rebuilding application mechanics.**

> **Shared by multiple real features or pages → move upward. Specific to one feature → keep local.**