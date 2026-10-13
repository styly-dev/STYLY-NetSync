# Signed UPM releases

`.github/workflows/sign-upm-package.yml` is the reusable signing workflow for
STYLY NetSync, STYLY XR Rig, Device ID Provider, and STYLY Shader Collection.
It runs in the calling repository, checks out the exact release commit, uses
Unity UPM CLI to sign the package, checks its manifest and signing attestation,
and attaches the `.tgz` to the caller's draft GitHub Release.

## One-time setup

1. In Unity Cloud, create a service account in the organization that owns the
   packages. Grant the organization role **Package Manager Package Signer**.
2. Generate a service account key and record its Key ID and Secret key. Copy the
   Unity Organization ID from the organization settings.
3. Add these GitHub Actions **repository secrets** to each of the four repositories:
   - `UPM_SERVICE_ACCOUNT_KEY_ID`: the service account Key ID.
   - `UPM_SERVICE_ACCOUNT_KEY_SECRET`: the service account Secret key.
   - `UPM_ORG_ID`: the Unity Organization ID.
4. Merge the OpenUPM metadata migration for all four packages before releasing a
   new version. Set `trackingMode: githubRelease` and `githubReleaseAssetName`
   to the package name followed by `-`, such as `com.styly.styly-netsync-`.

The GitHub owner `styly-dev` is currently a user account, so it cannot provide
Organization Secrets. The same Unity service account can sign all four
packages, but its values must be stored in each repository. The workflows pass
the three secrets explicitly to the reusable workflow.

## Releasing a package

Run the package repository's existing release workflow with the next version.
After its release preparation job creates a draft, the signing job builds the
package from the recorded commit and uploads
`<package-name>-<version>.tgz` to that draft. Wait for the **entire workflow**
to succeed before publishing the draft. OpenUPM then republishes that signed
release asset unchanged.

The preparation job checks signing secrets and the OpenUPM tracking mode before
it changes versions or branches. Signing or upload failures leave the release
unpublished. Inspect the run and draft before retrying; never publish a draft
whose signing job failed. Existing published package versions remain unchanged;
use a new package version for the first signed release.

## Calling the workflow

NetSync uses the workflow from the same commit with a relative `uses` path.
The other repositories pin the shared workflow to a full commit SHA in this
repository. When updating the shared implementation, validate it and update
those pinned references deliberately.

Pass the package folder, the full release commit SHA, and a `v<version>` tag.
The draft must use that SHA as its `targetCommitish`; passing a branch name
would not guarantee that the tarball matches the intended release source.
The calling job needs `contents: write` and must pass the three signing secrets.
The common workflow does not require a Unity Editor installation.

## Testing before a release

Set `attach-to-release: false` to test the same signing and package validation
steps without creating or changing a GitHub Release. Pass the source commit
and `v<package-version>` as usual; no Git tag needs to exist in this mode.
The workflow saves the signed tarball as an Actions artifact for seven days.
This test does not require the OpenUPM metadata migration. A caller can pin
the reusable workflow to a pushed commit before its PR is merged.

References:
- [Unity UPM CLI setup](https://docs.unity.com/en-us/engine/6000.3/manual/packages-list/upm-signature-lp/upm-command-line/upm-cli-install)
- [OpenUPM signed package publishing](https://openupm.com/docs/signing-upm-packages)
- [GitHub reusable workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows)
