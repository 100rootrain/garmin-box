# garmin-box

[![README: Korean](https://img.shields.io/badge/README-Korean-blue)](README.md)

Displays Garmin Connect activity statistics in a GitHub profile gist. It replaces
strava-box after Strava's June 2026 policy change blocked the free API.

```
Scheduler (once daily)
 └─ run_garmin_batch (.ps1 on Windows / .sh on Linux)
     ├─ garmindb_cli : Garmin Connect -> SQLite
     ├─ sync_supa.py : SQLite -> Supabase garmin_activity upsert
     └─ update_gist.py : YTD/all-time statistics -> gist update
```

> **Note:** Garmin blocks logins from cloud/data-center IP addresses (such as OCI)
> with HTTP 429. Run the `--download` step only from a PC on a normal home or
> office IP address. The recommended setup is therefore a Windows PC with Task
> Scheduler using `run_garmin_batch.ps1`.

## Setup (recommended: Windows PC)

    git clone https://github.com/100rootrain/garmin-box.git
    cd garmin-box
    py -m venv .venv
    .venv\Scripts\pip install -r requirements.txt

1. Set up GarminDB at `%USERPROFILE%\.GarminDb\GarminConnectConfig.json`.
   See `.venv\Lib\site-packages\garmindb\GarminConnectConfig.json.example` for an example.
2. Create `%USERPROFILE%\.garmin-box.env` with your secrets using the format below.
   See `.env.example`. `SUPA_DSN` must be key=value conninfo, not a URL:

       SUPA_DSN=host=... port=5432 dbname=postgres user=... password=...
       GITHUB_TOKEN=gist-scoped token
       GIST_ID=gist id

3. Run `schema.sql` in Supabase once.
4. Run manually: `powershell -ExecutionPolicy Bypass -File .\run_garmin_batch.ps1`
5. Register a daily task (09:10 example). Use the `run_garmin_batch_hidden.vbs`
   launcher to keep the PowerShell window hidden; running the `.ps1` directly
   opens a PowerShell window each time:

       schtasks /Create /TN garmin-box /SC DAILY /ST 09:10 /TR "wscript.exe \"C:\path\to\garmin-box\run_garmin_batch_hidden.vbs\""

   Output is written to `batch.log`, so failures remain traceable while the
   window stays hidden.

## Setup (Linux server / OCI — cloud IPs may receive Garmin 429)

> **This server is OCI (Oracle Cloud Infrastructure).** Garmin Connect blocks
> logins and `--download` requests from cloud/data-center IPs such as OCI with
> HTTP 429. Running the full `run_garmin_batch.sh` on OCI therefore fails at the
> download step. Use OCI only for the downstream steps that do not contact Garmin
> (`sync_supa` and `update_gist`), or as a gist-update-only scheduler. Download
> Garmin data from a Windows PC on a home or office IP address.

    git clone https://github.com/100rootrain/garmin-box.git && cd garmin-box
    python3 -m venv .venv
    .venv/bin/pip install -r requirements.txt

1. Set up GarminDB at `~/.GarminDb/GarminConnectConfig.json`.
   See `.venv/lib/python*/site-packages/garmindb/GarminConnectConfig.json.example` for an example.
2. Create the secrets file and enter your values:
   `cp .env.example ~/.garmin-box.env && chmod 600 ~/.garmin-box.env`
3. Run `schema.sql` in Supabase once.
4. Run the downstream steps manually:
   `.venv/bin/python sync_supa.py && .venv/bin/python update_gist.py`
   (including the Garmin download in `./run_garmin_batch.sh` fails with HTTP 429 on OCI IPs)
5. Add a daily gist-update-only cron job at 03:10:
   `10 3 * * * cd /home/USER/garmin-box && .venv/bin/python update_gist.py >> update_gist.log 2>&1`

## Development

    .venv/bin/python -m pytest -v
    .venv/bin/python update_gist.py --dry-run

Never commit secrets. See `.gitignore` for `.env`, logs, and token files.
