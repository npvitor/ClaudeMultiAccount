# Claude Multi-Account

**Run several Claude Desktop accounts side by side on Windows**: work and personal, two clients, whatever you need. Every account keeps its own sign-in, settings, history and Claude Code sessions, and all of them can be open at the same time.

[Leia em português](README.pt-BR.md)

> Unofficial, community project. Not affiliated with or endorsed by Anthropic.

## Why

Claude Desktop only signs in one account per Windows user, and it allows a single window per user. The reliable way to run a second account is to run it as a **second Windows user**. Doing that by hand means creating the account, sharing your project folders with it, installing Claude for it, fixing Git's "dubious ownership" errors, and typing a password into "Run as different user" every time.

This launcher does all of that for you, then gives you a one-click **Open Claude** button per account.

## Features

- **One click per account**, from the window or from the tray menu.
- **Hidden Windows users**: each profile gets its own local standard user. It doesn't show on the sign-in screen and isn't an administrator.
- **No passwords to remember**: a random 28-character password is generated per profile and stored encrypted with Windows DPAPI, so only your Windows account can read it.
- **Your projects stay where they are**: choose which folders each profile can read and write. Common repo folders such as `source\repos` and `C:\Git` are suggested.
- **Git just works**: it marks your folders as `safe.directory` and can set a per-profile `user.name` and `user.email`. Personal commits use your personal email.
- **Claude stays updated**: when Claude Desktop updates on your main account, the profile picks up the new version the next time you open it.
- **Terminal per profile**: a PowerShell window running as that account, for the Claude Code CLI, `git` or `gh auth login`.
- Starts with Windows (optional), runs from the tray, single instance.
- Portuguese and English UI, chosen from your Windows language.

## Requirements

- Windows 10 (2004 or later) or Windows 11, 64-bit.
- Your Windows account must be an **administrator**. Creating, changing or removing a profile asks for UAC approval; opening Claude does not.
- [Claude Desktop](https://claude.ai/download) installed on your main account.
- Optional: Git 2.46 or later, for the per-folder `safe.directory` entries.

## Install

1. Download `ClaudeMultiAccount.exe` from the [Releases](../../releases) page. It is a single file and needs no .NET installation.
2. Run it. Windows SmartScreen may warn about an unsigned app: choose **More info → Run anyway**.

## Usage

1. Click **New profile**, give it a name (for example *Personal*), pick a color and the project folders it may use, and optionally a Git name and email.
2. Approve the UAC prompt. The app creates the hidden user, prepares its Windows profile, installs Claude Desktop for it and configures Git. This takes about a minute.
3. Claude opens as the new profile. Sign in with the other account. You only do this once.
4. From then on, use **Open Claude** on each card, or the tray menu.

The **⋯** menu on each profile has:

| Option | What it does |
| --- | --- |
| Open terminal for this profile | PowerShell running as that account (Claude Code CLI, git, gh…) |
| Edit profile… | Rename, recolor, change folders and Git identity |
| Repair / update Claude in profile | Re-runs setup if something broke |
| Copy Windows user password | For troubleshooting only |
| Remove profile… | Deletes the hidden user and its data; your project files are not touched |

## How it works

| Step | Technique |
| --- | --- |
| Create user | `NetUserAdd` (standard user, password never expires), added to *Users*, hidden via `Winlogon\SpecialAccounts\UserList` |
| Share folders | `icacls <folder> /grant *SID:(OI)(CI)M` |
| Install Claude | MSIX install: `Add-AppxPackage -Register` of the package already on the machine, so nothing is downloaded. Classic install: runs the installer you pick inside the profile |
| Launch | `CreateProcessWithLogonW` (the API behind "Run as") starts the profile's own `claude-desktop.exe` |
| Admin steps | The app relaunches itself elevated for that single task. The request goes through a temp file, never the command line, and the password stays DPAPI-encrypted |

Data locations:

- `%APPDATA%\ClaudeMultiAccount\profiles.json`: your profiles, with DPAPI-encrypted passwords.
- `%ProgramData%\ClaudeMultiAccount\<profile user>`: short-lived setup scripts and logs from failed steps. Each folder is readable only by administrators, your account and that profile's user.

## Known limitations

- Windows asks for admin approval when you create, edit folders of, or remove a profile.
- Each profile is a real Windows user, so its browser, Git credentials, `gh` login and the Claude Code CLI are separate from yours. That's the point, but you sign in to those once per profile. Install the CLI from the profile's terminal: `irm https://claude.ai/install.ps1 | iex`.
- Features that rely on a single machine-wide component, such as the Cowork VM service, may only work in one account at a time.
- Toast notifications from a profile's Claude may not appear, because Windows shows notifications for the signed-in user.
- The Claude package includes a Windows service, so Windows may refuse to install it for a standard user. When that happens the app asks for admin approval once and provisions the package for all users of the machine; any Windows user created later also gets Claude Desktop.

## Troubleshooting

- **"Secondary Logon service is disabled"**: open `services.msc`, set **Secondary Logon** to *Manual* and start it.
- **A setup step failed**: read the log in `%ProgramData%\ClaudeMultiAccount\<profile user>`, fix the cause, then use **⋯ → Repair**.
- **Git says "dubious ownership"**: run **⋯ → Edit profile → Save** so the folder is added again, and check that Git is version 2.46 or later.
- **Uninstalling**: remove every profile from the app first, which deletes the hidden users. Then delete the exe and `%APPDATA%\ClaudeMultiAccount`.

## Build from source

```bash
dotnet build src/ClaudeMultiAccount/ClaudeMultiAccount.csproj -c Release
```

Single-file release build:

```bash
dotnet publish src/ClaudeMultiAccount/ClaudeMultiAccount.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 10 SDK. Pushing a `v*` tag builds and publishes a GitHub release through the included workflow.

## License

[MIT](LICENSE)
