# SUPVAN / KATASYMBOL protocol notes

This document records the portion of the protocol Etikra implements. It is an independent interoperability effort, not vendor documentation.

## Evidence and confidence

The primary reference is the MIT-licensed [`heeen/supvan-cups`](https://github.com/heeen/supvan-cups) repository, particularly its [`PROTOCOL.md`](https://github.com/heeen/supvan-cups/blob/master/docs/PROTOCOL.md), [`usb_transport.rs`](https://github.com/heeen/supvan-cups/blob/master/crates/supvan-proto/src/usb_transport.rs), [`buffer.rs`](https://github.com/heeen/supvan-cups/blob/master/crates/supvan-proto/src/buffer.rs), and [`printer.rs`](https://github.com/heeen/supvan-cups/blob/master/crates/supvan-proto/src/printer.rs). That project reports vendor Android/Electron app analysis plus live T50/T50M hardware captures.

Etikra ports the documented USB behavior to the Windows HID API. Its pure transformation code has automated checks, but this Windows transport port has not yet been exercised against a physical printer in this repository. Treat direct USB as experimental until model-specific reports arrive.

## USB transport

Discovery enumerates the Windows HID device-interface class and keeps only paths containing `VID_1820`. A known PID selects DPI and printhead width; unknown PIDs cannot print.

Windows HID includes a leading report-ID byte. Etikra writes report ID `0` followed by a zero-padded protocol payload and strips the report ID from input reports.

Standard command payload (8 bytes):

```text
offset  size  value
0       1     C0
1       1     40
2       2     parameter 1, big-endian
4       1     command
5       1     00
6       1     08
7       1     00
```

Two-parameter commands append parameter 2 as a big-endian 16-bit value at offsets 8–9.

Implemented commands:

| Name | Code | Use |
|---|---:|---|
| `BUF_FULL` | `10` | Commit the compressed-byte count and calculated speed. |
| `INQUIRY_STA` | `11` | Poll busy, printing, buffer, and error flags. |
| `CHECK_DEVICE` | `12` | Confirm the device responds. |
| `START_PRINT` | `13` | Enter the print state. |
| `STOP_PRINT` | `14` | Best-effort abort after a transfer failure. |
| `NEXT_ZIPPEDBULK` | `5C` | Announce compressed byte length before raw 64-byte HID reports. |

The print state machine is:

```text
CHECK_DEVICE
  → poll INQUIRY_STA until idle
  → START_PRINT
  → poll until printing
  → poll until buffer is available
  → NEXT_ZIPPEDBULK(compressed length)
  → raw compressed data in 64-byte HID payloads
  → 20 ms settle
  → BUF_FULL(compressed length, speed)
  → poll until idle
```

Etikra aborts on any decoded device error and times out every wait loop. It never sends firmware-update opcodes.

## Status response

After Windows removes the HID report ID, the USB response uses:

```text
[0] response type/length
[1] MSTA low
[2] MSTA high
[3] FSTA low
[4] FSTA high
[5..6] print count, little-endian
[7] reserved
```

The active error/busy bits match the upstream status decoder. See `Printing/SupvanUsbProtocol.cs` for the exact mapping.

## Raster conversion

Etikra renders the physical label at the selected model's native DPI, thresholds it to a one-bit MSB-first image, then converts each feed-direction row to the printer's LSB-first bit order. The label is centred within the printhead canvas; over-wide media is centre-cropped to the physical head.

The canvas is split into fixed 4096-byte buffers. Each buffer contains:

```text
offset  size  meaning
0       2     checksum, little-endian
2       2     page-register flags and material/density bits
4       2     column count, little-endian
6       1     bytes per printhead row
7       1     reserved
8       2     top margin (8 dots)
10      2     bottom margin (8 dots)
12      1     density, 0–15
13      1     reserved
14      ...   image bytes (maximum 4074)
```

The checksum is the sum of header bytes 2–13 plus the byte immediately before every 256-byte boundary through the used image region, truncated to 16 bits. First/last-page flags and the buffer checksum have focused local tests.

## Compression and speed

All page buffers are concatenated and compressed as one LZMA1-alone stream using:

```text
dictionary = 8192 bytes
lc = 3
lp = 0
pb = 2
nice/fast bytes = 128
properties byte = 5D
header size field = exact uncompressed byte count
```

Etikra uses the MIT/public-domain [`LZMA-SDK` NuGet package](https://www.nuget.org/packages/LZMA-SDK/22.1.1), which packages Igor Pavlov's public-domain SDK. The test harness decodes the produced stream again and checks the header values. Pages whose compressed size exceeds the protocol's 16-bit length field are rejected before connecting.

Speed is derived from average compressed bytes per 4096-byte buffer: `10` above 3000 bytes, then `15`, `20`, `25`, `40`, `45`, `55`, and `60` at decreasing thresholds matching the public reference implementation.

## Model registry

Etikra currently records these known USB families:

- T50: T50M, T50M Pro, T50M Plus, T50s, T50s Pro — 203 DPI, 384 dots.
- T80: T80M, T80M Pro — 201 DPI, 568 dots.
- G: G11, G15, G18, G18 Pro — 193 DPI, 190 nominal dots.
- TP76 — 305 DPI, 912 dots.
- TP80 — 305 DPI, 960 dots.
- TP86 — 305 DPI, 1032 dots.
- SP650 — 203 DPI, 384 dots.

The exact PID map is in `Printing/PrinterDevice.cs` and is derived from the upstream [`models.toml`](https://github.com/heeen/supvan-cups/blob/master/data/models.toml).

## Bluetooth LE transport

The public T-series research describes Bluetooth Classic SPP commands with `7E 5A` framing, 16-byte commands, little-endian parameters, and framed 512-byte data packets. It also records several BLE GATT service/characteristic patterns used by E-series hardware. [`katasymbol-e12-lab`](https://github.com/eteriall/katasymbol-e12-lab) provides an additional independent E12 reference.

Etikra implements native Windows BLE advertisement scanning, a persistent GATT connection, notifications, command/reply correlation, and raster transfer. A live E12-class hardware probe confirmed service `FEE7`, characteristic `FEC1`, and `WriteWithoutResponse, Notify`. Windows negotiated an ATT MTU of 251. The scanner also recognizes the observed SUPVAN `A4:93:40` OUI plus `T`/`G`/`D` serial-style advertisement names.

BLE commands use a 16-byte `7E 5A` request with little-endian parameters and a checksum over bytes 10–15. Replies echo the command at offset 7. Etikra enables notifications before its first command and supports the three published service paths: `FEE7/FEC1`, `E0FF/(FFE1 notify, FFE9 write)`, and `FF00/(FF01 notify, FF02 write)`.

### Live E12 configuration query

Etikra sends read-only `CHECK_DEVICE (12)`, `INQUIRY_STA (11)`, `RD_DEV_NAME (16)`, `READ_REV (17)`, `RD_LAB_DPI (22)`, `RETURN_MAT (30)`, and `READ_FWVER (C5)` queries. On the available unit, those replies established:

```text
Bluetooth name       T0188A2602242874
protocol model       G15
firmware byte        01
resolution field     0320 little-endian = 800 hundredths = 8 dots/mm
material type        1
material width       12 mm
material height      40 mm
material gap         3 mm
label SN             25004
```

The common material payload starts at response byte 22: RFID UID (7 bytes), RFID code (8), label SN (u16), material type (u8), width/height/gap (three u8 values), and a four-byte firmware counter. Geometry is accepted only inside conservative ranges and is queried again immediately before a print. A die-cut design must match returned width and height within 0.1 mm. Continuous media (height zero) requires a width match while document height remains the requested feed length.

The four-byte field after the gap is called `remaining` by public implementations. It changed from 204 to 0 during a single live transfer, which is not a credible remaining-label decrement, so Etikra exposes it only as an unverified firmware counter and never uses it for safety decisions.

### BLE raster transfer

The E12 path renders at the returned 8 dots/mm (203.2 dpi) and uses the verified 96-dot head width. The printer-returned material type populates the two-bit material field in every 4096-byte print buffer; Etikra does not hard-code that field for Bluetooth jobs.

Compressed data is split into 500-byte payloads inside checksummed 506-byte packets, then wrapped as 512-byte `7E 5A` frames. Each frame is fragmented into conservative 180-byte GATT writes. The state machine is:

```text
CHECK_DEVICE → idle status → START_PRINT → printing/buffer-ready status
→ NEXT_ZIPPEDBULK(block_size=512, frame_count)
→ 512-byte data frames
→ BUF_FULL(compressed length, speed)
→ status polling until idle
```

The live firmware accepted the raster transfer but omitted the `BUF_FULL (10)` echo. Since that opcode is also documented as flow control/output-only, Etikra treats only that missing echo as optional and then uses `INQUIRY_STA` as the completion authority. Any other timeout or decoded printer error triggers a best-effort `STOP_PRINT`.

Firmware update and RFID-write opcodes remain deliberately absent.
