# Extension Selector Hot-Fix

When Instagram, TikTok or Facebook changes their DOM and the OrderDeck
extension stops capturing chat, this is the runbook for pushing a fix to
every operator without forcing an extension reinstall.

The selector bundle lives in two places that **must stay in sync**:

| Location | Format | Purpose |
| --- | --- | --- |
| [`OrderDeck.LicenseServer/Extension/SelectorRegistry.cs`](../../OrderDeck.LicenseServer/Extension/SelectorRegistry.cs) | C# constant | Served live at `https://license.orderdeckapp.com/api/v1/extension/selectors`. Hot-fix path. |
| [`Extension/selectors.bundled.json`](../../Extension/selectors.bundled.json) | JSON | Shipped inside the extension zip. Used on first install, when the license server is unreachable, or when storage is empty. |

The runbook below covers both: ship a fix to existing users in <10 min,
then update the bundled JSON so fresh installs ship with the same fix.

## Step 1 — Diagnose what changed

Open the broken page in Chrome with DevTools, paste this in the console:

```js
window.__orderdeckBridge.scan()
```

If it returns an empty array but the page clearly has chat messages, the
selectors are stale. Inspect a single message row in the Elements panel
and identify the new structure:

- Is the comment container still tagged with `aria-label*="comment"` /
  `aria-label*="yorum"`? Or has the platform switched to a different
  attribute (e.g. `data-testid="..."`)?
- Are the username/text spans still `span[dir="auto"]`?
- Did a wrapper `<div role="article">` disappear?

Write down the new selector strings.

## Step 2 — Patch the C# constant

Edit [`SelectorRegistry.cs`](../../OrderDeck.LicenseServer/Extension/SelectorRegistry.cs):

1. Update the changed selector strings inside the affected platform block.
2. Bump `PublishedAt` to **right now** (UTC). The ETag is derived from the
   serialized JSON, so a content change already invalidates downstream
   caches, but a refreshed `publishedAt` makes the change easy to spot in
   logs / debug output.

Build + run the controller test to make sure the schema still serializes:

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~ExtensionConfig" --no-restore
```

## Step 3 — Mirror the change into the bundled JSON

Edit [`Extension/selectors.bundled.json`](../../Extension/selectors.bundled.json) so it matches
the new C# constant. This is the fallback that ships inside the
extension installer for fresh installs. Keep them aligned at every commit
— if they drift, fresh installs will use stale selectors until the first
license-server fetch succeeds.

> Tip: copy the JSON the running server produces to verify byte-equality:
> `curl https://license.orderdeckapp.com/api/v1/extension/selectors > /tmp/live.json`
> then diff against `selectors.bundled.json`.

## Step 4 — Ship to production

```bash
git add OrderDeck.LicenseServer/Extension/SelectorRegistry.cs Extension/selectors.bundled.json
git commit -m "fix(extension): refresh <platform> selectors after DOM change"
git push
```

The web-deploy workflow takes care of the static site; for the license
server, the deploy is one of:

- Manual: ssh into the VPS → `cd /opt/orderdeck && docker compose build license-server && docker compose up -d license-server`
- CI (when the workflow is added): push triggers automated rebuild

Verify the live endpoint:

```bash
curl -i https://license.orderdeckapp.com/api/v1/extension/selectors | head -10
```

The new ETag should appear in the response and the body should reflect
your change.

## Step 5 — Confirm operators pick up the fix

Each operator's extension polls the endpoint every 10 minutes via
`chrome.alarms`. To verify a single client manually:

1. Open the affected platform tab in Chrome.
2. DevTools → Application → Storage → Local Storage → extension scope.
3. Wait up to 10 minutes (or refresh the page to trigger the boot-time
   read of `chrome.storage.local`, which the background worker has
   updated by now).
4. Confirm `__orderdeck_selectors` reflects the new content.
5. `window.__orderdeckBridge.scan()` should return non-empty.

If you need an instant push (rare — usually 10 min wait is fine), ask
the operator to right-click the extension icon → **Manage extension** →
**Reload**. That re-runs `onStartup` which kicks the immediate refresh.

## When you also need to bump the extension itself

Pure selector changes never need a new extension build. You only need to
ship a new sideload zip when:

- The **scan logic** changes (e.g. new MutationObserver pattern, parsing
  code that doesn't fit the schema).
- A new **platform** is being added.
- The **schema version** is bumped — the registry refuses to load
  bundles with a higher schemaVersion than the extension was built
  against.

Bump `Extension/manifest.json` `version`, rebuild the zip, and ship to
operators via your usual install channel.
