# VitePress docs site on GitHub Pages — design

Date: 2026-07-04
Repo (future): `dirnei/matterhorn` · Site URL: `https://dirnei.github.io/matterhorn/`

## Goal

Publish a VitePress documentation site for Matterhorn, hosted on GitHub Pages,
built from the content currently in the README. The README slims to a pitch +
quick start + link to the site so there is one source of truth (DRY).

## Decisions (confirmed with user)

- **Site location:** VitePress root at `docs/`; internal process docs under
  `docs/superpowers/` excluded from the build via `srcExclude`.
- **Content:** restructure the README's sections into guide pages; slim the README.
- **Deploy trigger:** push to `main` touching docs sources (+ manual dispatch).
- **No custom domain:** project-pages base path `'/matterhorn/'`.

## Layout

```
package.json                     # NEW — root; devDep vitepress; scripts docs:dev/build/preview
package-lock.json                # NEW — committed lockfile (npm ci in CI needs it)
docs/
  .vitepress/config.ts           # NEW — nav, sidebar, base, srcExclude, title
  index.md                       # NEW — home layout (hero + features)
  guide/
    getting-started.md           # from README "Running it"
    how-it-works.md              # from README "How it works"
    commissioning.md             # from README "Commissioning a device"
    control.md                   # from README "Controlling a device — MQTT or REST"
    configuration.md             # from README "Configuration"
    rest-api.md                  # from README "REST API (contract-first)"
  superpowers/…                  # EXISTING — excluded, never published
```

## Components

### `package.json` (repo root)

- `devDependencies`: `vitepress` (latest 1.x).
- `scripts`:
  - `docs:dev` → `vitepress dev docs`
  - `docs:build` → `vitepress build docs`
  - `docs:preview` → `vitepress preview docs`
- `"private": true` (not an npm package).
- A committed `package-lock.json` so CI can run `npm ci`.

### `docs/.vitepress/config.ts`

Key settings:
- `title: 'Matterhorn'`, `description`: one-line pitch from the README.
- `base: '/matterhorn/'` — required for GitHub project pages served under a
  sub-path. (If a custom domain is added later: set `base: '/'` and add a
  `CNAME` file to `docs/public/`.)
- `srcExclude: ['superpowers/**']` — keep internal specs/plans out of the site.
- `themeConfig.nav`: Guide (→ getting-started), REST API (→ rest-api), GitHub
  link (`https://github.com/dirnei/matterhorn`).
- `themeConfig.sidebar` for `/guide/`: Getting Started, How it works,
  Commissioning, Controlling devices, Configuration, REST API.
- `themeConfig.socialLinks`: GitHub.
- `ignoreDeadLinks: false` — dead internal links fail the build (acts as a link
  check).

### `docs/index.md`

VitePress `layout: home` front matter with a `hero` (name "Matterhorn",
tagline, a "Get Started" action → `/guide/getting-started`, a "View on GitHub"
action) and a `features` grid summarising the "What you get" bullets
(Commissioning, MQTT, REST, Web dashboard).

### Guide pages

Each page is a restructure of the matching README section — same prose and code
fences, adapted for the web (relative links between guide pages; the ASCII
architecture diagram kept as a fenced block). No new content is invented.

- `getting-started.md` — Docker/compose quick look (fake controller, no
  hardware) and the real-controller path (matterjs-server on a Pi, `.env`).
- `how-it-works.md` — architecture diagram + the matterjs-server / host-network
  requirement.
- `commissioning.md` — commissioning a device (incl. multi-admin).
- `control.md` — MQTT topic shape and REST PATCH/GET examples.
- `configuration.md` — env vars, `.env.example` walkthrough.
- `rest-api.md` — contract-first note + pointer to Swagger UI / the OpenAPI YAML.

### README (slimmed)

Keep: title, one-paragraph pitch, "Why" (condensed), the architecture diagram,
a minimal quick-start snippet, and a prominent link to
`https://dirnei.github.io/matterhorn/`. Detailed prose lives in the site.

### `.gitignore` additions

```
node_modules/
docs/.vitepress/dist/
docs/.vitepress/cache/
```

### `.github/workflows/docs.yml`

Separate from `release.yml`.

```yaml
name: docs

on:
  push:
    branches: [main]
    paths:
      - 'docs/**'
      - 'package.json'
      - 'package-lock.json'
      - '.github/workflows/docs.yml'
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: false

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 20
          cache: npm
      - run: npm ci
      - run: npm run docs:build
      - uses: actions/upload-pages-artifact@v3
        with:
          path: docs/.vitepress/dist

  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - uses: actions/deploy-pages@v4
        id: deployment
```

Note: `docs/superpowers/**` matches the `docs/**` path filter, so edits to
internal specs/plans will also trigger a docs rebuild. That is acceptable — the
build is cheap and `srcExclude` keeps that content out of the output. (Tightening
the filter to exclude `docs/superpowers/**` is possible but not worth the
complexity.)

## Manual steps (once the repo exists on GitHub)

1. GitHub → Settings → Pages → **Source: GitHub Actions**. (Required before
   `docs.yml` can publish.)
2. First push to `main` (or a manual `workflow_dispatch`) builds and deploys.

## Verification

- `npm ci` (or `npm install`) then `npm run docs:build` succeeds and produces
  `docs/.vitepress/dist`.
- `npm run docs:dev` serves locally; the build fails on dead internal links, so
  a clean build is also a link check.
- End-to-end Pages deploy is verifiable only after the repo exists and Pages is
  enabled — covered by the manual steps.

## Out of scope

- Creating the GitHub repo / enabling Pages (user handles).
- Custom domain / CNAME.
- Versioned docs, i18n, search backends beyond VitePress's built-in local search
  default (not enabled unless trivial).
- API reference auto-generated from the OpenAPI YAML (the REST page links to
  Swagger UI instead).
