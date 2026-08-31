# Third-party notices

PCMode source code, documentation, and assets are proprietary except where a file or bundled component states otherwise. See [LICENSE](LICENSE).

This inventory reflects the current project files and restored dependency graphs. Development/test packages are listed separately from components used by production code. A release must still be audited against the files actually published or installed.

## Production managed dependency

| Component | Version | License | Use |
|---|---:|---|---|
| [OpenRGB.NET](https://www.nuget.org/packages/OpenRGB.NET/3.1.1) | 3.1.1 | MIT | OpenRGB SDK client used by `PCMode.Engine`. |

The current `PCMode.Audio.*`, Picker, and Tray production projects have no additional third-party NuGet package references. They use .NET and Windows platform APIs directly.

OpenRGB.NET does not include the OpenRGB server. PCMode connects only to a separately installed server on `127.0.0.1:6742`.

## Direct development and test dependencies

The repository currently has two Microsoft test-platform version sets because the older Engine, Picker, and Tray tests and the newer audio tests do not use the same package versions.

| Package | Version used | License |
|---|---|---|
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | 17.14.1; 18.9.0 | MIT |
| [xunit](https://www.nuget.org/packages/xunit) | 2.9.3 | Apache License 2.0 |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio) | 3.1.4; 3.1.5 | Apache License 2.0 |

These packages are used to build and run tests and are not intended to be included in normal PCMode application output.

## Resolved test dependencies

The restored NuGet graphs also contain the following transitive test components:

| Package | Version used | License |
|---|---|---|
| Microsoft.CodeCoverage | 17.14.1; 18.9.0 | MIT |
| Microsoft.TestPlatform.ObjectModel | 17.14.1; 18.9.0 | MIT |
| Microsoft.TestPlatform.TestHost | 17.14.1; 18.9.0 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |
| xunit.abstractions | 2.0.3 | Apache License 2.0 |
| xunit.analyzers | 1.18.0 | Apache License 2.0 |
| xunit.assert | 2.9.3 | Apache License 2.0 |
| xunit.core | 2.9.3 | Apache License 2.0 |
| xunit.extensibility.core | 2.9.3 | Apache License 2.0 |
| xunit.extensibility.execution | 2.9.3 | Apache License 2.0 |

Transitive versions can change when direct package references or lock inputs change. Re-create this inventory from the restored assets before release.

## Microsoft platform and driver components

PCMode currently targets .NET 10 and Windows. The native virtual-audio driver project targets the Windows kernel-mode driver toolset with:

- Windows SDK/WDK `10.0.28000.0`;
- Audio Class Extensions (ACX) `1.1`;
- Kernel-Mode Driver Framework (KMDF) `1.33`;
- Microsoft driver libraries including `acxstub.lib` and `libcntpr.lib`.

These are Microsoft platform/build components, not code owned by PCMode. Framework-dependent output relies on the installed .NET runtime; self-contained publishing or an installer may redistribute Microsoft files. Any distributed runtime, WDK redistributable, catalog, co-installer, or installer component must retain the applicable Microsoft license and notice materials.

## External products and protocols

PCMode interoperates with products that are not bundled with the repository or application output, including:

- the separately installed OpenRGB server and supported RGB hardware;
- LG webOS televisions over their local WebSocket/Wake-on-LAN interfaces;
- Windows display, audio, DDC/CI, power, registry, process, XInput, and shell interfaces;
- applications and URI handlers selected by the user.

Interoperability does not grant redistribution rights. Do not bundle an external server, vendor utility, firmware, SDK binary, trademarked asset, or other product without separately reviewing its license and distribution terms.

## Release audit

Before producing a release:

1. Inventory the exact managed assemblies, native binaries, driver package, runtime, installer files, and assets being distributed.
2. Confirm every component's exact version and license from the resolved build or publish output.
3. Include all required license, copyright, attribution, and third-party notice files.
4. Confirm optional integrations are invoked rather than redistributed.
5. Recheck the driver kit, signing, catalog, and installer redistribution terms for the chosen release toolchain.

No component should be added to a release solely because its license appears compatible; redistribution, modification, linking, trademark, and notice obligations must be reviewed for the actual packaging.
