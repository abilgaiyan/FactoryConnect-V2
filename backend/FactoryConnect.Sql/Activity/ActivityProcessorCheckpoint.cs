using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Sql.ActivityProcessing;

/// <summary>
/// Immutable qualification identity for a checkpoint. The digest pins exact
/// mapping semantics in addition to machine/stream/address identities.
/// ContinuityIdentity comes from the trusted acquisition/continuity qualifier;
/// it is never derived from the difference between adjacent sequence numbers.
/// </summary>
public sealed record ActivityQualificationIdentity(
    Guid MachineId, string StreamKey, Guid MappingId, string SourceAddress,
    string ExactValueMapSha256, string ContinuityIdentity)
{
    public static ActivityQualificationIdentity From(
        QualifiedActivitySignal signal, string continuityIdentity)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if(string.IsNullOrWhiteSpace(continuityIdentity) ||
           signal.MachineId==Guid.Empty || signal.MappingId==Guid.Empty ||
           string.IsNullOrWhiteSpace(signal.StreamKey) ||
           string.IsNullOrWhiteSpace(signal.SourceAddress) ||
           signal.ExactValues is null || signal.ExactValues.Count==0)
            throw new ArgumentException("Explicit qualified mapping and continuity identities required.");
        var pairs=signal.ExactValues.OrderBy(x=>x.Key,StringComparer.Ordinal)
            .Select(x=>new { raw=x.Key, state=x.Value.ToString() }).ToArray();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(pairs);
        return new(signal.MachineId,signal.StreamKey,signal.MappingId,
            signal.SourceAddress,Convert.ToHexString(SHA256.HashData(bytes)),continuityIdentity);
    }
}

/// <summary>
/// This complete state is the payload to persist atomically with emitted periods,
/// not just a source sequence. The source sample carries exact machine/stream/mapping
/// identity and original value. The open anchor and its interpreted state survive
/// an empty batch or a worker restart.
/// </summary>
public sealed record ActivityAcceptedFrontier(
    Guid OperationId,ulong InstanceId,ulong ThroughSequence,Guid PackageRevisionId,
    Guid AcknowledgementId,string PackageSha256,
    SqlAcceptedActivityEvidenceReader.AcceptedRow? LastObservation,
    SqlAcceptedActivityEvidenceReader.AcceptedRow? OpenAnchorObservation,Guid SiteId=default,
    SqlAcceptedActivityEvidenceReader.BatchProof? FrontierBatch=null);

public sealed record ActivityProcessorCheckpoint(
    ActivityQualificationIdentity Qualification,
    ActivityInputSample? LastConsumedSample,
    ActivityInputSample? OpenAnchor,
    ActivityState? OpenState,
    ulong NextGeneration,ActivityAcceptedFrontier? AcceptedSource=null);

public sealed record ActivityProcessorBatch(
    ActivityProcessorCheckpoint Checkpoint,
    IReadOnlyList<ClosedActivityPeriod> ClosedPeriods,
    int UnqualifiedSamples, int ContinuityBreaks);

/// <summary>
/// Pure resume evaluator; this is not yet a SQL transaction or a claim of durable
/// processing. A Slice B writer must atomically store this entire checkpoint and
/// the periods, with optimistic generation and exact source identities.
/// </summary>
public static class ActivityCheckpointEvaluator
{
    public static ActivityProcessorCheckpoint Initial(ActivityQualificationIdentity qualification)
    {
        ArgumentNullException.ThrowIfNull(qualification);
        return new(qualification,null,null,null,0);
    }

    public static ActivityProcessorBatch Advance(
        QualifiedActivitySignal signal, ActivityQualificationIdentity identity,
        ActivityProcessorCheckpoint checkpoint, IReadOnlyList<ActivityInputSample> samples)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(samples);
        if(identity!=checkpoint.Qualification ||
           identity!=ActivityQualificationIdentity.From(signal,identity.ContinuityIdentity))
            throw new InvalidDataException("Activity qualification or continuity identity changed.");
        if((checkpoint.OpenAnchor is null)!=(checkpoint.OpenState is null) ||
           checkpoint.OpenAnchor is not null && checkpoint.LastConsumedSample is null)
            throw new InvalidDataException("Incomplete open activity checkpoint.");
        if(samples.Count>1000)throw new ArgumentException("Bounded activity batch required.");
        var rows=new List<ClosedActivityPeriod>();
        var previous=checkpoint.LastConsumedSample;
        var anchor=checkpoint.OpenAnchor;
        var state=checkpoint.OpenState;
        var unqualified=0;
        var gaps=0;
        var map=new Dictionary<string,ActivityState>(signal.ExactValues,StringComparer.Ordinal);
        if(map.Count!=signal.ExactValues.Count || map.Keys.Any(string.IsNullOrEmpty) ||
           map.Values.Any(x=>!Enum.IsDefined(x)))
            throw new ArgumentException("Invalid exact activity value map.");

        void CheckIdentity(ActivityInputSample sample)
        {
            if(sample.MachineId!=identity.MachineId ||
               !string.Equals(sample.StreamKey,identity.StreamKey,StringComparison.Ordinal) ||
               sample.MappingId!=identity.MappingId ||
               !string.Equals(sample.SourceAddress,identity.SourceAddress,StringComparison.Ordinal) ||
               sample.ObservedAt.Offset!=TimeSpan.Zero ||
               sample.Quality is not ("source-reported" or "unavailable"))
                throw new InvalidDataException("Sample violates qualified activity identity.");
        }
        if(previous is not null)CheckIdentity(previous);
        if(anchor is not null)
        {
            CheckIdentity(anchor);
            if(anchor.InstanceId!=previous!.InstanceId ||
               anchor.Sequence>previous.Sequence ||
               anchor.ObservedAt>previous.ObservedAt ||
               // A later consumed sample declaring a break cannot belong to
               // the anchor's still-open continuity chain, even when its
               // qualified state and timestamp otherwise look consistent.
               (anchor.Sequence!=previous.Sequence &&
                !previous.ContinuityFromPrevious) ||
               anchor.Quality!="source-reported" ||
               !map.TryGetValue(anchor.RawValue,out var interpreted) ||
               interpreted!=state ||
               previous.Quality!="source-reported" ||
               !map.TryGetValue(previous.RawValue,out var lastState) ||
               lastState!=state)
                throw new InvalidDataException("Checkpoint anchor, last sample and open state disagree.");
        }

        foreach(var sample in samples)
        {
            CheckIdentity(sample);
            if(previous is not null)
            {
                if(sample.InstanceId==previous.InstanceId &&
                   sample.Sequence<=previous.Sequence)
                    throw new InvalidDataException("Nonadvancing source sequence.");
                if(sample.ObservedAt<previous.ObservedAt)
                    throw new InvalidDataException("Source timestamp moved backwards.");
                if(sample.ObservedAt==previous.ObservedAt &&
                   sample.Quality=="source-reported" && previous.Quality=="source-reported" &&
                   map.TryGetValue(sample.RawValue,out var incoming) &&
                   map.TryGetValue(previous.RawValue,out var prior) && incoming!=prior)
                    throw new InvalidDataException("Contradictory qualified states at one instant.");
                if(sample.InstanceId!=previous.InstanceId || !sample.ContinuityFromPrevious)
                {
                    anchor=null;
                    state=null;
                    gaps++;
                }
            }
            if(sample.Quality!="source-reported" ||
               !map.TryGetValue(sample.RawValue,out var nextState))
            {
                unqualified++;
                anchor=null;
                state=null;
            }
            else if(anchor is null)
            {
                anchor=sample;
                state=nextState;
            }
            else if(state!=nextState)
            {
                var ticks=sample.ObservedAt.UtcTicks-anchor.ObservedAt.UtcTicks;
                if(ticks<=0)throw new InvalidDataException("Closed period must have positive duration.");
                rows.Add(new(identity.MachineId,identity.StreamKey,identity.MappingId,state!.Value,
                    anchor.ObservedAt,sample.ObservedAt,ticks,sample.InstanceId,
                    anchor.Sequence,sample.Sequence,anchor.RawValue,sample.RawValue));
                anchor=sample;
                state=nextState;
            }
            previous=sample;
        }
        var next=checkpoint with {
            LastConsumedSample=previous,OpenAnchor=anchor,OpenState=state,
            NextGeneration=checked(checkpoint.NextGeneration+(samples.Count>0?1UL:0UL))
        };
        return new(next,rows.AsReadOnly(),unqualified,gaps);
    }
}
