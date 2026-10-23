# RFID Label Printer (Zebra ZD621T)

A high-reliability system for printing and encoding UHF RFID jewelry tags (dumbbell / butterfly / flag tags). 

* **Backend API (`publish_latest_v2`)**: Runs on your server/cloud, accepts print jobs via REST API, and stores them in SQL Server.
* **Local Agent (`publish_latest_agent`)**: Runs on the Windows machine connected to the Zebra ZD621T printer over TCP network, polls pending jobs, rasterizes PDF designs, injects Gen2 RFID write commands (`^RFW`), and delivers raw ZPL graphics over port 9100.

---

## Folder Structure

```text
publish_latest_v2/     — Backend REST API build (deploy to server)
publish_latest_agent/  — Local Agent self-contained build (deploy to printer PC)
publish_latest_agent.zip — Compressed deployable package for Local Agent
src/                   — C# .NET 8 source code
sql/schema.sql         — SQL Server database initialization script
```

---

## Getting Started

### 1. Database Setup
Create database in SQL Server and run [sql/schema.sql](file:///p:/perso_l/write_test/sql/schema.sql):
```sql
CREATE DATABASE RfidLabelPrinter;
GO
```

### 2. Backend API Setup (`publish_latest_v2`)
In `publish_latest_v2/appsettings.json`, set your SQL Server connection:
```json
"ConnectionStrings": {
  "DefaultConnection": "Server=YOUR_SERVER;Database=RfidLabelPrinter;User Id=sa;Password=YOUR_PASS;TrustServerCertificate=true;"
}
```
Run `RfidLabelPrinter.Api.exe` (listens on port `5170` / `7000` by default).

### 3. Local Agent Setup (`publish_latest_agent`)
In `publish_latest_agent/appsettings.json`, configure connection to API and Zebra printer:
```json
"AgentSettings": {
  "CloudApiUrl": "http://YOUR_SERVER_IP:7000/api/print-jobs",
  "PrinterIp": "192.168.29.180",
  "PrinterPort": 9100
}
```
Run `RfidLabelPrinter.LocalAgent.exe`. It begins processing immediately with auto-reconnect and crash recovery.

---

## API Usage

### Single Label via Remote PDF URL
```http
POST /api/print-jobs
Content-Type: application/json

{
  "url": "http://yourserver.com/labels/barcode_FE804A1F62.pdf",
  "barcode": "FE804A1F62"
}
```
*(If the filename matches `barcode_XXXX.pdf`, the `barcode` parameter is extracted automatically).*

### Batch Labels
```http
POST /api/print-jobs
Content-Type: application/json

{
  "labels": [
    { "barcode": "FE804A1F62", "url": "http://yourserver.com/labels/FE804A1F62.pdf" },
    { "barcode": "FE837F1F70", "url": "http://yourserver.com/labels/FE837F1F70.pdf" }
  ]
}
```

### Raw ZPL Label
```http
POST /api/print-jobs
Content-Type: application/json

{
  "labels": [
    { "barcode": "FE804A1F62", "labelZpl": "^XA^FO50,50^ADN,36,20^FDFE804A1F62^FS^XZ" }
  ]
}
```

### Check Job Queue Status
```http
GET /api/print-jobs
```
Statuses: `Pending` → `Processing` → `Success` / `Failed`

---

## Physical Label Geometry (Jewelry Tag)

* **Printer Resolution**: 300 DPI (Zebra ZD621T RFID)
* **Total Physical Size**: 100mm × 50mm = **1181 dots width × 591 dots length**
* **Tag Layout**:
  * **Left Side (Loop / Tail)**: Thin strip with RFID antenna inlay that loops through jewelry rings/chains.
  * **Right Flap (Foldable Wings)**: Divided horizontally by a fold crease into:
    * **Upper Flap (Side 1)**: Inverted 180° text (Item name, pearl details, polish) so it reads upright when folded.
    * **Lower Flap (Side 2)**: Upright text (Brand name, Barcode, Price, Rate).

---

## Calibrated Settings (`appsettings.json`)

All values below are live-reloaded automatically by the agent without needing a restart:

| Parameter | Calibrated Value | Description |
| :--- | :---: | :--- |
| `PrinterDpi` | `300` | Zebra printer hardware head resolution (300 DPI) |
| `LabelWidthDots` | `1181` | Total physical label width in dots (100mm @ 300 DPI) |
| `LabelLengthDots` | `591` | Total physical label length in dots (50mm @ 300 DPI) |
| `PrintOffsetX` | `690` | Horizontal alignment in dots (Centers 42% design onto Right Flap) |
| `PrintOffsetY` | `105` | Vertical alignment in dots (Locks 4-line and 3-line safely above fold crease) |
| `PdfSizePercent` | `42` | Design scale factor percentage (Calibrated compact jewelry flap fit) |
| `SingleSidePrintOnLowerFlap` | `true` | Automatically detects single-sided templates and places them on Lower Flap |
| `SingleSideOffsetY` | `225` | Calibrated vertical offset in dots for single-sided labels |
| `TemplateBaseTopPercent` | `22.5` | Anchor ratio percentage anchoring 4-line reverse wings so fold line never slides |
| `SingleSideThresholdPercent` | `45` | White-space threshold ratio classifying single-sided vs double-sided templates |
| `TrimPaddingDots` | `4` | Retained border padding in dots after trimming dead whitespace |
| `AutoTrimWhitespace` | `true` | Trims blank outer margins from incoming PDF vector layouts |
| `PrintPdfAsGraphics` | `true` | Renders PDF as high-resolution ZPL graphic bitmaps (`^GFA`) |
| `PdfRenderDpi` | `300` | SkiaSharp PDF rasterization resolution (300 DPI) |
| `BlackThreshold` | `240` | Darkness threshold (0-255) for crisp, solid black ink conversion |
| `AlphaThreshold` | `5` | Opacity threshold (0-255) ensuring thin vector strokes are captured |

---

## Fold Line Crease & Anchor Math

To ensure mixed print queues (3-line labels, 4-line labels, and single-sided labels) never slide across the physical fold line:

1. **4-Line Reverse Wing**: Contains an extra line at top (`Silver Polish`). Content starts at `TemplateBaseTopPercent = 22.5%`. It starts at `PrintOffsetY = 105` and its 4th line finishes comfortably above the fold crease.
2. **3-Line Reverse Wing**: Top line is absent in the source template. The agent shifts it by exactly 1 line height so that common lines and the bottom line end at the **exact same vertical coordinate** as the 4-line template.
3. **Single-Sided Label**: Ink is confined to the lower half. The agent places it at `SingleSideOffsetY = 225` onto the Lower Flap near the jewelry tail.

---

## RFID Encoding Configuration

The agent encodes UHF EPC Gen2 memory using standard `^RFW` commands:

```json
"EnableRfidWrite": true,
"RfidMode": "H",
"RfidBlockNumber": 1,
"RfidStartWord": 2,
"RfidWordCount": 4,
"UseEpcBankWriteFormat": true,
"RfidHexInputMode": "Always"
```

* **Word Count**: `RfidWordCount = 4` writes 4 words = 8 bytes = **16 hex characters** (e.g. `FE804A1F62000000`).
* **PC Word**: `UseEpcBankWriteFormat: true` writes the Gen2 Protocol Control (PC) word at EPC Word 1 prior to data, ensuring handheld and fixed readers read exact EPC length without trailing garbage.
* **Hex Barcodes**: Barcodes are treated as Hex strings. Odd-length barcodes are automatically padded with a leading zero.

---

## Troubleshooting Guide

| Issue | Cause | Solution |
| :--- | :--- | :--- |
| **Agent crashes on startup** | Missing .NET Runtime | Run self-contained build: `publish_latest_agent/RfidLabelPrinter.LocalAgent.exe` |
| **Job fails with 404** | PDF URL not reachable | Verify PDF file exists and URL is accessible from printer PC |
| **Print is too far Left/Right** | `PrintOffsetX` calibration | Adjust `PrintOffsetX` (increase to move Right, decrease to move Left) |
| **Print touches fold crease** | `PrintOffsetY` calibration | Decrease `PrintOffsetY` (e.g., from `120` to `105`) to pull print upward |
| **RFID tag reads 24 hex instead of 16** | PC word not written | Ensure `UseEpcBankWriteFormat` is set to `true` |
| **Agent cannot connect to Zebra** | Network / Port mismatch | Check `PrinterIp` in `appsettings.json` and ensure printer TCP port `9100` is open |
