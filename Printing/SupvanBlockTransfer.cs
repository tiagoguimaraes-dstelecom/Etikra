namespace Etikra.Printing;

internal readonly record struct SupvanTransferStatus(bool Printing, bool BufferFull);

internal interface ISupvanBlockTransferChannel
{
    bool BufferCommitAcknowledgementOptional { get; }

    Task<SupvanTransferStatus> ReadTransferStatusAsync(CancellationToken cancellationToken);

    Task AnnounceBlockAsync(
        SupvanCompressedBlock block,
        int blockIndex,
        int blockCount,
        CancellationToken cancellationToken);

    Task WriteBlockAsync(
        SupvanCompressedBlock block,
        int blockIndex,
        int blockCount,
        CancellationToken cancellationToken);

    Task CommitBlockAsync(
        SupvanCompressedBlock block,
        ushort speed,
        int blockIndex,
        int blockCount,
        CancellationToken cancellationToken);
}

internal static class SupvanBlockTransfer
{
    private const int BufferReadyAttempts = 200;
    private const int BufferReadyDelayMilliseconds = 20;
    private const int BufferSettleDelayMilliseconds = 20;

    public static async Task TransferAsync(
        SupvanPrintData data,
        ISupvanBlockTransferChannel channel,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        int bufferReadyAttempts = BufferReadyAttempts,
        int bufferReadyDelayMilliseconds = BufferReadyDelayMilliseconds,
        int bufferSettleDelayMilliseconds = BufferSettleDelayMilliseconds)
    {
        if (data.Blocks.Count == 0)
        {
            throw new InvalidOperationException("The print job contains no raster buffers.");
        }

        if (bufferReadyAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferReadyAttempts));
        }

        for (var index = 0; index < data.Blocks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForBufferReadyAsync(
                channel,
                index,
                data.Blocks.Count,
                bufferReadyAttempts,
                bufferReadyDelayMilliseconds,
                cancellationToken);

            var block = data.Blocks[index];
            progress?.Report(
                $"Sending buffer {index + 1} of {data.Blocks.Count} ({block.Length:N0} compressed bytes)…");
            await channel.AnnounceBlockAsync(block, index, data.Blocks.Count, cancellationToken);
            await channel.WriteBlockAsync(block, index, data.Blocks.Count, cancellationToken);
            if (bufferSettleDelayMilliseconds > 0)
            {
                await Task.Delay(bufferSettleDelayMilliseconds, cancellationToken);
            }

            try
            {
                await channel.CommitBlockAsync(block, data.Speed, index, data.Blocks.Count, cancellationToken);
            }
            catch (TimeoutException) when (channel.BufferCommitAcknowledgementOptional)
            {
                progress?.Report(
                    $"Printer omitted the optional acknowledgement for buffer {index + 1} of {data.Blocks.Count}; continuing from live status…");
            }
        }
    }

    private static async Task WaitForBufferReadyAsync(
        ISupvanBlockTransferChannel channel,
        int blockIndex,
        int blockCount,
        int attempts,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await channel.ReadTransferStatusAsync(cancellationToken);
            if (!status.Printing)
            {
                throw new InvalidOperationException(
                    $"Printer left the printing state before buffer {blockIndex + 1} of {blockCount} could be sent.");
            }

            if (!status.BufferFull)
            {
                return;
            }

            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"Timed out waiting for printer buffer space before buffer {blockIndex + 1} of {blockCount}.");
    }
}
