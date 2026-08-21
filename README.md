# Etikra

Etikra is an open-source Windows label designer for SUPVAN and KATASYMBOL thermal label printers. It is a native C#/.NET 10 WPF application with an offline editor, safe mock-file output, print-ready PNG export, an experimental direct USB HID backend, and a live-tested E12 Bluetooth LE backend.

> Etikra is an independent community project. It is not affiliated with or endorsed by SUPVAN or KATASYMBOL. Direct printing is based on public reverse-engineering work and needs testing on more physical printers.

## What works

- Millimetre-based label canvas with drag, resize, keyboard nudging, rotation, and numeric positioning.
- Text, Code 128-B barcodes, rectangles, lines, and embedded PNG/JPEG/BMP images.
- `.etikra` JSON project files and 300 DPI PNG export.
- A true empty-workspace startup: create from freshly read media, choose a custom size, or open an existing label without sample artwork or invented label metadata.
- Safe **Save mock PNG** output to `%LOCALAPPDATA%\Etikra\Mock Prints` without representing file output as physical hardware.
- Windows USB HID discovery restricted to SUPVAN vendor ID `1820`.
- Guarded direct USB backend for known T50, T80, G, TP76, TP80, TP86, and SP650 product IDs.
- Native Windows BLE discovery, persistent GATT notification transport, and E12 printing through `FEE7/FEC1`.
- A remembered-printer setup flow and one persistent E12 session with separate device-health and installed-media cards.
- Fresh media interrogation on connect/reconnect, foreground return, manual refresh, after printing, and immediately before every print. Media is never persisted or reused after disconnect.
- A loaded-media action plus a hard pre-print geometry recheck. Fixed stock sets both editor dimensions; continuous stock sets the tape width while preserving the user-selected length.
- A live 1-bit thermal-dot preview and dashed E12 print-safe guide. The feed-end inset is 1 mm; 15 mm tape is centered over the 12 mm head and therefore has a 1.5 mm tape-edge inset. Elements crossing the applicable boundary are blocked before any print bytes are sent.
- Printer status/error handling for cover-open, missing/empty labels, ribbon faults, thermal faults, and busy states.
- Version-2 `.etikra` files remember compatible media kind/geometry without binding to a cartridge RFID or serial; version-1 files remain readable.
- Dependency-free executable test harness for editor, persistence, session lifecycle, readiness, protocol, raster, checksum, model, and LZMA verification.

## Hardware status

| Connection | Etikra status | Notes |
|---|---|---|
| Mock/file output | Ready | Default and safe without hardware. |
| USB HID | Experimental | Protocol is implemented; this Windows port has not yet been validated on physical hardware in this repository. |
| Bluetooth Classic SPP | Researched, not implemented | Public work documents shared commands with different `7E 5A` framing. |
| Bluetooth LE GATT | Experimental, live E12 path | Discovery, configuration queries, raster framing, and printing are implemented. Live unit `T0188A2602242874` reported model `G15`, firmware `1`, and `8 dots/mm`. Verified loaded-media replies include `12 × 40 mm` die-cut stock with a `3 mm` gap and `15 × L` continuous tape. Its firmware can omit the final `BUF_FULL` echo, so Etikra verifies completion through status polling. |

Known USB product IDs and head profiles come from the [supvan-cups model registry](https://github.com/heeen/supvan-cups/blob/master/data/models.toml). Unknown PID values are displayed but blocked from printing so Etikra never guesses a raster width.

SUPVAN's E12 listing specifies a 12 mm print width, 12–15 mm label widths, and continuous labels with customizable length. Etikra therefore centers the 96-dot head raster on the printer-reported tape width instead of treating a 15 mm roll as a 120-dot head. See the [official E12 product specifications](https://global.supvan.com/en-ca/products/supvan-e12-bluetooth-label-maker-machine-with-4-tapes-support-keyboard-app-with-30-fonts-and-660-icons-rechargeable-inkless-labeler-for-office-home-kitchen-school-organization-white-5).

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

1. On first launch, use **Find label maker**, continue with a custom size, or open a saved label. Etikra quietly reconnects to the last successful printer on later launches.
2. Review the separate **Label maker** health and **Installed media** cards. Use the reported media to create/bind a blank label; continuous tape keeps a user-selected length.
3. Add elements from the left palette. Drag them on the canvas or edit exact values in the inspector.
4. Save the editable project as `.etikra`, export a 300 DPI PNG, or use **Save mock PNG** for safe 203 DPI output without hardware.
5. Review the readiness checklist before physical printing. E12 jobs re-read status and media on the same persistent connection immediately before raster transfer.
6. USB media interrogation remains unavailable. A supported experimental USB device can print only after an amber warning and explicit confirmation that the manual dimensions match loaded stock.

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
