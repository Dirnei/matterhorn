# release-please + GHCR Publishing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automate semver/changelog with release-please and publish a multi-arch Docker image to GHCR on each release.

**Architecture:** One GitHub Actions workflow with two jobs on push to `main`: `release-please` maintains the release PR / creates the tag, and a gated `docker` job builds and pushes `linux/amd64,linux/arm64` to `ghcr.io/dirnei/matterhorn` when a release is created.

**Tech Stack:** GitHub Actions, `googleapis/release-please-action@v4`, `docker/*` actions (buildx + QEMU), .NET 10 SDK (existing Dockerfile).

## Global Constraints

- Image: `ghcr.io/dirnei/matterhorn` — exact string in the workflow.
- Architectures: `linux/amd64,linux/arm64`.
- release-please `release-type: simple`; version files seed at `0.0.0`.
- First release forced to `0.1.0` via a `Release-As: 0.1.0` commit footer.
- Auth: built-in `GITHUB_TOKEN` with `packages: write` — no PAT.
- Do NOT create the GitHub repo or set a git remote.
- Spec: `docs/superpowers/specs/2026-07-04-release-please-ghcr-design.md`.

---

### Task 1: release-please config + version files

**Files:**
- Create: `release-please-config.json`
- Create: `.release-please-manifest.json`
- Create: `version.txt`

**Interfaces:**
- Produces: config referenced by the workflow as `config-file: release-please-config.json` and `manifest-file: .release-please-manifest.json`; manifest package key `"."`.

- [ ] **Step 1: Write `release-please-config.json`**

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

- [ ] **Step 2: Write `.release-please-manifest.json`**

```json
{ ".": "0.0.0" }
```

- [ ] **Step 3: Write `version.txt`**

```
0.0.0
```

- [ ] **Step 4: Verify both JSON files parse**

Run: `python -c "import json,sys; [json.load(open(f)) for f in ['release-please-config.json','.release-please-manifest.json']]; print('ok')"`
Expected: prints `ok` (no exception).

- [ ] **Step 5: Commit**

```bash
git add release-please-config.json .release-please-manifest.json version.txt
git commit -m "chore: add release-please config and version files"
```

---

### Task 2: csproj version property

**Files:**
- Modify: `src/Matterhorn/Matterhorn.csproj` (first `<PropertyGroup>`, after `<TargetFramework>`)

**Interfaces:**
- Consumes: `extra-files` entry from Task 1 pointing at this csproj.
- Produces: `<Version>` line carrying the `x-release-please-version` annotation that the generic updater bumps.

- [ ] **Step 1: Add the annotated `<Version>` line**

In `src/Matterhorn/Matterhorn.csproj`, inside the first `<PropertyGroup>`, add immediately after the `<TargetFramework>net10.0</TargetFramework>` line:

```xml
    <Version>0.0.0</Version> <!-- x-release-please-version -->
```

The annotation comment MUST stay on the same line as `<Version>` — the generic updater matches per-line.

- [ ] **Step 2: Verify the project still builds**

Run: `dotnet build src/Matterhorn/Matterhorn.csproj -c Release`
Expected: `Build succeeded` (0 errors).

- [ ] **Step 3: Commit**

```bash
git add src/Matterhorn/Matterhorn.csproj
git commit -m "chore: add release-please-managed Version to csproj"
```

---

### Task 3: release + publish workflow

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: `release-please-config.json`, `.release-please-manifest.json` (Task 1); the existing `Dockerfile` at repo root via `context: .`.
- Produces: images `ghcr.io/dirnei/matterhorn:<version>`, `:<major>.<minor>`, `:latest`.

- [ ] **Step 1: Write `.github/workflows/release.yml`**

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

- [ ] **Step 2: Verify the workflow is valid YAML**

Run: `python -c "import yaml; yaml.safe_load(open('.github/workflows/release.yml')); print('ok')"`
Expected: prints `ok`. (If `yaml` is unavailable, use `python -c "import json,subprocess"` alternative or skip — full validation happens once the repo is on GitHub.)

- [ ] **Step 3: Verify the multi-arch image builds (no push)**

Run: `docker buildx build --platform linux/amd64,linux/arm64 .`
Expected: build completes for both platforms. (Requires local Docker/buildx; if unavailable, note it — runtime verification occurs in CI once the repo exists.)

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: add release-please + GHCR publish workflow"
```

---

## Post-implementation (manual, once the repo exists on GitHub)

Not code steps — instructions for the user after `dirnei/matterhorn` is created and this branch is pushed/merged:

1. Ensure GitHub Actions is enabled and workflow permissions allow "Read and write" (Settings → Actions → General → Workflow permissions).
2. The very first setup commit on `main` must carry the footer to force `0.1.0`:
   ```
   Release-As: 0.1.0
   ```
   (If already merged without it, add an empty commit: `git commit --allow-empty -m "chore: release 0.1.0" -m "Release-As: 0.1.0"`.)
3. release-please opens a release PR at `0.1.0`; merge it → tags `v0.1.0` → `docker` job publishes `:0.1.0`, `:0.1`, `:latest`.
4. First package is private by default; make it public (or grant pulls) via the package settings on GHCR if desired.

## Verification Summary

- JSON configs parse (Task 1).
- csproj builds with the new `<Version>` (Task 2).
- Workflow YAML parses; multi-arch image builds locally (Task 3).
- End-to-end (release PR → tag → image push) is verifiable only after the repo exists — covered in Post-implementation.
