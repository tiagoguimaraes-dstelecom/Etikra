using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Etikra.Printing;

internal sealed class WindowsHidStream : IAsyncDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly int _inputLength;
    private readonly int _outputLength;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int MaximumPayloadLength => _outputLength - 1;

    private WindowsHidStream(SafeFileHandle handle, int inputLength, int outputLength)
    {
        _handle = handle;
        _inputLength = Math.Max(2, inputLength);
        _outputLength = Math.Max(2, outputLength);
        _stream = new FileStream(handle, FileAccess.ReadWrite, Math.Max(_inputLength, _outputLength), true);
    }

    public static WindowsHidStream Open(string devicePath)
    {
        var handle = CreateFile(devicePath, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the printer HID interface.");
        }

        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect the printer HID interface.");
        }

        try
        {
            var status = HidP_GetCaps(preparsed, out var caps);
            if (status < 0)
            {
                handle.Dispose();
                throw new IOException($"HidP_GetCaps failed with status 0x{status:X8}.");
            }

            return new WindowsHidStream(handle, caps.InputReportByteLength, caps.OutputReportByteLength);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > _outputLength - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"HID payload is {payload.Length} bytes; the device accepts {_outputLength - 1}.");
        }

        var report = new byte[_outputLength]; // report ID 0 at index 0
        payload.CopyTo(report.AsMemory(1));
        await _stream.WriteAsync(report, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async Task<byte[]?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var report = new byte[_inputLength];
        try
        {
            var read = 0;
            while (read < report.Length)
            {
                var count = await _stream.ReadAsync(report.AsMemory(read), timeoutSource.Token);
                if (count == 0)
                {
                    break;
                }

                read += count;
                if (read == report.Length)
                {
                    break;
                }
            }

            return read <= 1 ? null : report.AsSpan(1, read - 1).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<byte[]?> ExchangeAsync(ReadOnlyMemory<byte> payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(payload, cancellationToken);
            return await ReadAsync(timeout, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _handle.Dispose();
        _gate.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps caps);
}
