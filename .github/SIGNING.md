# Release signing (maintainers)

Release exes are signed through the [SignPath Foundation](https://signpath.org) free program for open-source projects. The workflow in [`workflows/build.yml`](workflows/build.yml) signs release tags (`v*`) on its own as soon as the secret and variables below exist. Until then it publishes unsigned releases with their SHA-256, as before.

## 1. Before applying

- Turn on two-factor authentication on GitHub (and later on SignPath) for everyone listed in the README's [Code signing policy](../README.md#code-signing-policy).
- The README already has the required **Code signing policy** section (sign-up line, committers and reviewers, approvers) and the **Privacy** statement, and every release page links to the policy.

## 2. Apply

Fill in the form at <https://signpath.org/apply>. In the description, explain what the app changes on the machine (hidden local users, folder permissions, elevation) and link the README's Security section: it helps the review.

## 3. Set up SignPath (after approval)

1. **Project**: create one for this repository and link the predefined trusted build system **GitHub.com** to it.
2. **Artifact configuration** (make it the default one). The workflow uploads the exe as a GitHub artifact, which is a ZIP:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <pe-file path="ClaudeMultiAccount.exe">
         <authenticode-sign />
       </pe-file>
     </zip-file>
   </artifact-configuration>
   ```

3. **Signing policy** for releases (for example `release-signing`): you as approver, so every request waits for your approval. Under origin verification, allow this repository and the release tags (`v*`).
4. **CI user and API token**: a user with the *submitter* role on that signing policy; create an API token for it.

## 4. Set up GitHub

In the repository: **Settings → Secrets and variables → Actions**.

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | the CI user's API token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | your SignPath organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | the project's slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | the signing policy's slug (e.g. `release-signing`) |

Signing stays off until all four exist.

## 5. Each release

1. Write `.github/release-notes/vX.Y.Z.md` (optional) and push the tag: `git push origin vX.Y.Z`.
2. The workflow tests, builds, uploads the unsigned exe and sends the signing request. **Approve it in SignPath**; the run waits up to 3 hours.
3. It checks the signature, computes the SHA-256 of the signed exe and publishes the release, which says it is signed and links the policy.

After the first signed release, update the "not signed yet" notes in the READMEs (Install, Security, Code signing policy status).
