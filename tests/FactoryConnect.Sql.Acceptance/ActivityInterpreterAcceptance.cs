using FactoryConnect.Sql.ActivityProcessing;

internal static class ActivityInterpreterAcceptance
{
    internal static int PureChecks()
    {
        var count=0;
        var mapping=Guid.NewGuid();
        var machineId=Guid.NewGuid();
        const string streamKey="mtconnect:uuid:activity-fixture";
        var configured=new QualifiedActivitySignal(machineId,streamKey,mapping,"execution",
            new Dictionary<string,ActivityState>(StringComparer.Ordinal)
            {
                ["ACTIVE"]=ActivityState.Running,
                ["READY"]=ActivityState.Idle,
                ["STOPPED"]=ActivityState.Stopped,
            });
        var from=new DateTimeOffset(2026,11,7,10,0,0,TimeSpan.Zero);
        ActivityInputSample Sample(ulong sequence,int seconds,string value,
            bool continuous=true,ulong instance=7,string quality="source-reported",
            Guid? map=null,string address="execution")
            => new(machineId,streamKey,map??mapping,address,instance,sequence,from.AddSeconds(seconds),
                value,quality,continuous);
        void Check(bool condition,string label)
        {
            if(!condition)throw new InvalidOperationException("Activity interpretation: "+label);
            count++;
            Console.WriteLine("PASS: activity "+label);
        }
        void Reject(Action run,string label)
        {
            try{run();}catch(InvalidDataException){Check(true,label);return;}
            throw new InvalidOperationException("Activity interpretation did not reject "+label);
        }

        var transitions=MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(102,5,"ACTIVE"),Sample(108,10,"READY"),
             Sample(111,15,"STOPPED")]);
        Check(transitions.ClosedPeriods.Count==2 &&
            transitions.ClosedPeriods[0].State==ActivityState.Running &&
            transitions.ClosedPeriods[0].DurationTicks==TimeSpan.FromSeconds(10).Ticks &&
            transitions.ClosedPeriods[0].StartSequence==100 &&
            transitions.ClosedPeriods[0].EndSequence==108 &&
            transitions.ClosedPeriods[1].State==ActivityState.Idle,
            "qualified transition closes only known state-change intervals");

        Check(MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(101,15,"ACTIVE")]).ClosedPeriods.Count==0,
            "same state and open final interval do not invent closed periods");

        var gap=MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(105,20,"READY",false),
             Sample(109,30,"STOPPED")]);
        Check(gap.ClosedPeriods.Count==1 &&
            gap.ClosedPeriods[0].State==ActivityState.Idle &&
            gap.ClosedPeriods[0].StartedAt==from.AddSeconds(20) &&
            gap.ContinuityBreaks==1,
            "unproven continuity cannot bridge activity duration");

        var unknown=MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(101,5,"UNAVAILABLE",quality:"unavailable"),
             Sample(102,10,"READY"),Sample(103,20,"STOPPED")]);
        Check(unknown.UnqualifiedSamples==1 && unknown.ClosedPeriods.Count==1 &&
            unknown.ClosedPeriods[0].State==ActivityState.Idle,
            "unavailable source breaks interpretation without inventing stopped time");

        var reset=MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(1,10,"READY",instance:1),
             Sample(3,20,"STOPPED",instance:1)]);
        Check(reset.ContinuityBreaks==1 && reset.ClosedPeriods.Count==1 &&
            reset.ClosedPeriods[0].State==ActivityState.Idle,
            "instance change fences old state");

        var counters=new QualifiedActivitySignal(machineId,streamKey,mapping,"Counter",
            new Dictionary<string,ActivityState>(StringComparer.Ordinal)
            {["ACTIVE"]=ActivityState.Running});
        Check(MachineActivityInterpreter.Evaluate(counters,
            [Sample(100,0," 00042 ",address:"Counter"),
             Sample(101,5,"UNAVAILABLE",address:"Counter",quality:"unavailable")])
            .ClosedPeriods.Count==0,"counter values do not become activity");

        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(100,5,"READY")]),
            "duplicate source sequence rejected");
        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(101,-5,"READY")]),
            "backwards source time rejected");
        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(101,0,"READY")]),
            "zero-duration transition rejected");
        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(101,10,"ACTIVE"),Sample(102,10,"READY")]),
            "repeated ACTIVE then conflicting READY at identical timestamp rejected");
        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE"),Sample(101,10,"ACTIVE"),Sample(102,10,"READY"),Sample(103,15,"STOPPED")]),
            "conflicting timestamp cannot yield a misleading earlier running period");
        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE",map:Guid.NewGuid())]),
            "unapproved mapping identity rejected");
        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE",address:"Execution")]),
            "case-folded address cannot substitute exact identity");

        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE") with { MachineId=Guid.NewGuid() }]),
            "different machine cannot share state history");
        Reject(()=>MachineActivityInterpreter.Evaluate(configured,
            [Sample(100,0,"ACTIVE") with { StreamKey="mtconnect:uuid:other" }]),
            "different stream cannot share state history");
        var caseInsensitive=new QualifiedActivitySignal(machineId,streamKey,mapping,"execution",
            new Dictionary<string,ActivityState>(StringComparer.OrdinalIgnoreCase)
            {["ACTIVE"]=ActivityState.Running});
        Check(MachineActivityInterpreter.Evaluate(caseInsensitive,
            [Sample(100,0,"active"),Sample(101,5,"ACTIVE")]).UnqualifiedSamples==1,
            "case-insensitive caller mapping never changes exact raw-value interpretation");
        Check(MachineActivityInterpreter.Evaluate(configured,[]).ClosedPeriods.Count==0,
            "no retained evidence produces no invented activity");

        var identity=ActivityQualificationIdentity.From(configured,"qualified-continuity:fixture-revision-7");
        var initial=ActivityCheckpointEvaluator.Initial(identity);
        var fullSamples=new[] {Sample(100,0,"ACTIVE"),Sample(101,10,"ACTIVE"),
            Sample(102,20,"READY"),Sample(103,30,"STOPPED")};
        var single=ActivityCheckpointEvaluator.Advance(configured,identity,initial,fullSamples);
        var firstPart=ActivityCheckpointEvaluator.Advance(configured,identity,initial,fullSamples[..2]);
        Check(firstPart.ClosedPeriods.Count==0 && firstPart.Checkpoint.OpenAnchor==fullSamples[0] &&
            firstPart.Checkpoint.LastConsumedSample==fullSamples[1] &&
            firstPart.Checkpoint.OpenState==ActivityState.Running,
            "split checkpoint retains original anchor, latest sample and interpreted open state");
        var secondPart=ActivityCheckpointEvaluator.Advance(configured,identity,
            firstPart.Checkpoint,fullSamples[2..]);
        Check(single.ClosedPeriods.SequenceEqual(
            firstPart.ClosedPeriods.Concat(secondPart.ClosedPeriods)) &&
            single.Checkpoint.OpenAnchor==secondPart.Checkpoint.OpenAnchor &&
            single.Checkpoint.OpenState==secondPart.Checkpoint.OpenState &&
            single.Checkpoint.LastConsumedSample==secondPart.Checkpoint.LastConsumedSample,
            "single and split batches yield identical closed periods and open state");
        // A restart reloads the exact persisted state, not a new blank evaluator.
        var saved=System.Text.Json.JsonSerializer.Serialize(firstPart.Checkpoint);
        var restored=System.Text.Json.JsonSerializer.Deserialize<ActivityProcessorCheckpoint>(saved)!;
        var restarted=ActivityCheckpointEvaluator.Advance(configured,identity,restored,fullSamples[2..]);
        Check(secondPart.ClosedPeriods.SequenceEqual(restarted.ClosedPeriods) &&
            secondPart.Checkpoint==restarted.Checkpoint,
            "serialized checkpoint restart yields identical transitions without history traversal");
        var empty=ActivityCheckpointEvaluator.Advance(configured,identity,firstPart.Checkpoint,[]);
        Check(empty.Checkpoint==firstPart.Checkpoint && empty.ClosedPeriods.Count==0,
            "empty poll retains open checkpoint and generation exactly");
        var restartGap=ActivityCheckpointEvaluator.Advance(configured,identity,
            firstPart.Checkpoint,[Sample(102,20,"READY",continuous:false),
                Sample(103,30,"STOPPED")]);
        Check(restartGap.ClosedPeriods.Count==1 &&
            restartGap.ClosedPeriods[0].State==ActivityState.Idle &&
            restartGap.ContinuityBreaks==1,
            "restart continuity gap never bridges persisted open running interval");
        Reject(()=>ActivityCheckpointEvaluator.Advance(configured,
            identity with {ContinuityIdentity="different"},firstPart.Checkpoint,fullSamples[2..]),
            "changed continuity qualification cannot resume old checkpoint");
        Reject(()=>ActivityCheckpointEvaluator.Advance(configured,identity,
            firstPart.Checkpoint,[Sample(101,25,"READY")]),
            "replay of previously consumed sequence is rejected");
        Reject(()=>ActivityCheckpointEvaluator.Advance(configured,identity,
            firstPart.Checkpoint,[Sample(102,10,"READY")]),
            "equal-timestamp contradiction across batch restart is rejected");
        Check(single.Checkpoint.NextGeneration==1 && secondPart.Checkpoint.NextGeneration==2,
            "processor generation advances only for nonempty committed candidate batches");

        return count;
    }
}
