# Security policy

TidyWindow changes Windows settings, removes apps and runs a helper with administrator rights, so security reports are taken seriously. Thank you for reporting problems responsibly.

## Supported versions

| Version | Supported |
| --- | --- |
| 5.x (the current WinUI 3 app on `main`) | ✅ Security fixes go into the next release |
| 4.x and earlier (the [`legacy`](https://github.com/Cosmos-0118/TidyWindow/tree/legacy) branch) | ❌ No longer developed |

Fixes are released as a new version rather than patched into old ones, so please update to the [latest release](https://github.com/Cosmos-0118/TidyWindow/releases/latest) before reporting.

## How to report a vulnerability

**Please do not open a public issue, pull request or discussion for a security problem.**

1. **Preferred:** report it privately through GitHub at [Security → Report a vulnerability](https://github.com/Cosmos-0118/TidyWindow/security/advisories/new). This keeps the report, the discussion and the fix in one private place.
2. **If you cannot use GitHub:** email [dhanush13244@gmail.com](mailto:dhanush13244@gmail.com) with "TidyWindow security" in the subject.

Please include:

- The TidyWindow version (Settings → Current version) and how it was installed (all-users installer, per-user installer or portable ZIP).
- Your Windows version and build.
- What an attacker needs to start with (for example a standard account on the same PC, or a file they can place somewhere) and what they gain.
- Steps to reproduce, and a proof of concept if you have one.

Review any logs you attach: they can contain local paths and package names.

## What happens next

TidyWindow is maintained by a small team, so these are targets rather than guarantees:

- We aim to acknowledge a report within **7 days**.
- We will confirm whether we can reproduce it and keep you updated while we work on a fix.
- Once a fixed release is out, we publish a GitHub security advisory and credit you, unless you would rather stay anonymous.

Please give us a reasonable chance to release a fix before you share details publicly.

## Scope

Reports are most useful for the parts of TidyWindow that cross a privilege boundary or that users must trust:

- **The elevated helper** (`TidyWindow.CleanHost.exe`) and the named pipe the app uses to talk to it, including ways for another program or account to connect, send requests or make it run something outside its catalogs.
- **Catalog and allowlist checks** in Cleanup, Quiet, Tune, Debloat, Fixes and Lab, including ways to make an action do more than its catalog entry describes.
- **Saved originals and undo records**, including ways to tamper with records that the helper restores with administrator rights.
- **Package manager and PATH handling**, including ways to make the app or the helper start a program from a location a standard account can change.
- **Release integrity**: the installer, the portable ZIP, `SHA256SUMS.txt` and the build provenance attestation.

### Known limitations (not vulnerabilities)

These are documented trade-offs. Reports about them are welcome only if they show something beyond what is described:

- **Per-user and portable installs.** The README explains that a per-user install or the portable ZIP puts the app, including the elevated helper, in a folder your account can write to. Any program running as you could then replace the helper before your next approved administrator prompt. The default all-users install in Program Files does not have this problem.
- **Running as administrator.** The app refuses to run elevated and closes; this is intended.
- **Logs.** Logs replace the account name but can still contain local paths, package names and stack traces. This is documented.
- **Third-party tools.** Vulnerabilities in WinGet, Chocolatey, Scoop, Everything or the packages they install should be reported to those projects. Tell us too if TidyWindow makes the problem worse.

Out of scope: attacks that already need administrator rights on the PC, physical access to an unlocked PC, social engineering, and denial of service against your own machine.
