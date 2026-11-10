namespace FactoryConnect.Protocols.MTConnect;

/// <summary>
/// Single-device polling runner. The caller owns durable admission and checkpoint advancement.
/// A frame is never acknowledged merely because HTTP acquisition succeeded.
/// </summary>
public sealed class MtConnectPollingLoop(MtConnectSampleClient client)
{
    public async Task RunAsync(
        MtConnectEndpoint endpoint,
        string deviceUuid,
        Func<CancellationToken, Task<ulong>> readCommittedNextSequence,
        Func<MtConnectSampleFrame, CancellationToken, Task> commitFrame,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUuid);
        ArgumentNullException.ThrowIfNull(readCommittedNextSequence);
        ArgumentNullException.ThrowIfNull(commitFrame);
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(interval));
        // No overlapping acquisition or admission; failure propagates to the supervising host.
        // The host must restart only after determining whether the failure is recoverable.
        while (!cancellationToken.IsCancellationRequested)
        {
            var from = await readCommittedNextSequence(cancellationToken);
            var frame = await client.AcquireAsync(endpoint, deviceUuid, from, ct: cancellationToken);
            if (frame.NextSequence < from)
                throw new InvalidDataException("MTConnect source cursor regressed.");
            await commitFrame(frame, cancellationToken);
            // Never advance from in-memory state: a restart or failed commit must replay
            // from the durable cursor supplied by the caller.
            await Task.Delay(interval, cancellationToken);
        }
    }
}
