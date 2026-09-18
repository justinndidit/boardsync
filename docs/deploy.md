# Deploying BoardSync

One host, Docker Compose, Caddy in front for TLS. Postgres, Redis, the API, the frontend and the
proxy all run on the same machine.

This is the shape that fits the product today. **BoardSync cannot scale to zero** — three background
services must keep running whether or not anyone is using the site:

- `OutboxDispatcher` drains domain events into the activity feed. Asleep, events are written and
  never delivered, and the feed silently stops.
- `JobWorker` processes git webhook deliveries. Asleep, a webhook hits a cold start and the provider
  may time out and mark the delivery failed — which is the product's whole premise breaking.
- `SprintScheduler` is time-based. No requests means no wakeups means no runs.

SignalR also holds long-lived websockets. So the free tiers built around scale-to-zero — Render's
free web services, Cloud Run and Container Apps without a minimum replica — are the wrong shape,
however cheap. An always-on small VM is the right one.

---

## 1. What you need first

| | |
| --- | --- |
| A host | 2 vCPU / 4GB is comfortable. Oracle Cloud Always Free, Hetzner CX22, or similar. |
| Docker | Engine 24+ with the Compose plugin. |
| A domain | An `A` record (and `AAAA` if you have IPv6) pointing at the host, **resolving before you start the stack**. |
| Ports 80 and 443 | Open to the internet. Port 80 is not optional — it is how the certificate is issued. |z
| An SMTP sender | Brevo, Resend, Mailgun, Postmark. See §3. |

### On ARM hosts

Oracle's free tier is ARM. Every image here is multi-arch and builds natively, so building **on the
host** just works. If you build elsewhere and push, you must target the host's architecture:

```bash
docker buildx build --platform linux/arm64 ...
```

An `exec format error` on startup is this and nothing else.

---

## 2. Clone both repositories

The frontend is a **separate git repository**, and `ui/` is gitignored here. Cloning only the
backend leaves `ui/boardsync` empty and the `ui` image will not build.

```bash
git clone <backend-repo> boardsync
cd boardsync
git clone <frontend-repo> ui/boardsync
```

---

## 3. Configure

```bash
cp .env.sample .env
```

Everything below must be set before the first start. The API **refuses to boot** without several
of them, deliberately — a half-configured deployment should fail loudly rather than run wrong.

| Variable | Notes |
| --- | --- |
| `DOMAIN` | Bare hostname, no scheme. Caddy issues the certificate for this. |
| `ACME_EMAIL` | Let's Encrypt mails expiry warnings here. |
| `APP_ORIGIN` | `https://<DOMAIN>`. Startup throws if empty in Production. Also what invitation links are built from. |
| `APP_BASE_URL` | `https://<DOMAIN>`. Confirmation and reset links, which are API endpoints. |
| `POSTGRES_DB` / `_USER` / `_PASSWORD` | Generate the password; do not keep the sample. |
| `JWT_SECRET` | 32+ random characters. `openssl rand -base64 48`. Startup rejects anything containing `${`. |
| `JWT_ISSUER` / `JWT_AUDIENCE` | Any stable strings. |
| `SMTP_SERVER` / `SENDER_EMAIL` / `SMTP_USERNAME` / `SMTP_PASSWORD` | **Not optional.** Invitations fail without working mail — see below. |
| `STORAGE_CONNECTION_STRING` | Optional. Azure Storage connection string with an account key. Without it the picture upload endpoints answer 503 and nothing else changes. |
| `INTELLIGENCE_PROVIDER` + key | Optional. Without a key, decomposition and narratives answer "not configured"; every computed figure on the reports page is unaffected. |

### Email is load-bearing

It used to be that mail only affected confirmation and password resets. Since invitations landed,
**sending an invitation fails outright if the message cannot be delivered** — the endpoint reports
it rather than pretending. Configure a real sender before inviting anyone.

Free tiers that are enough for a small team: Brevo (300/day), Resend (100/day).

### Object storage, if you want profile pictures

The account needs CORS rules allowing `https://<DOMAIN>` to `PUT`, because the browser uploads
straight to the blob endpoint rather than through the API. Without them every upload fails
preflight with nothing in the API logs. Either configure the account once in the portal, or set
`Storage__ConfigureCors=true` and let the app write the rules on first use.

---

## 4. Start

```bash
docker compose -f docker-compose.prod.yaml up -d --build
```

The first start issues a certificate. Watch it happen:

```bash
docker compose -f docker-compose.prod.yaml logs -f caddy
```

You want a line naming your domain and `certificate obtained successfully`. If instead you see the
ACME challenge failing, stop and fix DNS or the firewall before retrying — see §7.

Then:

```bash
curl -I https://<DOMAIN>/              # the app
curl    https://<DOMAIN>/healthz       # Healthy
curl    https://<DOMAIN>/healthz/ready # Healthy
```

**The probe path is `/healthz`, not `/api/healthz`.** `/api/healthz` reaches the API, matches no
route, and comes back 401 from the authorization fallback. Caddy routes `/healthz*` straight to the
API for this reason — see §5.

### Testing without burning certificate quota

Let's Encrypt allows **5 duplicate certificates per domain per week**. A misconfigured domain can
exhaust that in an afternoon and lock you out of HTTPS for days. While you are still getting DNS or
firewall right, uncomment `acme_ca` in `deploy/Caddyfile` to use the staging CA — browsers will
distrust the certificate, which is the point, and the quota is effectively unlimited. Comment it
out and `docker compose restart caddy` when the challenge succeeds.

---

## 5. How the request path fits together

```
browser ──HTTPS──▶ caddy ──┬── /healthz*  ──────────────────────▶ api
                    :443   │                                        :8080
                           │
                           └── everything else ──▶ ui (nginx) ──▶ api
                                                   :80
                                                    ├── /api/*  ─▶ api
                                                    ├── /hubs/* ─▶ api  (websockets, 1h timeouts)
                                                    └── the rest ─▶ the SPA from disk
```

Health is the one path Caddy handles itself, because nginx proxies only `/api` and `/hubs` and
everything else falls through to the SPA — so before this, an external probe for `/healthz` got
200 and a page of HTML whether the API was alive or dead.

Caddy only terminates TLS. Routing stays in the frontend's nginx, which already knows how to split
the origin — so the `ui` image is deployable elsewhere unchanged, and there is one place to look
when a path goes somewhere unexpected.

**Two proxies is why `ForwardedHeaders__ForwardLimit` is 2.** The middleware walks
`X-Forwarded-For` from the right exactly that many times. Left at the default of 1 it stops a hop
short and hands back Caddy's container address as the client — quietly, with everything still
appearing to work, while every anonymous rate limit collapses into one bucket and one address gets
recorded against every failed login. If you change the number of proxies, change this too.

---

## 6. Operating it

### Deploying a new version

```bash
cd boardsync && git pull
cd ui/boardsync && git pull && cd ../..
docker compose -f docker-compose.prod.yaml up -d --build
```

Migrations apply on API startup (`Database__AutoMigrate: "true"`). That is a deliberate choice for
a single-host deployment — with one instance there is nothing to race, and it removes a step that
is easy to forget. Under more than one API instance, turn it off and run
`dotnet ef database update` as a release step instead; the startup path takes a Postgres advisory
lock, but a failed migration should stop a rollout rather than surface as crash-looping replicas.

### Shutdown behaviour

`stop_grace_period: 40s` on the `api` service is deliberate and should stay above the host's own
`ShutdownTimeout` (30s, the .NET default). Docker's default of 10s is *shorter* than the budget the
app works to, so it would kill the process partway through its own drain. On shutdown the API
finishes in-flight requests, hands any claimed job back to the queue with its attempt returned, and
fails `/healthz/ready` before it stops accepting.

### Probes, if you put anything in front

Liveness `→ /healthz`, readiness `→ /healthz/ready`. They differ only while shutting down,
which is the window that matters: readiness fails as soon as shutdown begins so the instance leaves
rotation while it can still finish what it holds. Liveness stays healthy throughout — a draining
instance is not a faulty one, and failing liveness invites a restart of something already leaving.

### Backups

`pgdata` is the only volume holding anything you cannot rebuild.

```bash
docker exec boardsync-postgres pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB" | gzip > boardsync-$(date +%F).sql.gz
```

Put that on a timer and copy it off the host. A backup on the same disk as the database is not a
backup.

`caddydata` holds certificates and the ACME account key — losing it costs a re-issue, not data, but
re-issuing repeatedly is how you hit the rate limit.

### Logs

```bash
docker compose -f docker-compose.prod.yaml logs -f api
docker compose -f docker-compose.prod.yaml logs -f caddy
```

---

## 7. When it does not work

**No certificate; Caddy logs an ACME failure.** In order of likelihood: DNS is not pointing here
yet (`dig +short <DOMAIN>` against the host's public IP), port 80 is closed at the cloud firewall
as well as the host one — Oracle and AWS both have a security-list layer separate from `ufw` — or
something else on the host already holds port 80 (`ss -ltnp | grep :80`).

**`AllowedOrigins must be explicitly configured in production`.** `APP_ORIGIN` is empty in `.env`.

**An uptime check on `/healthz` always passes, even with the API down.** You are hitting the SPA,
not the API — that is what happens if `/healthz*` is not routed to `api:8080`. The bundled Caddyfile
does it; a different proxy in front needs the same rule, or the check is worthless.

**The first account will not register: "Password validation failed".** Production policy is
stricter than development — 12 characters minimum, with upper, lower, digit and a special
character. See `SecuritySettings` in `appsettings.Production.json`.

**Registering returns 500 "User created but failed to send confirmation email".** The account
*was* created; only the mail failed. Fix SMTP, then use resend-confirmation rather than registering
again — the address is already taken.

**The app loads but every API call 502s.** The API is not up. `docker compose logs api` — usually
Postgres credentials, or a `JWT_SECRET` shorter than 32 characters.

**Realtime never connects; the badge says "reconnecting".** Something between the browser and nginx
is dropping the websocket upgrade. Caddy and the bundled nginx both handle it; a corporate proxy or
an extra CDN in front may not.

**Invitations report "created but the email could not be sent".** SMTP credentials. The invitation
row is kept deliberately — fix the settings and send it again from the members screen rather than
re-creating it.

**Profile picture uploads fail in the browser with nothing in the API logs.** CORS on the storage
account. The browser uploads directly to blob storage, so a failed preflight never reaches the API.

**`exec format error`.** An image built for the wrong architecture — see §1.

---

## 8. What this setup does not give you

- **No horizontal scaling.** One API instance. Redis is already wired for a shared cache and
  backplane, so a second instance is mostly a matter of putting a load balancer in front and
  turning `Database__AutoMigrate` off — but nothing here is doing that today.
- **No log aggregation or metrics.** `Telemetry:OtlpEndpoint` is unset, so OpenTelemetry is not
  registered at all. Point it at a collector when you want traces.
- **Database and app share a host.** Fine for a small team; the first thing to split out when it
  stops being fine.
