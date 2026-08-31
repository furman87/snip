# Snip

Snip is a private cross-device scratchpad for text, code, Markdown, and pasted screenshots. It uses .NET 10, PostgreSQL, Dapper, Docker Compose, and OAuth sign-in.

## Features

- Private, account-scoped snippets: users only see their own saved items.
- Google, GitHub, and Microsoft account sign-in (enable any combination by adding credentials).
- Incremental full-text/title search and date/title sorting.
- Pasted text and pasted or dropped images; copy either back to the system clipboard.
- Mutable **last saved** timestamp; it updates whenever the title or payload is saved.
- Markdown preview plus lightweight syntax previews for JSON, XML, YAML, C#, and Python.

## Local run

1. Copy `.env.example` to `.env` and configure PostgreSQL plus at least one OAuth provider.
2. Register redirect URIs for every enabled provider (details below).
3. Run `docker compose up --build` and visit `http://localhost:8088`.

## OAuth setup

All callbacks must use the public HTTPS URL in production.

| Provider | Register this redirect URI |
| --- | --- |
| Google | `https://snip.fu87.app/signin-google` |
| GitHub | `https://snip.fu87.app/signin-github` |
| Microsoft account | `https://snip.fu87.app/signin-microsoft` |

For local development, replace `https://snip.fu87.app` with `http://localhost:8088`. Create an OAuth app with each provider, then put its client ID and secret in `.env`. A provider is only shown if both values are populated. GitHub uses its standard OAuth app flow; Google and Microsoft use the official ASP.NET Core handlers.

## Deploy to Ubuntu 24 at `/opt/snip`

Prerequisites: Docker/Compose, nginx, and certbot are installed; DNS for `snip.fu87.app` points at the server; firewall ports 80 and 443 are open.

```bash
sudo mkdir -p /opt
sudo chown "$USER":"$USER" /opt
git clone https://github.com/YOUR-GITHUB-USERNAME/YOUR-SNIP-REPOSITORY.git /opt/snip
cd /opt/snip
cp .env.example .env
chmod 600 .env
nano .env
docker compose up -d --build
```

Put real OAuth client credentials in `.env` before starting. Then install nginx and request the certificate:

```bash
sudo cp nginx/snip.fu87.app.conf /etc/nginx/sites-available/snip.fu87.app
sudo ln -s /etc/nginx/sites-available/snip.fu87.app /etc/nginx/sites-enabled/snip.fu87.app
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d snip.fu87.app
sudo certbot renew --dry-run
```

Certbot updates nginx to serve TLS and redirect HTTP automatically. The app is deliberately bound only to `127.0.0.1:8088`; PostgreSQL is reachable only from its Docker network.

## Updates and backups

```bash
cd /opt/snip
git pull
docker compose up -d --build

# Save the database somewhere outside this server regularly.
docker compose exec -T db pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB" > "snip-$(date +%F).sql"
```

The `postgres_data` named Docker volume holds all content. Keep the database backups and `.env` private.
