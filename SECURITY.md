# Security policy

Claude Multi-Account creates Windows accounts, changes folder permissions and relaunches itself with administrator rights, so security reports are very welcome.

## Supported versions

Only the latest release gets fixes. Please check that a problem still happens there before reporting it.

## Reporting a vulnerability

Please **don't open a public issue**. Report it privately through GitHub instead: open the repository's **Security** tab and choose **Report a vulnerability**.

Include what you can of:

- the app version and the Windows version and edition;
- what an attacker needs to start with (which account, which rights) and what they gain;
- steps to reproduce, or a proof of concept.

This is a community project maintained in spare time. Reports are acknowledged and fixed on a best-effort basis, and you'll be credited in the release notes unless you'd rather not be.

## Scope

In scope, for example:

- the elevated helper (`--elevated`) doing something its request shouldn't allow, or accepting a request from another account;
- a way for one profile user, or another non-admin user of the machine, to read or change another profile's or the main account's data, or to get code run by the launcher or as another account;
- weaknesses in how profile passwords are generated or stored;
- injection through profile names, folder paths, Git identity or other input into the scripts and commands the app runs.

Out of scope:

- attacks that already require running code as the main Windows account or as an administrator. That account holds the DPAPI keys for every profile by design (see [Security in the README](README.md#security));
- what a profile user can do inside the folders you shared with it, or with administrator rights you enabled for it;
- vulnerabilities in Claude Desktop, Claude Code or other Anthropic software. Report those to [Anthropic](https://www.anthropic.com/responsible-disclosure-policy).
