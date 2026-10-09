# RFID Label Printer

A two-part system for printing and encoding RFID jewelry tags. The **API** runs on a server and queues jobs, the **Local Agent** runs on the machine connected to the printer and processes them.

## How it works

You POST a barcode (or PDF URL) to the API. The API stores it as a pending job. The Local Agent polls the API every 500ms, picks up pending jobs, generates ZPL, and sends it straight to the Zebra printer over TCP. That's it.

## Folder structure

```
publish_latest_v2/     — API build (deploy this to your server)
publish_latest_agent/  — Local Agent build (put this on the printer PC)
src/                   — source code
sql/schema.sql         — database tables
```

## Getting started

**1. Database**

Run this once in SQL Server, then execute `sql/schema.sql`:
```sql
CREATE DATABASE RfidLabelPrinter;
```

**2. API (`publish_latest_v2`)**

Open `appsettings.json` and update the connection string:
```json
"DefaultConnection": "Server=YOUR_SERVER;Database=RfidLabelPrinter;User Id=sa;Password=YOUR_PASS;TrustServerCertificate=true;"
```
Then run `RfidLabelPrinter.Api.exe`. Listens on port `5170` by default.

**3. Local Agent (`publish_latest_agent`)**

Open `appsettings.json` and set:
```json
"CloudApiUrl": "http://<your-server-ip>:5170/api/print-jobs",
"PrinterIp": "192.168.x.x",
"PrinterPort": 9100
```
Run `RfidLabelPrinter.LocalAgent.exe`. It starts polling immediately — no other setup needed.

## Sending a print job

**Single label via PDF URL**
```http
POST /api/print-jobs
Content-Type: application/json

{
  "url": "http://yourserver.com/barcodes/barcode_FE3B3F000011.pdf",
  "barcode": "FE3B3F000011"
}
```
If the PDF filename follows the pattern `barcode_XXXX.pdf`, you can skip the `barcode` field — it gets extracted automatically.

**Multiple labels at once**
```http
POST /api/print-jobs
Content-Type: application/json

{
  "labels": [
    { "barcode": "FE3B3F000011", "url": "http://yourserver.com/barcodes/barcode_FE3B3F000011.pdf" },
    { "barcode": "FE3B3F000012", "url": "http://yourserver.com/barcodes/barcode_FE3B3F000012.pdf" }
  ]
}
```

**Raw ZPL**
```http
POST /api/print-jobs
Content-Type: application/json

{
  "labels": [
    { "barcode": "FE3B3F000011", "labelZpl": "^XA^FO50,50^ADN,36,20^FDFE3B3F000011^FS^XZ" }
  ]
}
```

## Checking job status

```http
GET /api/print-jobs
```

Statuses: `Pending` → `Processing` → `Success` or `Failed`

To retry a single failed job:
```http
POST /api/print-jobs/{id}/retry
```

To retry everything that failed:
```http
POST /api/print-jobs/retry-all-failed
```

## RFID configuration

All in `publish_latest_agent/appsettings.json`.

`RfidWordCount` controls how many EPC words get written. Rule of thumb: **hex digits ÷ 4**.

```
12 hex digits  →  3 words
16 hex digits  →  4 words
24 hex digits  →  6 words
```

Keep `UseEpcBankWriteFormat: true`. This writes the PC word alongside the EPC so readers know exactly how long the EPC is. Without it, readers pick up leftover data from the tag and show more digits than you wrote.

`RfidHexInputMode: "Always"` means barcodes are always treated as hex. Set it to `"Never"` if your barcodes are plain text (ASCII).

To disable RFID encoding and just print the label: `EnableRfidWrite: false`.

## ZPL dump (debugging)

If you need to see exactly what ZPL is being sent to the printer:
```json
"DumpZplToFile": true,
"DumpZplPath": "C:\\logs\\zpl-dump.txt"
```
Every job gets appended to that file with a timestamp and job ID.

## PDF render settings

These affect how PDFs are converted to printable images:

| Setting | What it does | Default |
|---------|--------------|---------|
| `PdfRenderDpi` | Render resolution — match your printer DPI | `203` |
| `BlackThreshold` | Pixels darker than this become black | `230` |
| `AlphaThreshold` | Pixels more transparent than this become white | `20` |
| `LabelWidthDots` | Label width in dots | `600` |
| `LabelLengthDots` | Label height in dots | `400` |

## Troubleshooting

**Agent exe crashes immediately**
The build is framework-dependent and .NET isn't installed. Republish as self-contained:
```
dotnet publish -r win-x64 --self-contained true
```

**Jobs failing with 404**
The PDF file doesn't exist at the URL stored in the job. Check whatever system generates and uploads those PDFs.

**Reader shows 24 digits instead of 12**
`UseEpcBankWriteFormat` is `false`. Set it to `true` and make sure `RfidWordCount` matches your EPC length.

**Barcode getting mangled (e.g. `FE3B3F` instead of the full value)**
This happens when the barcode has an odd number of hex digits — the hex check fails and it falls back to ASCII encoding, then gets truncated. Always use an even number of hex digits. Pad with a leading zero if needed: `0FE3B3F12345`.
|---------|-------|-----|
| Agent exe does not open | .NET runtime missing | Republish as self-contained: `dotnet publish -r win-x64 --self-contained true` |
| Job `Failed` — 404 error | PDF file missing on server | Check the PDF generation system |
| RFID contains extra random digits | `UseEpcBankWriteFormat: false` | Set it to `true` |
| Barcode being ASCII-encoded instead of hex | Odd-length hex barcode | Use an even number of hex digits (e.g. `0FE3B3F12345`) |
| Agent cannot connect to printer | Incorrect IP or port | Verify `PrinterIp` and `PrinterPort` |

---

## PDF Rendering Settings

| Setting | Description | Recommended |
|---------|-------------|-------------|
| `PdfRenderDpi` | PDF render quality | `203` (match your printer DPI) |
| `BlackThreshold` | Darkness level for black pixels | `230` |
| `AlphaThreshold` | Transparent pixel handling | `20` |
| `LabelWidthDots` | Label width in dots | `600` |
| `LabelLengthDots` | Label height in dots | `400` |
