using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryConnect.Protocols.MTConnect;
using FactoryConnect.Sql;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

internal static class ActivityBoundaryAcceptance
{
    internal static int PureChecks()
    {
        var count=0;var site=Guid.NewGuid();var machine=Guid.NewGuid();var mapping=Guid.NewGuid();var runtime=Guid.NewGuid();
        var ack=Guid.NewGuid();var time=new DateTimeOffset(2026,11,8,0,0,0,TimeSpan.Zero);
        var signal=new QualifiedActivitySignal(machine,"mtconnect:uuid:boundary",mapping,"execution",
            new Dictionary<string,ActivityState>{{"ACTIVE",ActivityState.Running},{"READY",ActivityState.Idle}});
        var approved=new ApprovedActivitySignal(site,Guid.NewGuid(),ack,new string('A',64),signal,time.AddMinutes(-1),null);
        (SqlAcceptedActivityEvidenceReader.AcceptedRow Row,ActivityBoundaryBatch Batch) Make(ulong from,ulong next,ulong sequence,string value,
            Guid? predecessor,ulong instance=7,params ulong[] additional)
        {
            var id=Guid.NewGuid();var elements=string.Join("",new[]{sequence}.Concat(additional).Select(s=>$"<Execution dataItemId=\"execution\" sequence=\"{s}\" timestamp=\"{time.AddSeconds((long)s):O}\">{value}</Execution>"));
            var raw=Encoding.UTF8.GetBytes($"<MTConnectStreams><Header instanceId=\"{instance}\" firstSequence=\"1\" lastSequence=\"{next-1}\" nextSequence=\"{next}\"/><Streams><DeviceStream uuid=\"boundary\"><ComponentStream><Events>{elements}</Events></ComponentStream></DeviceStream></Streams></MTConnectStreams>");
            var frame=MtConnectSampleParser.Parse(raw,"boundary",from);var source=frame.Observations.Single(x=>x.Sequence==sequence);var hash=Convert.ToHexString(SHA256.HashData(raw));
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="ingest-mtconnect",OperationId=id,SiteId=site,MachineId=machine,AcknowledgementId=ack,
                StreamKey=signal.StreamKey,ExpectedBatchId=predecessor,expectedInstance=predecessor is null?null:instance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                from=from.ToString(System.Globalization.CultureInfo.InvariantCulture),documentHash=hash});
            return (new(site,machine,signal.StreamKey,mapping,"execution",instance,sequence,id,ack,source.Timestamp,value,source.Quality,source.SourceTimestamp,source.ContentHash,hash),
                new(runtime,new(id,ack,instance,from,next,1,hash),raw,bytes,SHA256.HashData(bytes)));
        }
        var left=Make(100,102,100,"ACTIVE",null);var right=Make(102,104,103,"READY",left.Row.BatchId);
        AcceptedActivityBoundaryProof Assess(ActivityBoundaryBatch? following=null,SqlAcceptedActivityEvidenceReader.AcceptedRow? successor=null)
            =>AcceptedActivityBoundaryVerifier.Assess(approved,left.Row,successor??right.Row,left.Batch,following??right.Batch);
        void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);count++;Console.WriteLine("PASS: boundary "+label);}
        void Reject(Action action,string label){try{action();}catch(InvalidDataException){Check(true,label);return;}throw new InvalidOperationException(label);}
        ActivityBoundaryBatch Change(string property,JsonNode? value)
        {var node=JsonNode.Parse(right.Batch.Request)!;node[property]=value;var bytes=Encoding.UTF8.GetBytes(node.ToJsonString());return right.Batch with{Request=bytes,RequestHash=SHA256.HashData(bytes)};}
        Check(Assess().RetainedLink && right.Row.Sequence!=left.Row.Sequence+1,"exact ingestion ancestry qualifies nonadjacent signal sequences without an adjacency assumption");
        Check(!Assess(Change("ExpectedBatchId",JsonValue.Create(Guid.NewGuid()))).RetainedLink,"genuine-looking unrelated predecessor cannot join batches");
        Check(!Assess(Change("ExpectedBatchId",null)).RetainedLink,"bootstrap receipt cannot establish predecessor continuity");
        Check(!Assess(Change("expectedInstance",JsonValue.Create("8"))).RetainedLink,"different expected instance fences retained boundary");
        Reject(()=>Assess(Change("AcknowledgementId",JsonValue.Create(Guid.NewGuid()))),"checksum-consistent receipt acknowledgement mismatch rejects");
        Reject(()=>Assess(Change("documentHash",JsonValue.Create(new string('B',64)))),"receipt must bind exact source document hash");
        Reject(()=>Assess(right.Batch with{RequestHash=new byte[32]}),"changed receipt hash rejects");
        Reject(()=>Assess(right.Batch with{Request=Encoding.UTF8.GetBytes("{"),RequestHash=SHA256.HashData(Encoding.UTF8.GetBytes("{"))}),"malformed immutable receipt rejects");
        Reject(()=>Assess(successor:right.Row with{RawValue="ACTIVE"}),"endpoint value cannot disagree with original document");
        Reject(()=>Assess(successor:right.Row with{MappingId=Guid.NewGuid()}),"mapping identity mismatch rejects");
        var gap=Make(104,106,105,"READY",left.Row.BatchId);
        Check(!AcceptedActivityBoundaryVerifier.Assess(approved,left.Row,gap.Row,left.Batch,gap.Batch).RetainedLink,"header gap remains a break despite matching receipt predecessor");
        var overlap=Make(101,105,104,"READY",left.Row.BatchId);
        Check(!AcceptedActivityBoundaryVerifier.Assess(approved,left.Row,overlap.Row,left.Batch,overlap.Batch).RetainedLink,"overlapping response remains conservatively unqualified");
        var omitted=Make(102,105,104,"READY",left.Row.BatchId,7,103);
        Check(!AcceptedActivityBoundaryVerifier.Assess(approved,left.Row,omitted.Row,left.Batch,omitted.Batch).RetainedLink,"caller cannot select a later successor past an activity observation");
        var earlier=Make(100,102,100,"ACTIVE",null,7,101);var afterEarlier=Make(102,104,103,"READY",earlier.Row.BatchId);
        Check(!AcceptedActivityBoundaryVerifier.Assess(approved,earlier.Row,afterEarlier.Row,earlier.Batch,afterEarlier.Batch).RetainedLink,"caller cannot omit a later predecessor signal observation");
        var unknown=Make(102,104,103,"UNAVAILABLE",left.Row.BatchId);
        Check(!AcceptedActivityBoundaryVerifier.Assess(approved,left.Row,unknown.Row,left.Batch,unknown.Batch).RetainedLink,"unavailable endpoint never qualifies a state chain");
        var unmapped=Make(102,104,103,"MYSTERY",left.Row.BatchId);
        Check(!AcceptedActivityBoundaryVerifier.Assess(approved,left.Row,unmapped.Row,left.Batch,unmapped.Batch).RetainedLink,"unknown state value never qualifies a state chain");
        var changedInstance=Make(102,104,103,"READY",left.Row.BatchId,8);
        Check(!AcceptedActivityBoundaryVerifier.Assess(approved,left.Row,changedInstance.Row,left.Batch,changedInstance.Batch).RetainedLink,"source-instance change is explicit even with exact predecessor ID");
        Check(!Assess(right.Batch with{RuntimeIdentityId=Guid.NewGuid()}).RetainedLink,"runtime identity change remains a break");
        var cut=AcceptedActivityContinuityQualifier.Qualify(approved,[left.Row,right.Row]);
        Check(!cut.Samples[1].ContinuityFromPrevious && cut.UnprovenBoundaryLinks==1,"candidate proof does not enable cross-batch publication or change policy identity");
        return count;
    }

    internal static async Task<int> Run(InstallationTarget target,ActivityWorkerScope scope,
        SqlAcceptedActivityEvidenceReader.AcceptedRow prior,SqlAcceptedActivityEvidenceReader.AcceptedRow next)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(target.Database,"^FactoryConnect_V2_Test_[a-fA-F0-9]{32}$"))throw new InvalidOperationException("Disposable SQL required.");
        var count=0;var store=new SqlActivityProcessorStore(target.ConnectionString);
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        async Task<object?> Scalar(string sql,params (string,object)[] parameters)
        {await using var q=new SqlCommand(sql,c);foreach(var (name,value) in parameters)q.Parameters.AddWithValue(name,value);return await q.ExecuteScalarAsync();}
        async Task<string> Snapshot()=>JsonSerializer.Serialize(new{
            Checkpoint=await Scalar("SELECT CheckpointJson FROM activity.ProcessorCheckpoint WHERE MachineId=@machine AND MappingId=@mapping",("@machine",scope.MachineId),("@mapping",scope.MappingId)),
            Periods=await Scalar("SELECT COUNT(*) FROM activity.ClosedPeriod WHERE MachineId=@machine",("@machine",scope.MachineId)),
            Receipts=await Scalar("SELECT COUNT(*) FROM activity.ProcessorOperation WHERE MachineId=@machine",("@machine",scope.MachineId))});
        void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);count++;Console.WriteLine("PASS: boundary SQL "+label);}
        async Task Reject(Func<Task<AcceptedActivityBoundaryProof>> action,string label)
        {try{await action();}catch(InvalidDataException){Check(true,label);return;}throw new InvalidOperationException(label);}
        var baseline=await Snapshot();var proof=await store.AssessRetainedBoundaryAsync(scope,prior,next);
        Check(proof.RetainedLink && proof.Predecessor==prior && proof.Successor==next && proof.SuccessorRequestSha256.Length==64,
            "actual HTTP/mTLS accepted documents and immutable ingestion receipts prove exact direct ancestry");
        await Reject(()=>store.AssessRetainedBoundaryAsync(scope,prior,next with{RawValue="counterfeit"}),"altered caller endpoint rejects");
        await Reject(()=>store.AssessRetainedBoundaryAsync(scope,prior,next with{MappingId=Guid.NewGuid()}),"unrelated mapping rejects");
        var original=(byte[])(await Scalar("SELECT RequestBytes FROM security.SecurityOperation WHERE OperationId=@op",("@op",next.BatchId)))!;
        await Scalar("DISABLE TRIGGER security.TR_Operation_Immutable ON security.SecurityOperation");
        try
        {
            var node=JsonNode.Parse(original)!;node["ExpectedBatchId"]=Guid.NewGuid();var bytes=Encoding.UTF8.GetBytes(node.ToJsonString());
            await Scalar("UPDATE security.SecurityOperation SET RequestBytes=@bytes,RequestHash=HASHBYTES('SHA2_256',@bytes) WHERE OperationId=@op",("@bytes",bytes),("@op",next.BatchId));
            Check(!(await store.AssessRetainedBoundaryAsync(scope,prior,next)).RetainedLink,"checksum-consistent unrelated predecessor remains a break");
            bytes=Encoding.UTF8.GetBytes("{");
            await Scalar("UPDATE security.SecurityOperation SET RequestBytes=@bytes,RequestHash=HASHBYTES('SHA2_256',@bytes) WHERE OperationId=@op",("@bytes",bytes),("@op",next.BatchId));
            await Reject(()=>store.AssessRetainedBoundaryAsync(scope,prior,next),"malformed retained receipt fails closed");
        }
        finally
        {
            await Scalar("UPDATE security.SecurityOperation SET RequestBytes=@bytes,RequestHash=HASHBYTES('SHA2_256',@bytes) WHERE OperationId=@op",("@bytes",original),("@op",next.BatchId));
            await Scalar("ENABLE TRIGGER security.TR_Operation_Immutable ON security.SecurityOperation");
        }
        Check((await store.AssessRetainedBoundaryAsync(scope,prior,next)).RetainedLink,"restored original immutable receipt reproduces exact qualification");
        var address=(byte[])(await Scalar("SELECT AddressBytes FROM acquisition.MtConnectObservation WHERE MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND Sequence=@sequence",
            ("@machine",scope.MachineId),("@stream",Encoding.UTF8.GetBytes(scope.StreamKey)),("@instance",(decimal)scope.InstanceId),("@sequence",(decimal)next.Sequence)))!;
        await Scalar("DISABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
        try
        {
            await Scalar("UPDATE acquisition.MtConnectObservation SET AddressBytes=0x00 WHERE MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND Sequence=@sequence",
                ("@machine",scope.MachineId),("@stream",Encoding.UTF8.GetBytes(scope.StreamKey)),("@instance",(decimal)scope.InstanceId),("@sequence",(decimal)next.Sequence));
            await Reject(()=>store.AssessRetainedBoundaryAsync(scope,prior,next),"missing retained endpoint cannot be replaced by genuine documents and receipts");
        }
        finally
        {
            await Scalar("UPDATE acquisition.MtConnectObservation SET AddressBytes=@address WHERE MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND Sequence=@sequence",
                ("@address",address),("@machine",scope.MachineId),("@stream",Encoding.UTF8.GetBytes(scope.StreamKey)),("@instance",(decimal)scope.InstanceId),("@sequence",(decimal)next.Sequence));
            await Scalar("ENABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
        }
        Check(await Snapshot()==baseline,"all positive and negative assessment paths leave checkpoint periods and activity receipts unchanged");
        Directory.CreateDirectory("artifacts/ingestion");
        await File.WriteAllTextAsync("artifacts/ingestion/activity-boundary.json",JsonSerializer.Serialize(new{
            profile=AcceptedActivityBoundaryProof.Profile,proof,checks=count,
            unchangedActivityStateSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseline))),
            publicationPolicyUnchanged=true,physicalCompletenessEstablished=false},new JsonSerializerOptions{WriteIndented=true}));
        return count;
    }
}
