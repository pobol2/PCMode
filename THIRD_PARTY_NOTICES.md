# Third-party notices

PCMode source code, documentation, and assets are proprietary except where a file or bundled component states otherwise. See [LICENSE](LICENSE).

This notice distinguishes development/test dependencies from components distributed with an application release. A release must be audited against its actual contents; adding, bundling, or updating a dependency may require this file and the distributed license materials to be updated.

## Application dependencies

Packages referenced by `PCMode.Engine` and built into the application itself.

| Package | License |
|---|---|
| [OpenRGB.NET](https://www.nuget.org/packages/OpenRGB.NET/3.1.1) | MIT License |

`OpenRGB.NET` is a client for the OpenRGB SDK protocol; it does not bundle or require the separately installed OpenRGB server application (see External integrations below).

## Development and test dependencies

The test projects currently reference packages restored from NuGet. They are used to build and run the test suite and are not intended to be part of the normal PCMode application output.

| Package | License |
|---|---|
| [xunit](https://www.nuget.org/packages/xunit/2.9.3) | Apache License 2.0 |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.4) | Apache License 2.0 |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1) | MIT License |

These packages are not vendored into the repository. Their source code, trademarks, and licenses remain the property of their respective owners.

## .NET and Windows components

PCMode targets .NET and calls Windows platform APIs. Framework-dependent builds rely on components already installed on the user's system. If a release redistributes .NET runtime or other Microsoft components, the release packaging must preserve the license and third-party notice files supplied with those components.

Windows APIs and protocols are not copied third-party source code. Their use may nevertheless be subject to platform documentation, compatibility, trademark, or distribution terms.

## External integrations

PCMode may interoperate with separately installed applications or devices. Interoperability does not mean that their software is included with PCMode. An external component must not be bundled, installed, or modified by PCMode until its license and redistribution requirements have been reviewed.

## Compatibility interfaces

Some PCMode providers use interfaces or behavior that are not part of a stable, documented public control API. Current examples include Windows default-audio policy configuration, a Windows notification registry setting, and the LG webOS local control protocol.

The PCMode implementations of these compatibility boundaries are original project code. Their unsupported nature is documented because Windows or device updates may change their behavior; it is not a claim of ownership over the underlying platform or protocol.

## Updating this notice

Before producing a release:

1. inventory the packages and native files actually shipped;
2. record each component's exact version and license;
3. include required license, copyright, attribution, and notice text;
4. verify whether optional integrations are merely invoked or actually redistributed;
5. retain notices delivered with self-contained runtimes and installer components.

No third-party component may be added to a release solely because its license appears compatible; its redistribution, modification, linking, trademark, and notice obligations must be reviewed for the intended packaging.

