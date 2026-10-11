# Research Form ETL

Upload the research-form .xlsx -> ETL runs -> pick columns -> download transformed .xlsx.

## Run
Only Docker + Docker Compose are needed on the server (no .NET install).

    docker compose up -d --build

Open http://localhost:8085 (or http://<server-ip>:8085 from another device).

    docker compose down      # stop it whenever you don't need it

Nothing is stored on disk: the uploaded data is kept in memory for 1 hour, then discarded.
There is no login, so keep it on your LAN.

## Where the ETL rules live
`Etl/Transformer.cs` - every rule from the "Instructions For AI" sheet
(renames, Caste/Ethnicity clean-up, disease classification, Age, Is Sample Twin, 2D:4D ratios).
`Etl/ExcelIo.cs` - reading the upload / writing the export.
`wwwroot/index.html` - the page.
