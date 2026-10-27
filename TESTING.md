# Testing

## Automated

```bash
dotnet test --project tests/ClaudeMultiAccount.Tests
```

Runs without admin rights and changes nothing on the machine (it works only in `%TEMP%` and in a throwaway `HKCU\Software\ClaudeMultiAccount.Tests` key). It covers:

| Area | What is checked |
| --- | --- |
| `Secret` | DPAPI round trip, tampered/garbage data gives a friendly error, generated passwords (length, every character class, no ambiguous or shell-breaking characters, uniqueness) |
| `LocalUsers` | user-name suggestion (accents, symbols, 20-character limit, numeric suffixes, real accounts skipped), SID lookup, folder sharing with `icacls` (grant, skip when already allowed, inheritance, deny rules, revoke) |
| `ProfileStore` | save/load of every field, removal, corrupt file kept as `.corrupt`, older files, concurrent saves |
| `ProcessRunner`, `RunAs` | exit codes, stdout/stderr, big output, timeout kills the process, 1024-character limit, wrong password message |
| `ScriptRunner` | PowerShell quoting against injection, the wrapper's exit code and error log, non-ASCII text |
| `ProfileSetup` | the Git script run against a temporary global config (safe.directory, drive roots, identity, idempotency, Git missing), update-on-launch decision, missing installer |
| `ClaudeDesktop` | detection on this machine, launch script (ASCII, CRLF, exit 9 when Claude is missing), terminal command (length, quoting, valid PowerShell) |
| `SecureFolder` | ProgramData folders are trusted only when they are real folders (junctions are never followed) owned by Administrators or SYSTEM, with the sentinel file in each work folder |
| `Elevation` | the elevated entry point refuses other accounts, users the app didn't create, the requesting account itself, unknown operations, bad package names and broken request files |
| `StartupRegistration`, `S`, UI models | Run key on/off, every string in both languages with matching `{0}` placeholders, profile cards and step states |

### Admin integration tests

`AdminIntegrationTests` creates a real hidden Windows user and goes through the whole lifecycle: create, first sign-in, steps run as that user, writing to a shared folder, Git, edit (folders and administrator flag), repair of the work folder, removal, and a failed create that must leave nothing behind. It also checks the ProgramData protections in a throwaway root: a junction or a folder planted there by another account is moved aside without being followed, and a work folder turned into a junction is replaced. Finally, it checks that the profile user can't remove its work folder or the sentinel inside it. They run on every CI build. To run them locally, from a terminal opened with **Run as administrator**:

```bat
set CMA_ADMIN_TESTS=1
dotnet test --project tests/ClaudeMultiAccount.Tests -- --filter-class ClaudeMultiAccount.Tests.AdminIntegrationTests
```

Add `set CMA_ADMIN_TESTS_CLAUDE=1` to also register Claude Desktop (MSIX) in the test user and launch it. That can provision the Claude package for every user of the machine. The test user is removed at the end, even when a check fails.

## UI smoke test

[`tests/ui-smoke.ps1`](tests/ui-smoke.ps1) starts the built exe and drives its real window through Windows UI Automation. It needs no admin rights and never creates, edits or removes a profile. It only presses **Save** with names the form must refuse, and never clicks anything that asks for UAC.

| Area | What is checked |
| --- | --- |
| Start | the main window opens; one **Open Claude** button per account; the "Claude not found" banner matches whether Claude is installed |
| Single instance | a second start exits at once and brings the existing window forward, also when it was hidden in the tray |
| Settings | 4 menu items; **Start with Windows** matches the registry; **About** shows the exe's version |
| New profile | example hint; Windows user suggestion without accents and within 20 characters; existing default folders listed; 8 colors; administrator off and "open when done" on by default; empty name and name with quotes refused; **Cancel** closes |
| Tray | closing the window keeps the app in the tray; **Exit** quits; `--tray` starts without a window |
| Side effects | `profiles.json`, the startup entry and the local `claude-*` users are unchanged |

Build the exe as in the README (`dotnet publish … -o publish`), exit any running copy of the app from the tray, then run it with Windows PowerShell 5.1 from a signed-in desktop:

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File tests\ui-smoke.ps1 -Exe publish\ClaudeMultiAccount.exe
```

The app's windows open and close on their own for about a minute; don't use the mouse or keyboard meanwhile. The script prints one line per check and exits with 1 when any check fails. It refuses to start while the app is already running, so it never closes your own copy. It works with the system in English or Portuguese and on machines that already have profiles.

## Manual checklist (before a release)

The parts below need a person: UAC prompts, the tray icon menu, other apps (Explorer, browser, clipboard) and Claude's own sign-in. The UI smoke test already covers starting, single instance, the settings menu, the New profile form up to **Save**, closing to the tray and `--tray`.

1. Start the exe from Explorer (as a user would) and check SmartScreen's message for the unsigned file.
2. **New profile**: add/remove a folder, pick a color. Cancel the UAC prompt: back to the form, nothing created. Approve it: all four steps turn green and Claude opens in the profile.
3. Sign in to Claude in the profile with another account; close and **Open Claude** again: still signed in. Main account and profile open side by side.
4. **⋯ → Open terminal**: PowerShell titled `Claude - <name>`, in the first shared folder, `whoami` shows the hidden user, `git status` works in a repo.
5. **⋯ → Edit**: rename, change color, add a folder (UAC), toggle administrator (UAC + restart notice).
6. **⋯ → Repair**: finishes with "Done!"; also after deleting `%ProgramData%\ClaudeMultiAccount\<user>`.
7. **⋯ → Copy password**: clipboard has the password.
8. Close the window: the tray balloon shows once; right-click the tray icon: it opens each profile, **Show window** and **Exit** work.
9. **Settings → Start with Windows**: sign out and in, app starts in the tray only. **Settings → Open logs folder** and the banner's **Download** open Explorer and the browser.
10. **⋯ → Remove**: confirm, UAC; card disappears, the user is gone from `lusrmgr.msc`/`net user`, `C:\Users\<user>` is deleted (or the warning names it).
11. Windows in English and in Portuguese: texts follow the system language.
