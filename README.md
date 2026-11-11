# Research Form ETL

Upload the research-form .xlsx -> renames + calculated columns are added -> filter, pick, rename, sort columns -> download the .xlsx.

Values are exported **exactly as uploaded**. The clean-up rules (Caste 'NT', Ethnicity mapping, disease classification,
removing empty "Other" columns) are optional: open **Clean-up rules** in the page, tick the ones you want, see what each
changes, and apply. Unticking a rule and applying again restores the original values.

## Run
Only Docker + Docker Compose are needed on the server (no .NET install).

    cp .env.example .env     # then edit .env: set APP_USERNAME and APP_PASSWORD
    docker compose up -d --build

Open http://localhost:8085 (or http://<server-ip>:8085 from another device).

    docker compose down      # stop it whenever you don't need it

Uploads are kept in memory for 1 hour after you last use them, then discarded. Limits: 20 MB per file and
1,000,000 cells (rows x columns) per sheet; when several uploads are open, the least recently used ones are dropped first.

The only things stored on disk are the saved filters and saved setups (SQLite file `./data/etl.db`) and the sign-in cookie keys (`./data/keys`), both in `./data`, mounted into the container at `/data`.
First run on the server (`docker-compose.yml` runs the container as user `1000:1000`, so the data folder must belong to that user; if your Debian user is different, change `user:` in the compose file and the numbers below to match):

    mkdir -p data && sudo chown 1000:1000 data

Backup: stop the container and copy `data/etl.db`, or run `sqlite3 data/etl.db ".backup 'etl-backup.db'"` while it is running.

### Login
The app has a single login, read from `APP_USERNAME` and `APP_PASSWORD` in `.env` (copy `.env.example` to `.env`;
`.env` is git-ignored and not baked into the image). The app will not start without them. To change the login, edit
`.env` and run `docker compose up -d` again. Sign-ins last 12 hours (renewed while you use the app); the cookie keys are
kept in `data/keys`, so restarting the container does not log you out. For `dotnet run` locally, the same `.env` file in
the project folder is read automatically.

## Where things live
- `Etl/Transformer.cs` - every rule from the "Instructions For AI" sheet: renames, calculated columns (Age, Is Sample Twin,
  2D:4D ratios) and the optional clean-up rules (`CleanupRules`).
- `Etl/ExcelIo.cs` - reading the upload / writing the export.
- `Etl/Filtering.cs` - column types (text / number / date) and the filters.
- `wwwroot/index.html` + `app.js` + `app.css` - the main page; `cleanup.html` - clean-up rules; `changes.html` - every changed cell.

Age is the age on the form date (`today` column); rows without a form date use the server's current date.
Dates typed as text are read day-first (`05/12/1990` = 5 December 1990).

## Tests

    dotnet test tests/EtlApp.Tests
