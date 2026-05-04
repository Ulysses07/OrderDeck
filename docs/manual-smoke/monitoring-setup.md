# Production Monitoring — Grafana Cloud + UptimeRobot

OrderDeck's license server already exposes Prometheus metrics + custom
business counters (license activations, backup uploads, email sends, auth
refresh rotations) and the OpenTelemetry Protocol exporter is wired in —
it's just not configured to push anywhere. This guide hooks it up to
Grafana Cloud (free tier) for metrics/traces and UptimeRobot for
external uptime checks. Two free accounts, one .env edit, one container
restart, ~30 minutes total.

The VPS (1 CPU, 3.8 GB RAM) cannot host a self-managed Prometheus +
Grafana stack without crowding out SQL Server, so external SaaS is the
right call — even more so because external monitoring **continues
working when the VPS dies**, which is exactly the moment you need to
hear about it.

## Part 1 — Grafana Cloud (metrics + traces)

### 1.1 Sign up + create stack

1. https://grafana.com/auth/sign-up/create-user — free account.
2. After confirming email, Grafana provisions a Stack automatically.
   Choose any region (Frankfurt for Türkiye latency); the URL ends up
   like `<your-stack>.grafana.net`.
3. Free tier limits: 10K active series, 50 GB logs+traces, 14-day
   retention. OrderDeck at 100+ users is well under all three.

### 1.2 Get OTLP credentials

1. In your stack: **Connections → Add new connection → "OpenTelemetry (OTLP)"**.
2. Click **"Generate token"**. Pick **"Send metrics, logs, and traces"**
   scope. Save the access policy token — Grafana shows it once.
3. The page shows the OTLP endpoint URL (looks like
   `https://otlp-gateway-prod-eu-west-3.grafana.net/otlp`) and an
   **Authorization** header. The header is in the form:
   ```
   Basic <base64(stack-id:token)>
   ```
   Grafana hands you the already-encoded value — copy it as-is.

### 1.3 Wire OrderDeck up

Edit `/opt/orderdeck/.env` on the VPS:

```bash
ssh root@72.62.53.86 'cat >> /opt/orderdeck/.env <<EOF
OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-prod-eu-west-3.grafana.net/otlp
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <PASTE_THE_BASE64_HERE>
EOF'
```

> **Don't quote** the values; docker-compose env file syntax does not
> handle quotes the way Bash does and Grafana will reject a
> `"Basic ..."` Authorization header.

Restart the license server so the OTel exporter picks up the new
endpoint:

```bash
ssh root@72.62.53.86 'cd /opt/orderdeck && docker compose up -d license-server --force-recreate'
```

### 1.4 Verify

Within 60 seconds, in your Grafana stack:

1. **Explore → Prometheus** datasource → query `up{service_name="orderdeck-license-server"}`. Expected: a line at value `1`.
2. **Explore → Tempo** datasource → click **"Search"**. Expected: traces
   appearing under the `orderdeck-license-server` service every few
   seconds (one trace per request).
3. ASP.NET Core request rate sanity check: `rate(http_server_request_duration_seconds_count{service_name="orderdeck-license-server"}[5m])` — should match real traffic to the API.

### 1.5 Import a starter dashboard

For metrics: **Dashboards → New → Import → 17110** (the community
"ASP.NET Core" dashboard). Filter by `service_name=orderdeck-license-server`.
This gives you request rate, error rate, p95 latency, GC, threadpool
live without writing any panels.

OrderDeck-specific business metrics (license_activations_total,
backup_uploads_total, etc.) are best surfaced in their own panels —
build those incrementally as you find the questions you want answered.

## Part 2 — UptimeRobot (external uptime)

Grafana Cloud sees the metrics the server pushes; UptimeRobot is the
"is the server even up?" check that runs from outside your
infrastructure. Both layers are needed: a process that has crashed
hard can't report its own death.

### 2.1 Sign up

1. https://uptimerobot.com/signUp — free tier. 50 monitors, 5-minute
   interval, public status page included.

### 2.2 Add three monitors

For each, **+ New monitor → HTTP(S)**:

| Friendly name | URL | Expected | Why this URL |
| --- | --- | --- | --- |
| OrderDeck site | `https://orderdeckapp.com/` | 200 | Marketing + PP/ToS — also catches Caddy + Let's Encrypt failures |
| OrderDeck license server | `https://license.orderdeckapp.com/healthz` | 200 | Liveness probe — process up, no DB check |
| OrderDeck extension config | `https://license.orderdeckapp.com/api/v1/extension/selectors` | 200 | If this 404s, every operator's extension stops auto-refreshing selectors |

Interval: 5 minutes (free-tier minimum). Alert contact: your email
(default). For a phone number, add via **My Settings → Alert contacts**;
free tier supports email + Slack/Discord/Telegram webhooks.

### 2.3 Test the alert

```bash
ssh root@72.62.53.86 'cd /opt/orderdeck && docker compose stop license-server'
```

Within 5–10 minutes you should get the "license server is DOWN" email.
Bring it back:

```bash
ssh root@72.62.53.86 'cd /opt/orderdeck && docker compose start license-server'
```

The "back UP" email lands ~5 minutes later.

### 2.4 Public status page (optional)

UptimeRobot → **Status pages → New status page**. Add the three
monitors. Set the public URL to something like
`https://stats.uptimerobot.com/<id>`. Link it from the OrderDeck
website footer if you want operators to self-serve when they ask "is
it down?".

## Recommended cadence

- **Weekly:** glance at the Grafana ASP.NET Core dashboard. Anomalies
  in p95 latency or error rate often surface a real issue before any
  user notices.
- **Whenever a new alert fires:** write down the trigger condition + the
  fix in a tiny incident log. After 2-3 entries you'll see the pattern
  and can prevent the recurrence.
- **Every couple of months:** review what's actually being scraped vs
  the 10K-series cap. The free tier is generous, but instrumenting too
  many high-cardinality labels (per-customer counters) can balloon
  series count fast.

## When things break

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| No data in Grafana after restart | OTEL_EXPORTER_OTLP_HEADERS quoted, or wrong base64 | `docker compose logs license-server | grep -i otel` — error message is explicit |
| `up{}` query returns nothing but app responds 200 | Service name mismatch (default is `orderdeck-license-server`, set in `OtelExporterExtensions.cs`) | Cross-check with Grafana Explore → Service Graph for actual labels |
| UptimeRobot reports the site down but it works locally | Geographic / IP-blocking (rare) — UptimeRobot probes from US-East primarily | Check from a different vantage; if confirmed, add IP allowlist exception |
| Grafana free tier "series limit exceeded" warning | Custom counters with per-customer labels | Move per-customer dimensions into traces (high cardinality, free) instead of metrics (limited) |
