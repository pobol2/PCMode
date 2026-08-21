# Security policy

## Supported versions

Security fixes are provided for the latest available PCMode release. Pre-release builds and older versions may be replaced by a newer build instead of receiving a separate patch.

## Security model

PCMode is a per-user Windows desktop application. It should not require administrative privileges for normal operation and should access only its own application data, configured local devices and applications, and explicitly enabled release services.

Security-sensitive implementation must follow these rules:

- Store configuration, state, logs, backups, and diagnostics under PCMode-owned per-user locations.
- Protect credentials and pairing material with Windows user-scoped protection where available.
- Never place credentials, tokens, or pairing secrets in exported configuration, logs, diagnostics, or support bundles.
- Treat local-network devices and their responses as untrusted input.
- Validate configuration and imported data before applying machine-changing operations.
- Keep native, process, filesystem, network, and update operations behind narrow auditable boundaries.
- Verify downloaded release artifacts before installation.
- Run with the least privilege required and do not auto-elevate.

The exact network connections made by PCMode depend on the providers and update features the user configures. Merely opening the application should not expose an unauthenticated remote-control service.

## Reporting a vulnerability

Do not publish credentials, exploit details, private logs, or other sensitive information in a public issue.

If private vulnerability reporting is enabled for the repository, use the repository's **Security** page to submit a private report. Otherwise, open a public issue containing only a request for a private reporting channel and enough non-sensitive context for the maintainer to respond.

A useful private report includes:

- the affected PCMode version;
- the relevant Windows version and provider;
- steps to reproduce or a proof of concept;
- the potential impact;
- whether credentials or personal data may have been exposed;
- any suggested mitigation.

Ordinary reliability bugs that do not involve a security boundary may be reported through the normal issue tracker.

There is currently no bug-bounty program or guaranteed response SLA.

