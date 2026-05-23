# OrderDeck — Repo Guide for Claude

This repo holds the broadcaster side of OrderDeck (a Turkish live-stream
e-commerce platform). The shopper-side mobile app lives in a separate
repo, **OrderDeck-Shopper** (private, on disk at
`C:\Users\burak\source\repos\OrderDeck-Shopper`).

## Stack

- **`OrderDeck.App`** — WPF desktop app, broadcaster operator UI (`net10.0-windows`)
- **`OrderDeck.LicenseServer`** — ASP.NET Core 10 server, deployed to VPS via Docker (`license.orderdeckapp.com`)
- **`OrderDeck.Chat`** — chat bridge (WebSocket server + YouTube scraper), used by WPF
- **`OrderDeck.Core`** — shared domain
- **`Extension/`** — Chrome MV3 extension that scrapes Instagram/TikTok/Facebook live chat → forwards to WPF over `ws://localhost:4748`
- **SQL Server (prod)** in Docker on VPS; **InMemory** for tests
- **EF Core 10**, Dapper for hot-path WPF queries (SQLite local)
- **Cloudflare R2** via AWS SDK S3 + SigV4 — uses `DisablePayloadSigning = true` + `UseChunkEncoding = false` (R2 doesn't support `STREAMING-AWS4-HMAC-SHA256-PAYLOAD`)
- **PdfPig** for server-side PDF parsing (shopper payment receipts)

## Mobile-side stack (OrderDeck-Shopper)

- React + Vite + TypeScript + Capacitor 6
- TanStack Query + Zustand
- Capacitor Preferences for auth tokens (native) / localStorage (web)
- Tailwind + ESLint v9

## Conventions

- **Branches**: `feat/...`, `fix/...`, `hotfix/...`, `chore/...`, `docs/...`
- **PR titles**: type(scope): summary (e.g. `fix(chat-dedupe): ...`)
- **Commit messages**: imperative; **Turkish or English are both fine — match the user's tone**
- Always include `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>` on commits I author
- Hosted services in WPF: registered in `AppHost.cs` via `AddHostedService<>`, but **must also be explicitly started** in `App.xaml.cs` (WPF has no `IHost` builder — see PR #89 fix that added the generic startup loop)

## Test + build

- `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` — WPF/Chat side (~620 tests)
- `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj` — server side (~747 tests)
- `dotnet build OrderDeck.App/OrderDeck.App.csproj` — WPF (Windows-only)
- CI runs both via `.github/workflows/build-test.yml`; server deploy via `license-server-deploy.yml`

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

## Currently in-flight (as of 2026-05-23)

- Shopper app Faz 4 (push notifications) — server-side PR #91 merged, client PRs in flight
- Chat dedupe regression chain (PR #92 → #93 → #94 → #95 → #96) — see those PRs for the saga; current state: Tier 1 WeakSet element-identity dedupe with Tier 2 hash fallback, manifest 1.4.8
- Customer projection sync between WPF and server (PR #88) — done

## Memory

Per-conversation auto-memory is in `.claude/projects/.../memory/MEMORY.md`.
It carries project state across sessions; this CLAUDE.md is the static
repo-shape companion.
