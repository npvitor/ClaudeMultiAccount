# Claude Multi-Account

[![build](https://github.com/npvitor/ClaudeMultiAccount/actions/workflows/build.yml/badge.svg)](https://github.com/npvitor/ClaudeMultiAccount/actions/workflows/build.yml)

**Run several Claude Desktop accounts side by side on Windows**: work and personal, two clients, whatever you need. Every account keeps its own sign-in, settings, history and Claude Code sessions, and all of them can be open at the same time.

[Leia em português](README.pt-BR.md)

> Unofficial, community project. Not affiliated with or endorsed by Anthropic.

## Why

Claude Desktop only signs in one account per Windows user, and it allows a single window per user. The reliable way to run a second account is to run it as a **second Windows user**. Doing that by hand means creating the account, sharing your project folders with it, installing Claude for it, fixing Git's "dubious ownership" errors, and typing a password into "Run as different user" every time.

This launcher does all of that for you, then gives you a one-click **Open Claude** button per account.

## Features

- **One click per account**, from the window or from the tray menu.
- **Hidden Windows users**: each profile gets its own local standard user. It doesn't show on the sign-in screen and isn't an administrator, unless you choose that for the profile.
- **No passwords to remember**: a random 28-character password is generated per profile and stored encrypted with Windows DPAPI, so only your Windows account can read it.
- **Your projects stay where they are**: choose which folders each profile can read and write. Common repo folders such as `source\repos` and `C:\Git` are suggested.
- **Git just works**: it marks your folders as `safe.directory` and can set a per-profile `user.name` and `user.email`. Personal commits use your personal email.
- **Claude stays updated**: when Claude Desktop (MSIX package) updates on your main account, the profile picks up the new version the next time you open it. With the classic installer, run **⋯ → Repair** with the new installer instead.
- **Terminal per profile**: a PowerShell window running as that account, for the Claude Code CLI, `git` or `gh auth login`.
- Starts with Windows (optional), runs from the tray, single instance.
- Portuguese and English UI, chosen from your Windows language.

## Requirements

- Windows 10 (2004 or later) or Windows 11, 64-bit.
- Your Windows account must be an **administrator**. Creating, changing or removing a profile asks for UAC approval; opening Claude normally does not (see [Known limitations](#known-limitations)).
- [Claude Desktop](https://claude.ai/download) installed on your main account.
- Optional: Git 2.46 or later, for the per-folder `safe.directory` entries.

## Install

1. Download `ClaudeMultiAccount.exe` from the [Releases](../../releases) page. It is a single file and needs no .NET installation.
2. Optional but recommended, since the app asks for administrator rights: check that the file is the one the build produced. The SHA-256 is in the release notes and in `ClaudeMultiAccount.exe.sha256`:
   ```powershell
   (Get-FileHash .\ClaudeMultiAccount.exe -Algorithm SHA256).Hash
   ```
3. Run it. Windows SmartScreen may warn about an unsigned app: choose **More info → Run anyway**.

## Usage

1. Click **New profile**, give it a name (for example *Personal*), pick a color and the project folders it may use, and optionally a Git name and email.
2. Approve the UAC prompt. The app creates the hidden user, prepares its Windows profile, installs Claude Desktop for it and configures Git. This usually takes 1 to 3 minutes.
3. Claude opens as the new profile. Sign in with the other account. You only do this once.
4. From then on, use **Open Claude** on each card, or the tray menu.

The **⋯** menu on each profile has:

| Option | What it does |
| --- | --- |
| Open terminal for this profile | PowerShell running as that account (Claude Code CLI, git, gh…) |
| Edit profile… | Rename, recolor, change folders, Git identity and the administrator option |
| Repair / update Claude in profile | Re-runs setup if something broke (may ask for UAC approval) |
| Copy Windows user password | For troubleshooting only; see [Security](#security) |
| Remove profile… | Deletes the hidden user and its data; your project files are not touched |

## How it works

| Step | Technique |
| --- | --- |
| Create user | `NetUserAdd` (standard user, password never expires), added to *Users*, hidden via `Winlogon\SpecialAccounts\UserList` |
| Share folders | `icacls <folder> /grant *SID:(OI)(CI)M` |
| Install Claude | MSIX install: `Add-AppxPackage -Register` of the package already on the machine, so nothing is downloaded. Classic install: runs the installer you pick inside the profile |
| Launch | `CreateProcessWithLogonW` (the API behind "Run as") starts a small hidden script, `launch-claude.cmd`, as the profile user. It starts the profile's own `claude-desktop.exe` and stays running while Claude is open, because Windows only lets the profile user use your desktop while that first process lives |
| Admin steps | The app relaunches itself elevated for that single task. The request goes through a temp file, never the command line, and the password stays DPAPI-encrypted |

Data locations:

- `%APPDATA%\ClaudeMultiAccount\profiles.json`: your profiles, with DPAPI-encrypted passwords.
- `%ProgramData%\ClaudeMultiAccount\<profile user>`: the launch script, short-lived setup scripts and logs from failed steps. Each folder is readable only by administrators, your account and that profile's user.
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `ClaudeMultiAccount`: only when **Start with Windows** is on.

## Security

Read this before using the app; it creates Windows accounts and asks for administrator rights.

- **Your Windows account holds the keys.** The profile passwords are encrypted with DPAPI for your account. Anything that runs as your account (you, or malware on it) can decrypt them and start programs as the profile users. The app doesn't protect profiles from your own account; it keeps them apart from each other and from other users of the machine.
- **Shared folders are writable by the profile.** Claude running in a profile can read and change everything in the folders you share with it, like it does in your own account. Share only what that profile needs.
- **Administrator profiles are real members of the Administrators group.** Claude still runs without elevated rights there, but a command it runs can get admin rights with a single **Yes** on a UAC prompt. Enable it only for profiles whose projects you trust.
- **"Copy Windows user password"** puts the password on the clipboard, where clipboard history (Win+V) and clipboard sync can keep it. Clear it after use.
- **Admin steps** are done by a second, elevated copy of the app that only accepts requests from your own account and only touches the `claude-*` users the app created.
- The exe is not code-signed yet (see [Code signing policy](#code-signing-policy)). Download it only from this repository's Releases page and compare the SHA-256 (see [Install](#install)).

To report a vulnerability, see [SECURITY.md](SECURITY.md). Please don't open a public issue for it.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> **Status:** signing through the SignPath Foundation program is being set up. Until it is active, releases are not signed; each one publishes the SHA-256 of its exe instead.

- Committers and reviewers: [npvitor](https://github.com/npvitor)
- Approvers: [npvitor](https://github.com/npvitor)

Only files built by this repository's GitHub Actions workflow from a release tag are signed, and every signing request is approved by hand. The workflow runs the tests before building.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

It starts and configures software that is not part of it and has its own privacy terms: Claude Desktop and the Claude Code CLI ([Anthropic privacy policy](https://www.anthropic.com/legal/privacy)), and Git or the GitHub CLI when you use them in a profile.

## Known limitations

- Windows asks for admin approval when you create a profile, change its folders or administrator option, repair it or remove it.
- If Windows refuses to register an updated Claude package for a profile, opening that profile asks for admin approval once to provision the package (see the last item below).
- Each profile is a real Windows user, so its browser, Git credentials, `gh` login and the Claude Code CLI are separate from yours. That's the point, but you sign in to those once per profile. Install the CLI from the profile's terminal: `irm https://claude.ai/install.ps1 | iex`.
- Features that rely on a single machine-wide component, such as the Cowork VM service, may only work in one account at a time.
- Profile users are standard users by default. When a profile's Claude runs a command that changes the whole machine, such as `dotnet workload install`, installing an SDK or trusting a machine-wide certificate, Windows shows a UAC prompt asking for an administrator's password. Either click **No** and run that command once from your main account in an elevated terminal, or tick **Make this user a Windows administrator** in the profile (also available under **Edit profile**). An administrator profile still runs Claude without elevated rights; those commands then ask for a simple **Yes** instead of a password. Only enable it for profiles whose projects you trust.
- Toast notifications from a profile's Claude may not appear, because Windows shows notifications for the signed-in user.
- The Claude package includes a Windows service, so Windows may refuse to install it for a standard user. When that happens the app asks for admin approval once and provisions the package for all users of the machine; any Windows user created later also gets Claude Desktop.

## Troubleshooting

- **"Secondary Logon service is disabled"**: open `services.msc`, set **Secondary Logon** to *Manual* and start it.
- **A setup step failed**: read the log in `%ProgramData%\ClaudeMultiAccount\<profile user>`, fix the cause, then use **⋯ → Repair**.
- **Git says "dubious ownership"**: run **⋯ → Edit profile → Save** so the folder is added again, and check that Git is version 2.46 or later.
- **Uninstalling**:
  1. Remove every profile from the app, which deletes the hidden users and their data.
  2. Turn off **Settings → Start with Windows**, so no startup entry points to a deleted exe.
  3. Exit the app from the tray, then delete the exe, `%APPDATA%\ClaudeMultiAccount` and `%ProgramData%\ClaudeMultiAccount`.
  4. If the app provisioned the Claude package for all users, it stays provisioned. Remove it from an elevated PowerShell if you want: `Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq Claude | Remove-AppxProvisionedPackage -Online`.

## Build from source

```bash
dotnet build src/ClaudeMultiAccount/ClaudeMultiAccount.csproj -c Release
```

Single-file release build:

```bash
dotnet publish src/ClaudeMultiAccount/ClaudeMultiAccount.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 10 SDK. Pushing a `v*` tag builds and publishes a GitHub release through the included workflow.

## Tests

```bash
dotnet test --project tests/ClaudeMultiAccount.Tests
```

See [TESTING.md](TESTING.md) for the admin integration tests and the manual release checklist.

## License

[MIT](LICENSE)
