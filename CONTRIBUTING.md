# Contributing to Etikra

Thank you for helping make closed label-printer hardware more useful.

## Development

1. Install the .NET 10 SDK on Windows.
2. Run `dotnet build Etikra.slnx`.
3. Run `dotnet run --project Tests\Etikra.Tests.csproj`.
4. Use **Save mock PNG** for editor work. Do not require physical hardware for unrelated changes or represent file output as a connected printer.

Keep protocol parsing, raster transformation, transport I/O, and WPF UI concerns separate. Add a deterministic test fixture for every newly decoded field or packet rule.

## Hardware reports

Include:

- Brand and exact model printed on the device.
- USB VID/PID and HID input/output report lengths, or Bluetooth advertisement name and services.
- Label width, height, gap, and media type.
- Connection type, firmware version if known, and the result.
- A redacted capture when legally obtained; remove device serials and other identifiers.

Never test firmware-flashing commands as part of an Etikra contribution. Do not upload vendor binaries, decompiled vendor source, personal RFID identifiers, or copyrighted symbol libraries.

## Pull requests

- Keep unknown devices blocked by default.
- Preserve the mock backend and its no-hardware workflow.
- Explain evidence and confidence for protocol changes.
- Run the build and all checks before submitting.
