## What changes

<!-- What this pull request changes for the user, and why. Link the issue it closes, if any ("Closes #12"). -->

## How it was tested

<!-- Tests added or run, and what you checked by hand (with `--demo` for interface changes). -->

## Checklist

- [ ] `dotnet build Sysora.slnx` and `dotnet test --project src/Sysora.Tests` pass
- [ ] New logic has tests
- [ ] Every new text is in `src/Sysora.Localization`, in English and French (or the French text is marked as missing above)
- [ ] No value is estimated or invented; what cannot be measured shows "Not available"
- [ ] No network access, telemetry or change to the user's files
- [ ] Documentation updated if the behavior changed
- [ ] Screenshots attached for interface changes
