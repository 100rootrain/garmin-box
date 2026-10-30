# garmin-box

[![README: Korean](https://img.shields.io/badge/README-Korean-blue)](README.md)

Stores Garmin Connect activities in Supabase and shows yearly and all-time
statistics in a GitHub profile gist. On Windows, a single `GarminBox.exe`
handles installation, connection checks, the first sync, and the daily task.

> **Note:** Garmin blocks logins from cloud/data-center IP addresses (such as OCI)
> with HTTP 429. Download Garmin data only from a PC on a normal home or office
> IP address.

## Requirements

- A Windows 10/11 64-bit PC with internet access. Python and Git are not needed.
- Your Garmin Connect email and password. If two-step verification is on, you
  enter the code inside the program during the connection check.
- A Supabase database connection string. Both a URI (`postgresql://...`) and
  key=value conninfo (`host=... password=...`) work.
  [Find your connection string](https://supabase.com/docs/guides/database/connecting-to-postgres)
- A GitHub token with only the gist scope
  ([create](https://github.com/settings/tokens/new?scopes=gist&description=garmin-box))
  and the gist that shows the statistics ([new gist](https://gist.github.com/)),
  entered as a URL or ID.

## 1. Install

1. Download `GarminBox.exe` from the
   [latest release](https://github.com/100rootrain/garmin-box/releases/latest)
   and run it. The file is not code-signed, so if SmartScreen appears choose
   **More info → Run anyway**.
2. The program installs itself and its Python runtime into
   `%LOCALAPPDATA%\garmin-box` and adds `garmin-box` to the Start menu. No
   administrator rights are needed.
3. Enter the Garmin email and password, Supabase connection string, GitHub
   token, gist URL or ID, and the daily run time.
4. Press **연결 확인** (check connection). It checks the Garmin login, the
   Supabase connection, and the GitHub token and gist owner. It does not change
   tables or the gist.
5. Choose the gist file to update (default `garmin-stats`). The window shows
   whether the gist is public and which file will be updated.
6. Press **설정 시작** (start setup). It saves the settings, creates the
   Supabase table, uploads all activities, updates the gist, and registers the
   daily task. The first sync can take a while. If a step fails, it stops there;
   fix the cause and press it again.

> Windows accounts whose user name contains non-ASCII characters (for example
> `C:\Users\홍길동`) are not supported yet. The program says so at startup and
> installs nothing.

Passwords and tokens are stored as plain text in
`%USERPROFILE%\.GarminDb\GarminConnectConfig.json` and
`%USERPROFILE%\.garmin-box.env`. Access is limited to the current user, SYSTEM,
and Administrators; the files are not encrypted.

## 2. Daily automatic run

- A Task Scheduler task named `garmin-box` runs at the time you entered.
- It runs only while you are signed in to Windows. Your Windows password is
  never requested.
- If the PC was off, it runs after your next sign-in. It also runs on battery;
  sleep and power settings are not changed.
- It downloads recent activities and fills any gap with a full download.
  Supabase and the gist are updated only when every Garmin activity is present.

## 3. Reopening

Open `garmin-box` from the Start menu.

- Saved settings and the latest sync result are shown. Saved passwords and
  tokens are hidden; leaving a field empty keeps the saved value.
- To change settings, edit the values and press **연결 확인 → 설정 시작**.
- **지금 동기화** (sync now) runs a sync immediately.
- If the latest result says Garmin re-authentication is needed, leave the
  password field empty and press **연결 확인** to sign in again (enter the code
  if asked).
- One Windows user supports one Garmin account. Once a local activity database
  exists, the Garmin email cannot be changed to another account.
- Activities deleted on Garmin can remain in the Supabase and gist statistics;
  the sync result shows a warning when that happens.

## Advanced

### Manual run

`run_garmin_batch.ps1` and `run_garmin_batch_hidden.vbs` run the installed
`%LOCALAPPDATA%\garmin-box\GarminBox.exe --sync`. Before installation they only
show a notice.

    powershell -ExecutionPolicy Bypass -File .\run_garmin_batch.ps1

### Disabling an old manual task

If you registered `run_garmin_batch_hidden.vbs` or `run_garmin_batch.ps1` in
Task Scheduler yourself, setup finds that task, names it, and does not enable
the new task. Set the named task to **Disabled** in Task Scheduler, then press
**예약 다시 등록** (register schedule again). Old tasks are never deleted
automatically.

### Linux / OCI

Do not download Garmin data on cloud servers such as OCI; cloud IPs may receive
HTTP 429. Download on a Windows PC, then copy `garmin_activities.db` to
`$HOME/HealthData/DBs/garmin_activities.db` on the server. For another location,
add `GARMIN_SQLITE=/path/to/garmin_activities.db` to `$HOME/.garmin-box.env`
on the server. Set `SUPA_DSN`, `GITHUB_TOKEN`, and `GIST_ID` in the same file.

    python3 -m venv .venv
    .venv/bin/pip install -r requirements.txt
    set -a
    source "$HOME/.garmin-box.env"
    set +a
    .venv/bin/python sync_supa.py
    .venv/bin/python update_gist.py

### Development

On Windows, tests run on the same Python runtime that ships. The C# tests need
the .NET 10 SDK.

    powershell -ExecutionPolicy Bypass -File build\build-runtime.ps1
    $PY = (Resolve-Path build\out\runtime\python\python.exe)
    & $PY -I -X utf8 -m unittest discover -s tests -t . -v
    dotnet test GarminBox.slnx
    powershell -ExecutionPolicy Bypass -File build\release.ps1

To preview the statistics only, run `.venv/bin/python update_gist.py --dry-run`.

Never commit secrets. See `.gitignore` for `.env`, logs, and token files.

## License

The code in this repository is released under the [MIT License](LICENSE).

The Windows release `GarminBox.exe` bundles a Python runtime and its dependencies, each under its own license.

- [GarminDB](https://github.com/tcgoetz/GarminDB) and its author's `fitfile`, `idbutils`, and `tcxfile` are GPL-2.0. `run_batch.py` imports GarminDB directly in the same Python process, so the release EXE, which ships them together, is distributed as a whole under GPL-2.0. The code files in this repository remain MIT when used on their own.
- `psycopg` and `psycopg-binary` are LGPL-3.0, and `certifi` and part of `tqdm` are MPL-2.0, all unmodified. The rest are MIT, BSD, Apache-2.0, or PSF style. Package versions are pinned in [`build/windows-requirements.lock`](build/windows-requirements.lock).
- Where to get the source:
  - garmin-box (including the C# executable): the tag in this repository that matches the release name (for example [`v0.1.0`](https://github.com/100rootrain/garmin-box/tree/v0.1.0)).
  - Python packages: each package's license text and `.py` source remain in `%LOCALAPPDATA%\garmin-box\runtime\python\Lib\site-packages` after installation.
  - Native modules of `psycopg-binary` (the `.pyd` files for `_psycopg` and `pq`): the bundle contains only the Cython-generated `.c` files. The original Cython sources (`psycopg_c/`) and the Windows wheel build scripts (`tools/ci/`, `.github/workflows/packages-bin.yml`) are in the psycopg repository at the same version tag (currently [`3.3.6`](https://github.com/psycopg/psycopg/tree/3.3.6)).
