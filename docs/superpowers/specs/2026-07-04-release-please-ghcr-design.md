# release-please + GHCR publishing — design

Date: 2026-07-04
Repo (future): `dirnei/matterhorn` · Image: `ghcr.io/dirnei/matterhorn`

## Goal

Automate versioning, changelog, and container publishing for the Matterhorn
(.NET 10) app:

- **release-please** maintains a rolling release PR from conventional commits on
  `main`, computing the next semver and changelog.
- Merging that PR tags the release and triggers a **multi-arch Docker build**
  pushed to **GHCR**.

## Approach

One GitHub Actions workflow with two jobs. On push to `main`:

1. `release-please` computes/updates the release PR (or, when a release PR is
   merged, creates the tag + GitHub release).
2. `docker` runs only when job 1 reports `release_created == 'true'`, then builds
   and pushes the image.

Rationale for a single workflow rather than an `on: release` trigger: releases
created by the built-in `GITHUB_TOKEN` do **not** trigger downstream workflows
(GitHub anti-recursion). Gating the Docker job on release-please's
`release_created` output is the reliable pattern.

Decisions (confirmed with user):
- **Push trigger:** release only. No edge/main image builds.
- **Architectures:** `linux/amd64` + `linux/arm64` (arm64 for Raspberry Pi hosts).
- **Versioning:** `version.txt` + csproj `<Version>` kept in sync by release-please.
- **First release:** exactly `0.1.0` (see "First release" below).

## Files

### `release-please-config.json` (repo root)

```json
{
  "$schema": "https://raw.githubusercontent.com/googleapis/release-please/main/schemas/config.json",
  "packages": {
    ".": {
      "release-type": "simple",
      "extra-files": ["src/Matterhorn/Matterhorn.csproj"]
    }
  }
}
```

- `release-type: simple` manages `version.txt` and `CHANGELOG.md`.
- `extra-files` runs the generic updater over the csproj (see annotation below).

### `.release-please-manifest.json` (repo root)

```json
{ ".": "0.0.0" }
```

Represents "last released version". Seeded at `0.0.0` (nothing released yet).

### `version.txt` (repo root)

```
0.0.0
```

Managed by the `simple` release type; bumped on each release.

### `src/Matterhorn/Matterhorn.csproj`

Add a `<Version>` inside the existing first `<PropertyGroup>`, with the
release-please annotation on the same line so the generic updater finds and
bumps it:

```xml
<Version>0.0.0</Version> <!-- x-release-please-version -->
```

Result: the built assembly reports the real released version.

### `.github/workflows/release.yml`

```yaml
name: release

on:
  push:
    branches: [main]

permissions:
  contents: write
  pull-requests: write

jobs:
  release-please:
    runs-on: ubuntu-latest
    outputs:
      release_created: ${{ steps.release.outputs.release_created }}
      version: ${{ steps.release.outputs.version }}
      major: ${{ steps.release.outputs.major }}
      minor: ${{ steps.release.outputs.minor }}
    steps:
      - uses: googleapis/release-please-action@v4
        id: release
        with:
          config-file: release-please-config.json
          manifest-file: .release-please-manifest.json

  docker:
    needs: release-please
    if: ${{ needs.release-please.outputs.release_created == 'true' }}
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - uses: docker/metadata-action@v5
        id: meta
        with:
          images: ghcr.io/dirnei/matterhorn
          tags: |
            type=raw,value=${{ needs.release-please.outputs.version }}
            type=raw,value=${{ needs.release-please.outputs.major }}.${{ needs.release-please.outputs.minor }}
            type=raw,value=latest
      - uses: docker/build-push-action@v6
        with:
          context: .
          platforms: linux/amd64,linux/arm64
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
```

Notes:
- Tags are built from release-please **outputs** (not `type=semver`, which reads
  the event ref `refs/heads/main` on a push and would not see the tag).
- `GITHUB_TOKEN` with `packages: write` is sufficient for GHCR — no PAT needed.

## Tag examples

A release landing at `1.4.0` pushes:
`ghcr.io/dirnei/matterhorn:1.4.0`, `:1.4`, `:latest`.

## First release (exactly 0.1.0)

Manifest/version files seed at `0.0.0`. To force the first release PR to target
`0.1.0` regardless of commit types, include a `Release-As` footer in the setup
commit:

```
chore: set up release-please + GHCR publishing

Release-As: 0.1.0
```

release-please opens the first release PR at `0.1.0`; merging it tags `v0.1.0`
and triggers the first image build. The footer is a one-shot; later releases
compute normally from `feat:`/`fix:` commits.

## Out of scope

- Creating the GitHub repo / setting the git remote (user handles later).
- Edge/`main` image builds.
- Signing/attestation (can be added later).

## Verification

The pipeline can only be fully exercised once the repo exists on GitHub. Before
that, verify locally:
- `release-please-config.json` / manifest are valid JSON.
- The csproj still builds with the added `<Version>` (`dotnet build`).
- The multi-arch image builds via `docker buildx build --platform
  linux/amd64,linux/arm64 .` (no push).
