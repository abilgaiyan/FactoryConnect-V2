namespace FactoryConnect.Sql.ActivityProcessing;

/// <summary>
/// Domain-only interpretation of explicitly qualified state evidence. This type does
/// not query SQL, approve signal meaning or assert physical continuity.
/// </summary>
public enum ActivityState { Running, Idle, Stopped, Fault, Unknown }

public sealed record QualifiedActivitySignal(
    Guid MachineId, string StreamKey, Guid MappingId, string SourceAddress,
    IReadOnlyDictionary<string,ActivityState> ExactValues);

/// <summary>ContinuityFromPrevious must be independently established by a durable
/// source-cut/retention qualifier. It is never inferred from neighbouring sequences.</summary>
public sealed record ActivityInputSample(
    Guid MachineId, string StreamKey, Guid MappingId, string SourceAddress,
    ulong InstanceId, ulong Sequence,
    DateTimeOffset ObservedAt, string RawValue, string Quality,
    bool ContinuityFromPrevious);

public sealed record ClosedActivityPeriod(
    Guid MachineId, string StreamKey, Guid MappingId, ActivityState State, DateTimeOffset StartedAt,
    DateTimeOffset EndedAt, long DurationTicks, ulong InstanceId,
    ulong StartSequence, ulong EndSequence, string StartRawValue,
    string EndRawValue);

public sealed record ActivityInterpretation(
    IReadOnlyList<ClosedActivityPeriod> ClosedPeriods, int UnqualifiedSamples,
    int ContinuityBreaks, string Assurance);

/// <summary>
/// Process a bounded forward-only source cut. A period is created only when two
/// different qualified states are linked by explicitly established continuity.
/// Neither state is extrapolated before the first sample or after the last one.
/// </summary>
public static class MachineActivityInterpreter
{
    public static ActivityInterpretation Evaluate(
        QualifiedActivitySignal signal, IReadOnlyList<ActivityInputSample> samples)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(samples);
        if(signal.MachineId==Guid.Empty || string.IsNullOrWhiteSpace(signal.StreamKey) ||
           signal.MappingId==Guid.Empty || string.IsNullOrWhiteSpace(signal.SourceAddress)
           || signal.ExactValues is null || signal.ExactValues.Count==0
           || samples.Count>1000)
            throw new ArgumentException("Exact qualified signal and bounded samples are required.");
        if(signal.ExactValues.Keys.Any(string.IsNullOrEmpty))
            throw new ArgumentException("State-value mappings may not be empty.");

        // Do not depend on a caller-supplied case-insensitive comparer.
        var exactValues=new Dictionary<string,ActivityState>(signal.ExactValues,StringComparer.Ordinal);
        if(exactValues.Count!=signal.ExactValues.Count || exactValues.Values.Any(v=>!Enum.IsDefined(v)))
            throw new ArgumentException("Qualified state mapping must be unambiguous.");
        var periods=new List<ClosedActivityPeriod>();
        ActivityInputSample? previous=null;
        ActivityInputSample? anchor=null;
        ActivityState? activeState=null;
        var breaks=0;
        var unqualified=0;
        foreach(var sample in samples)
        {
            if(sample.MachineId!=signal.MachineId ||
               !string.Equals(sample.StreamKey,signal.StreamKey,StringComparison.Ordinal) ||
               sample.MappingId!=signal.MappingId ||
               !string.Equals(sample.SourceAddress,signal.SourceAddress,StringComparison.Ordinal))
                throw new InvalidDataException("Observation is not from the qualified exact signal.");
            if(sample.ObservedAt.Offset!=TimeSpan.Zero ||
               sample.Quality is not ("source-reported" or "unavailable"))
                throw new InvalidDataException("Source evidence requires a UTC instant and explicit quality.");
            if(previous is not null)
            {
                if(sample.InstanceId<previous.InstanceId ||
                   sample.InstanceId==previous.InstanceId && sample.Sequence<=previous.Sequence)
                    throw new InvalidDataException("Out-of-order or repeated source identity.");
                if(sample.ObservedAt<previous.ObservedAt)
                    throw new InvalidDataException("Source observation time moved backwards.");
                if(sample.InstanceId!=previous.InstanceId || !sample.ContinuityFromPrevious)
                {
                    anchor=null;
                    activeState=null;
                    breaks++;
                }
            }
            if(sample.Quality=="unavailable" ||
               !exactValues.TryGetValue(sample.RawValue,out var interpreted))
            {
                unqualified++;
                anchor=null;
                activeState=null;
            }
            else if(anchor is null)
            {
                anchor=sample;
                activeState=interpreted;
            }
            else if(activeState!=interpreted)
            {
                var elapsed=sample.ObservedAt.UtcTicks-anchor.ObservedAt.UtcTicks;
                if(elapsed<=0)
                    throw new InvalidDataException("A closed period requires positive elapsed source time.");
                periods.Add(new(signal.MachineId,signal.StreamKey,signal.MappingId,activeState!.Value,
                    anchor.ObservedAt,sample.ObservedAt,elapsed,sample.InstanceId,
                    anchor.Sequence,sample.Sequence,anchor.RawValue,sample.RawValue));
                anchor=sample;
                activeState=interpreted;
            }
            previous=sample;
        }
        return new(periods.AsReadOnly(),unqualified,breaks,
            "retained-source-interpretation-not-physical-completeness");
    }
}
