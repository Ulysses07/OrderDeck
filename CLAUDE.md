# OrderDeck — Repo Guide for Claude

This repo holds the broadcaster side of OrderDeck (a Turkish live-stream
e-commerce platform). Two sibling repos hold the web/mobile clients:

- **OrderDeck-Shopper** (private) — `apps/shopper`, the customer mobile app
- **OrderDeck-Mobile** (private) — `apps/panel`, the broadcaster web panel,
  deployed to `panel.orderdeckapp.com`

Both live next to this one under `source/repos/`.

> **This repo is PUBLIC** (since 2026-08-10, for CI quota). History is
> included, so every past commit is readable. Secret hygiene is
> non-negotiable: no credentials, tokens or customer data in-repo, ever.

## Stack

- **`OrderDeck.App`** — WPF desktop app, broadcaster operator UI (`net10.0-windows`)
- **`OrderDeck.LicenseServer`** — ASP.NET Core 10 server, deployed to VPS via Docker (`license.orderdeckapp.com`)
- **`OrderDeck.Chat`** — chat bridge (WebSocket server + YouTube Data API v3), used by WPF. The YouTube scraper and the HTML-scraping live resolver were deleted in PR #213; the only YouTube path now is the official API
- **`OrderDeck.Core`** — shared domain
- **`Extension/`** — Chrome MV3 extension that scrapes **TikTok** live chat → forwards to WPF over `ws://localhost:4748`. Facebook was dropped in `0752a0d`, Instagram right after: both have official APIs now. TikTok is the only platform with no official path, and the only reason the bridge still exists
- **SQL Server (prod)** in Docker on VPS; **InMemory** for tests
- **EF Core 10**, Dapper for hot-path WPF queries (SQLite local)
- **Cloudflare R2** via AWS SDK S3 + SigV4 — uses `DisablePayloadSigning = true` + `UseChunkEncoding = false` (R2 doesn't support `STREAMING-AWS4-HMAC-SHA256-PAYLOAD`)
- **PdfPig** for server-side PDF parsing (shopper payment receipts)

## Mobile-side stack (OrderDeck-Shopper)

- React 18 + Vite 7 + TypeScript 5 + Capacitor 8 (**Android only** — there is no
  iOS target; `@capacitor/ios` isn't even a dependency)
- TanStack Query + Zustand
- Capacitor Preferences for auth tokens (native) / localStorage (web)
- Tailwind + ESLint v9 + Vitest

## Conventions

- **Branches**: `feat/...`, `fix/...`, `hotfix/...`, `chore/...`, `docs/...`
- **PR titles**: type(scope): summary (e.g. `fix(chat-dedupe): ...`)
- **Commit messages**: imperative; **Turkish or English are both fine — match the user's tone**
- Always include `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>` on commits I author
- Hosted services in WPF: registered in `AppHost.cs` via `AddHostedService<>`, but **must also be explicitly started** in `App.xaml.cs` (WPF has no `IHost` builder — see PR #89 fix that added the generic startup loop)

## Test + build

- `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` — WPF/Chat side
- `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj` — server side.
  Mostly InMemory, but a handful of tests start a real SQL Server through
  Testcontainers (`SqlServerContainerFixture`), because InMemory has no
  concurrency semantics — a fix that hinges on a row-level race can't be proven
  there. **Docker must be running** or those tests fail
- `dotnet build OrderDeck.App/OrderDeck.App.csproj` — WPF (Windows-only)
- CI runs both via `.github/workflows/build-test.yml`; server deploy via
  `license-server-deploy.yml`. Server tests run in their own **ubuntu** job —
  the Windows runner can't start Linux containers
- Test counts are deliberately not written down here. Every number that was
  ever pinned in this file went stale within weeks and then misled; read the
  count off the CI run instead.

## Logs (WPF, local dev)

- `~/Documents/OrderDeck/Logs/log-YYYYMMDD.txt` (Serilog daily rolling)
- Filter for hosted services: `grep -iE "HostedService starting|Ingested|sync" ...`

## Production

- VPS hosts the server; IP + SSH creds are in local notes, not in-repo
- Containers: `orderdeck-license`, `orderdeck-caddy`, `orderdeck-sqlserver`
- Server auto-deploys on merge to `master` via GitHub Actions
- Chrome extension published in the Web Store; users get updates automatically

## Communication preferences

- User speaks **Turkish**, reply in Turkish unless the topic is purely code/log output
- Be **concise**; lead with action, no preamble
- For risky operations (force push, destructive DB writes, prod deploys, sharing publicly) — **ask first**, never assume
- When facing a hard bug, **stop and reason out loud** before writing code; the user prefers a short discussion over a flurry of speculative PRs
- Don't add features, refactors, or "improvements" beyond what was asked

## Memory

Per-conversation auto-memory is in `.claude/projects/.../memory/MEMORY.md`.
It carries project state across sessions; this CLAUDE.md is the static
repo-shape companion.
