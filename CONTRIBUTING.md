# Contributing to Sysora

Thank you for your interest in Sysora. Bug reports, translations, documentation fixes and code are all welcome.

## Before you start

- **Bugs and ideas**: open an [issue](https://github.com/Elpo55/Sysora/issues/new/choose) first, so we can agree on the
  approach before you spend time on code. Small fixes (typos, obvious bugs) can go straight to a pull request.
- **Security problems**: do not open a public issue; follow [SECURITY.md](SECURITY.md).
- Everyone taking part follows the [code of conduct](CODE_OF_CONDUCT.md).

## Setting up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Nothing else: the Windows App SDK comes
from NuGet.

```powershell
git clone https://github.com/Elpo55/Sysora.git
cd Sysora
dotnet build Sysora.slnx
dotnet test --project src/Sysora.Tests
dotnet run --project src/Sysora.App -- --demo
```

The application needs Windows 10 1809 or later. On Linux and macOS you can build and test the shared code (Core,
Localization, Infrastructure) with `dotnet test --project src/Sysora.Tests`; see [docs/platforms.md](docs/platforms.md).

[docs/development.md](docs/development.md) explains the commands, options, local data and conventions, and
[docs/architecture.md](docs/architecture.md) the design.

## What makes a good pull request

- **One topic per pull request**, with a clear title and a description of what changes for the user.
- **Tests** for new logic: Core logic is tested with simulated data and fake clocks, never with the real hardware.
- **Both languages**: every text shown to the user goes in `src/Sysora.Localization`, in English and in French
  (see [Translating Sysora](docs/development.md#translating-sysora)). If you cannot write the French text, say so in the
  pull request and it will be added before merging.
- **No invented values**: a metric that cannot be read is shown as "Not available", never estimated.
- **Privacy**: no network access, no telemetry, no account. Sysora never changes, moves or deletes the user's files.
- **Screenshots** for interface changes, taken in demo mode (`--demo`), in light or dark theme.
- **CI must pass**: the build treats warnings as errors, on Windows, Ubuntu and macOS.

## Commit messages

Write the first line as a short sentence in the imperative ("Add launcher detection for GOG Galaxy"), and explain the
reason in the body when it is not obvious. Keep unrelated changes in separate commits.

## Translations

Corrections to the French texts are very welcome, as are new languages. The steps are in
[Translating Sysora](docs/development.md#translating-sysora); the tests check that every text is translated and that
placeholders match.

## License

By contributing, you agree that your contributions are released under the [MIT license](LICENSE).
