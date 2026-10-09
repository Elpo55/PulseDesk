# Security policy

## Supported versions

Security fixes are made for the latest release only. Update to the
[latest version](https://github.com/Elpo55/Sysora/releases/latest) before reporting a problem.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Report it privately through GitHub:
**Security** tab of the repository → **Report a vulnerability**.

Include, if you can:

- the Sysora version and the Windows version,
- what an attacker could do, and under which conditions,
- the steps to reproduce it, or a proof of concept.

You will get an answer within 7 days. Once the problem is confirmed, a fix is prepared and released, and you are
credited in the release notes unless you prefer otherwise.

## What Sysora does, and what it never does

Knowing this helps judge whether a behavior is a vulnerability:

- Sysora runs with the rights of the current user and never asks for administrator rights (only the installer does,
  when installing for all users).
- It never opens a network connection: no telemetry, no account, no update check, no server.
- It reads system information through documented Windows APIs, and reads (never writes) game launchers' files.
- It writes only to `%LOCALAPPDATA%\Sysora` (settings, logs, history), to the reports you export where you choose to
  save them and, when "Start with Windows" is on, to its own value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- It never changes, moves or deletes the user's files; the large-file scan is read-only.
- It ends a process only when the user asks and confirms, and always refuses critical Windows processes.

## Sensitive data in reports and logs

Logs and exported reports can contain application names, file paths (which include your user name) and your PC's
hardware description. Review them before attaching them to a public issue.
