# Etikra

Etikra is an open-source Windows label designer for SUPVAN and KATASYMBOL thermal label printers. It is a native C#/.NET 10 WPF application with an offline editor, a safe mock printer, print-ready PNG export, an experimental direct USB HID backend, and a live-tested E12 Bluetooth LE backend.

> Etikra is an independent community project. It is not affiliated with or endorsed by SUPVAN or KATASYMBOL. Direct printing is based on public reverse-engineering work and needs testing on more physical printers.

## What works

- Millimetre-based label canvas with drag, resize, keyboard nudging, rotation, and numeric positioning.
- Text, Code 128-B barcodes, rectangles, lines, and embedded PNG/JPEG/BMP images.
- `.etikra` JSON project files and 300 DPI PNG export.
- Mock printing to `%LOCALAPPDATA%\Etikra\Mock Prints` without printer hardware.
- Windows USB HID discovery restricted to SUPVAN vendor ID `1820`.
- Guarded direct USB backend for known T50, T80, G, TP76, TP80, TP86, and SP650 product IDs.
- Native Windows BLE discovery, persistent GATT notification transport, and E12 printing through `FEE7/FEC1`.
- Read-before-print interrogation for model, firmware, status, RFID material metadata, loaded width/height/gap/type, and dots/mm.
- A **Use loaded media size** action plus a hard pre-print geometry recheck; the E12's `12 mm head × 40 mm feed` reply becomes a conventional `40 × 12 mm` landscape editor canvas.
- A live 1-bit thermal-dot preview and dashed E12 print-safe guide. Elements crossing the approximately 1 mm boundary are blocked before any print bytes are sent.
- Printer status/error handling for cover-open, missing/empty labels, ribbon faults, thermal faults, and busy states.
- Dependency-free executable test harness for barcode, raster, buffer, checksum, model, and LZMA verification.

## Hardware status

| Connection | Etikra status | Notes |
|---|---|---|
| Mock/file output | Ready | Default and safe without hardware. |
| USB HID | Experimental | Protocol is implemented; this Windows port has not yet been validated on physical hardware in this repository. |
| Bluetooth Classic SPP | Researched, not implemented | Public work documents shared commands with different `7E 5A` framing. |
| Bluetooth LE GATT | Experimental, live E12 path | Discovery, configuration queries, raster framing, and printing are implemented. Live unit `T0188A2602242874` reported model `G15`, firmware `1`, `8 dots/mm`, and loaded `12 × 40 mm` media with a `3 mm` gap. Its firmware can omit the final `BUF_FULL` echo, so Etikra verifies completion through status polling. |

Known USB product IDs and head profiles come from the [supvan-cups model registry](https://github.com/heeen/supvan-cups/blob/master/data/models.toml). Unknown PID values are displayed but blocked from printing so Etikra never guesses a raster width.

## Build and run

Requirements:

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
dotnet restore Etikra.slnx
dotnet build Etikra.slnx
dotnet run --project Etikra.csproj
```

Run the local checks:

```powershell
dotnet run --project Tests\Etikra.Tests.csproj
```

## Using Etikra

1. Set the physical label width and height in millimetres, or let a compatible Bluetooth printer report them.
2. Add elements from the left palette. Drag them on the canvas or edit exact values in the inspector.
3. Save the editable project as `.etikra` or export a 300 DPI PNG.
4. Start with **Preview / mock printer**. Etikra writes the rendered output to the local mock-print folder.
5. For direct USB, connect a supported printer, choose **Refresh USB + Bluetooth**, select it, load the correct media, then print. Etikra shows a confirmation before sending protocol bytes.
6. For E12 Bluetooth, select the discovered printer and review its live configuration. Choose **Use loaded media size**; Etikra queries the printer again and refuses to send raster data if the design and loaded label disagree.

Etikra rotates the landscape editor raster into the printer's feed coordinates and compensates for the E12's reversed printhead-dot order. The editor therefore matches the physical label instead of exposing the printer's portrait wire orientation.

Holding Ctrl while dragging disables the normal 0.5 mm snap. Arrow keys nudge by 0.5 mm; Shift+arrow nudges by 1 mm. Ctrl+D duplicates and Delete removes the selected element.

## Protocol foundations

The main source is [`heeen/supvan-cups`](https://github.com/heeen/supvan-cups), an MIT-licensed Rust driver whose [protocol notes](https://github.com/heeen/supvan-cups/blob/master/docs/PROTOCOL.md) document the T-series command set from vendor-app analysis and hardware captures. Its key findings used here are:

- USB VID `1820`, HID commands beginning `C0 40`, and big-endian command parameters.
- `CHECK_DEVICE → ready → START_PRINT → printing → NEXT_ZIPPEDBULK → raster bytes → BUF_FULL → complete`.
- Column/feed-major LSB-first raster rows inside checksummed 4096-byte print buffers.
- LZMA1-alone compression with an 8192-byte dictionary, `lc=3`, `lp=0`, `pb=2`, and an exact uncompressed-size header.

The separate [`katasymbol-e12-lab`](https://github.com/eteriall/katasymbol-e12-lab) project supplies the E12 BLE service map, 16-byte command frames, 512-byte compressed raster frames, and tested timing Etikra follows. Etikra additionally verifies loaded media and resolution from live replies instead of using the reference tool's default label dimensions.

See [docs/PROTOCOL.md](docs/PROTOCOL.md) for Etikra's implementation notes and verification boundary.

## Project layout

```text
Models/                 Editable label document model
Services/               JSON persistence, Code 128, WPF raster rendering
Printing/               Model registry, Windows HID, SUPVAN buffers/protocol
Tests/                  Executable protocol and raster checks
docs/PROTOCOL.md        Reverse-engineering notes and implementation map
```

## Contributing

Hardware testing is especially valuable. Please include the exact model, USB VID/PID or Bluetooth advertisement, label dimensions, transport, observed result, and a redacted trace where possible. Do not test firmware-update commands: Etikra deliberately contains no flashing path.

See [CONTRIBUTING.md](CONTRIBUTING.md). The project is available under the [MIT License](LICENSE).
