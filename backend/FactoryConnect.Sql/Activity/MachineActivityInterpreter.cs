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
        // The single-cut API and the durable checkpoint candidate intentionally
        // share one transition/contradiction evaluator. An adapter must provide
        // externally qualified continuity before invoking either path.
        var identity=ActivityQualificationIdentity.From(signal,"explicit-single-cut");
        var result=ActivityCheckpointEvaluator.Advance(signal,identity,
            ActivityCheckpointEvaluator.Initial(identity),samples);
        return new(result.ClosedPeriods,result.UnqualifiedSamples,result.ContinuityBreaks,
            "retained-source-interpretation-not-physical-completeness");
    }
}
