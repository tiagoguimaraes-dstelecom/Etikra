using System.IO;

namespace Etikra.Printing;

internal sealed class SupvanUsbProtocol : IAsyncDisposable
{
    private const byte CommandBufferFull = 0x10;
    private const byte CommandInquiryStatus = 0x11;
    private const byte CommandCheckDevice = 0x12;
    private const byte CommandStartPrint = 0x13;
    private const byte CommandStopPrint = 0x14;
    private const byte CommandNextZippedBulk = 0x5C;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);

    private readonly WindowsHidStream _hid;

    public SupvanUsbProtocol(string path)
    {
        _hid = WindowsHidStream.Open(path);
        if (_hid.MaximumPayloadLength < 64)
        {
            throw new IOException($"The HID interface exposes {_hid.MaximumPayloadLength}-byte writes; this protocol requires 64.");
        }
    }

    public async Task PrintAsync(SupvanPrintData data, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Checking printer…");
        await SendCommandAsync(CommandCheckDevice, 0, null, cancellationToken);
        await WaitForAsync(status => !status.DeviceBusy && !status.Printing, 60, "ready", cancellationToken);

        progress?.Report("Starting print…");
        await SendCommandAsync(CommandStartPrint, 0, null, cancellationToken);
        await WaitForAsync(status => status.Printing, 60, "printing station", cancellationToken);
        await WaitForAsync(status => !status.BufferFull, 200, "buffer space", cancellationToken, 20);

        try
        {
            progress?.Report($"Sending {data.Compressed.Length:N0} bytes…");
            await SendCommandAsync(CommandNextZippedBulk, checked((ushort)data.Compressed.Length), null, cancellationToken);
            foreach (var chunk in data.Compressed.Chunk(64))
            {
                await _hid.WriteAsync(chunk, cancellationToken);
                if (chunk.Length == 64)
                {
                    await Task.Delay(1, cancellationToken);
                }
            }

            await Task.Delay(20, cancellationToken);
            await SendCommandAsync(CommandBufferFull, checked((ushort)data.Compressed.Length), data.Speed, cancellationToken);
            progress?.Report("Printing…");
            await WaitForAsync(status => !status.DeviceBusy && !status.Printing, 300, "print completion", cancellationToken);
        }
        catch
        {
            try
            {
                await SendCommandAsync(CommandStopPrint, 0, null, CancellationToken.None);
            }
            catch
            {
                // Preserve the original print failure.
            }

            throw;
        }
    }

    private async Task<SupvanStatus> QueryStatusAsync(CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(CommandInquiryStatus, 0, null, cancellationToken);
        if (response.Length < 7)
        {
            throw new IOException($"Printer returned a {response.Length}-byte status response; expected at least 7.");
        }

        return SupvanStatus.Parse(response);
    }

    private async Task WaitForAsync(
        Func<SupvanStatus, bool> predicate,
        int attempts,
        string state,
        CancellationToken cancellationToken,
        int delayMs = 100)
    {
        for (var i = 0; i < attempts; i++)
        {
            var status = await QueryStatusAsync(cancellationToken);
            status.ThrowIfError();
            if (predicate(status))
            {
                return;
            }

            await Task.Delay(delayMs, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for printer {state}.");
    }

    private async Task<byte[]> SendCommandAsync(byte command, ushort first, ushort? second, CancellationToken cancellationToken)
    {
        byte[] frame;
        if (second is null)
        {
            frame = [0xC0, 0x40, (byte)(first >> 8), (byte)first, command, 0, 8, 0];
        }
        else
        {
            frame = [0xC0, 0x40, (byte)(first >> 8), (byte)first, command, 0, 8, 0, (byte)(second.Value >> 8), (byte)second.Value];
        }

        return await _hid.ExchangeAsync(frame, ResponseTimeout, cancellationToken)
            ?? throw new TimeoutException($"Printer did not acknowledge command 0x{command:X2}.");
    }

    public ValueTask DisposeAsync() => _hid.DisposeAsync();

    private sealed record SupvanStatus(
        bool BufferFull,
        bool DeviceBusy,
        bool Printing,
        bool LowBattery,
        IReadOnlyList<string> Errors)
    {
        public static SupvanStatus Parse(ReadOnlySpan<byte> response)
        {
            var mstaLow = response[1];
            var mstaHigh = response[2];
            var fstaLow = response[3];
            var fstaHigh = response[4];
            var errors = new List<string>();
            if ((mstaLow & 0x02) != 0) errors.Add("label read/write error");
            if ((mstaLow & 0x04) != 0) errors.Add("label roll empty");
            if ((mstaLow & 0x08) != 0) errors.Add("label mode mismatch");
            if ((mstaLow & 0x10) != 0) errors.Add("ribbon read/write error");
            if ((mstaLow & 0x20) != 0) errors.Add("ribbon empty");
            if ((mstaHigh & 0x08) != 0) errors.Add("printhead temperature too high");
            if ((fstaLow & 0x08) != 0) errors.Add("cover open");
            if ((fstaHigh & 0x01) != 0) errors.Add("label not installed");
            return new SupvanStatus(
                (mstaLow & 0x01) != 0,
                (mstaHigh & 0x04) != 0,
                (fstaLow & 0x40) != 0,
                (mstaLow & 0x40) != 0,
                errors);
        }

        public void ThrowIfError()
        {
            if (Errors.Count > 0)
            {
                throw new InvalidOperationException("Printer error: " + string.Join(", ", Errors));
            }
        }
    }
}
