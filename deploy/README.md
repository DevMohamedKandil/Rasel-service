# Deploy

## Docker (API + sidecar)

```bash
cp deploy/.env.example deploy/.env      # fill the secrets, see the table
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --build
curl http://localhost:8080/health/ready
```

Volumes: `rasel-data` (SQLite database) and `rasel-session` (the linked WhatsApp account). Back both up; the session volume is equivalent to a logged-in phone. The sidecar is reachable only inside the compose network. Link WhatsApp: create a session through the API (`POST /v1/sessions`) and `GET /v1/sessions/{id}/qr` (Bridge provider), or read the sidecar log. With `WHATSAPP_PROVIDER=Fake` nothing is ever sent.

## Windows

Elevated PowerShell, .NET 10 SDK and Node 18+ installed:

```powershell
# API as a Windows service (dotnet publish + sc.exe, LocalService, delayed auto start, restart on failure)
.\deploy\install-api.ps1 -EnvFile C:\Rasel\api.env      # KEY=VALUE lines, .NET keys with "__"
.\deploy\uninstall-api.ps1

# Sidecar: a boot-time Scheduled Task (node.exe cannot be hosted by sc.exe); NSSM is the alternative, see the script header
cd sidecar; npm ci; cd ..
.\deploy\install-sidecar.ps1 -InstanceName main -SharedToken <16+ chars>   # task RaselSidecar-main, first free port from 5301
.\deploy\install-sidecar.ps1 -List
.\deploy\install-sidecar.ps1 -InstanceName main -Uninstall
```

Example `api.env`:

```
Security__OtpPepper=<64 hex>
Security__TokenSecret=<64 hex, different>
Seed__DevApiKey=<24+ chars>
WhatsApp__Provider=Bridge
WhatsApp__Bridge__BaseUrl=http://127.0.0.1:5001
WhatsApp__Bridge__SharedToken=<same as the sidecar SharedToken>
```

## Configuration

| `.env` variable | Maps to | Required | Notes |
|---|---|---|---|
| `RASEL_PORT` | host port | no (8080) | Terminate TLS in front |
| `SECURITY_OTP_PEPPER` | `Security:OtpPepper` | yes | >= 32 chars, not a dev placeholder |
| `SECURITY_TOKEN_SECRET` | `Security:TokenSecret` | yes | >= 32 chars, differs from the pepper |
| `SEED_API_KEY` | `Seed:DevApiKey` | yes | Key of the first client; only used on an empty database |
| `BRIDGE_SHARED_TOKEN` | `WhatsApp:Bridge:SharedToken` and sidecar `SHARED_TOKEN` | yes | >= 16 chars; required by the API to use plain http to `sidecar` |
| `WHATSAPP_PROVIDER` | `WhatsApp:Provider` | no (Fake) | `Fake` or `Bridge` |
| `CORS_ORIGIN` | `Cors:AllowedOrigins:0` | no | One explicit origin for the admin UI; empty = CORS off |
| `WEBHOOKS_*` | `Webhooks:*` | no | See the main README |

Every other API setting in the main README can be added to the `environment:` block of the compose file.

## Sidecar supervision (required)
The sidecar deliberately exits on unknown uncaught errors. Run it under a supervisor that restarts on exit (Docker `restart: unless-stopped`, NSSM, or the Scheduled Task with "restart on failure"), with a delay of a few seconds, and alert if it restarts repeatedly.

## Several WhatsApp numbers (manual instances)
One sidecar instance serves one linked number. Install one instance per number: `install-sidecar.ps1 -InstanceName sales -SharedToken <16+ chars> -Port 5302`. Each gets its own Scheduled Task `RaselSidecar-<InstanceName>`, its own session folder `C:Raselsessions<InstanceName>` and its own port. The script refuses an existing task name or a listening port unless `-Force`, always refuses port 5001 (Afzaz bridge), and defaults to ports from 5301. `-List` shows installed instances; `-Uninstall` removes the task and keeps the session data (delete the folder only to unlink the phone); `-DryRun` prints the plan without changing anything. Rasel talks to one sidecar at a time in Bridge mode; per-session routing to several sidecars is not implemented yet.

## Several WhatsApp numbers (manual instances)

One sidecar instance serves one linked number. Install one instance per number: `install-sidecar.ps1 -InstanceName sales -SharedToken <16+ chars> -Port 5302`. Each gets its own Scheduled Task `RaselSidecar-<InstanceName>`, its own session folder `C:Raselsessions<InstanceName>` and its own port. The script refuses an existing task name or a listening port unless `-Force`, always refuses port 5001 (Afzaz bridge), and defaults to ports from 5301. `-List` shows installed instances; `-Uninstall` removes the task and keeps the session data (delete the folder only to unlink the phone); `-DryRun` prints the plan without changing anything. In Bridge mode Rasel talks to one sidecar at a time; per-session routing to several sidecars is not implemented yet.
