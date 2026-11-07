using System.Data;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Protocols.MTConnect;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;
namespace FactoryConnect.Sql.Security;

public sealed record MtConnectBatchEnvelope(Guid OperationId,Guid SiteId,Guid MachineId,Guid AcknowledgementId,string StreamKey,Guid? ExpectedBatchId,ulong? ExpectedInstanceId,ulong FromSequence);
public sealed record MtConnectIngestionRequest(Guid OperationId,Guid SiteId,Guid MachineId,Guid AcknowledgementId,string StreamKey,Guid? ExpectedBatchId,ulong? ExpectedInstanceId,ulong FromSequence,byte[] RawDocument);
public sealed record MtConnectCursor(Guid BatchId,ulong InstanceId,ulong NextSequence,ulong BaselineSequence);
public sealed record MtConnectAcquisitionContext(Guid AcknowledgementId,string DeviceUuid,string StreamKey,MtConnectCursor? Cursor);
public sealed record MtConnectAcceptedObservation(string Sequence,string InstanceId,Guid MappingId,string Address,string RawValue,string Quality,string SourceTimestamp,
    DateTimeOffset ObservedAt,DateTimeOffset ReceivedAt,Guid BatchId,Guid AcknowledgementId,string DocumentHash);

public sealed partial class SqlSecurityAuthority
{
    private static readonly UTF8Encoding AcquisitionUtf8=new(false,true);
    private sealed record IngestionAuthority(Guid Ack,Guid Activation,long Generation,Guid Revision,byte[] Hash,Guid Review,DateTimeOffset From,DateTimeOffset? Until,byte[] Canonical,DateTimeOffset AppliedFrom,DateTimeOffset? AppliedUntil);
    public async Task<byte[]> ReceiveMtConnectAsync(RuntimeMtlsChannel channel,MtConnectBatchEnvelope envelope,CancellationToken ct=default)
    {
        _=channel.Revalidate(ct);var prefix=new byte[4];await ReadExactly(prefix);var length=BinaryPrimitives.ReadInt32BigEndian(prefix);
        if(length is <1 or >MtConnectSampleParser.MaximumDocumentBytes)throw new InvalidDataException("Runtime MTConnect frame length exceeds profile bound.");
        var bytes=new byte[length];await ReadExactly(bytes);
        return await IngestMtConnectAsync(channel,new(envelope.OperationId,envelope.SiteId,envelope.MachineId,envelope.AcknowledgementId,envelope.StreamKey,envelope.ExpectedBatchId,envelope.ExpectedInstanceId,envelope.FromSequence,bytes),ct);
        async Task ReadExactly(Memory<byte> buffer){while(buffer.Length>0){var read=await channel.ReadAsync(buffer,ct);if(read==0)throw new EndOfStreamException("Incomplete runtime observation frame.");buffer=buffer[read..];}}
    }
    public Task<MtConnectAcquisitionContext> ReadMtConnectAcquisitionContextAsync(RuntimeMtlsChannel channel,Guid siteId,Guid machineId,string streamKey,CancellationToken ct=default)
    {
        var(stream,device)=MtConnectStream(streamKey);var evidence=channel.Revalidate(ct);
        return Transaction(null,false,async(c,t)=>
        {
            Activity.Current?.SetTag("security.acquisition.context",true);await Lock(c,t,ConfigurationLockResources.Machine(installationId,machineId),false,ct);
            var state=await State(c,t,ct);var runtime=await ValidateRuntimeMachineAuthority(c,t,evidence,siteId,machineId,state.Now,ct);
            var pin=await LoadIngestionAuthority(c,t,siteId,machineId,null,runtime,state.Now,ct);
            using var document=JsonDocument.Parse(pin.Canonical);var machine=IngestionMachine(document.RootElement,machineId,streamKey);
            await VerifyIngestionAuthority(c,t,pin,runtime,document.RootElement,machine,state.Now,ct);
            var cursor=await LoadMtConnectCursor(c,t,siteId,machineId,stream,ct);
            return new MtConnectAcquisitionContext(pin.Ack,device,streamKey,cursor);
        },ct);
    }
    public Task<byte[]> IngestMtConnectAsync(RuntimeMtlsChannel channel,MtConnectIngestionRequest request,CancellationToken ct=default)
    {
        if(new[]{request.OperationId,request.SiteId,request.MachineId,request.AcknowledgementId}.Any(x=>x==Guid.Empty)||request.ExpectedBatchId==Guid.Empty||(request.ExpectedBatchId is null)!=(request.ExpectedInstanceId is null))throw new ArgumentException("Exact ingestion operation, scope, acknowledgement and cursor identities required.");
        var(stream,device)=MtConnectStream(request.StreamKey);var raw=request.RawDocument.ToArray();var frame=MtConnectSampleParser.Parse(raw,device,request.FromSequence);var evidence=channel.Revalidate(ct);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="ingest-mtconnect",request.SiteId,request.MachineId,request.AcknowledgementId,request.StreamKey,request.ExpectedBatchId,expectedInstance=request.ExpectedInstanceId?.ToString(CultureInfo.InvariantCulture),from=request.FromSequence.ToString(CultureInfo.InvariantCulture),documentHash=Convert.ToHexString(SHA256.HashData(raw))});
        return Transaction(request.OperationId,false,async(c,t)=>
        {
            Activity.Current?.SetTag("security.acquisition.ingest",true);
            var initial=await State(c,t,ct);var runtime=await ValidateRuntimeMachineAuthority(c,t,evidence,request.SiteId,request.MachineId,initial.Now,ct);
            var pin=await LoadIngestionAuthority(c,t,request.SiteId,request.MachineId,request.AcknowledgementId,runtime,initial.Now,ct);
            using var document=JsonDocument.Parse(pin.Canonical);var root=document.RootElement;var machine=IngestionMachine(root,request.MachineId,request.StreamKey);
            await ConfigurationLocks(c,t,root,installationId,ct,request.MachineId);
            var final=await State(c,t,ct);runtime=await ValidateRuntimeMachineAuthority(c,t,evidence,request.SiteId,request.MachineId,final.Now,ct);
            await VerifyIngestionAuthority(c,t,pin,runtime,root,machine,final.Now,ct);
            // Current authority must still permit the exact acknowledged configuration even for saved-result retrieval.
            var done=await FindOperation(c,t,request.OperationId,ct);
            if(done is not null)
            {
                if(done.Actor!=runtime.RuntimeIdentityId||done.Scope!=AuthorizationScope.Machine(request.SiteId,request.MachineId)||!done.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();
                await using var retained=Command(c,t,"SELECT RawDocument FROM acquisition.MtConnectBatch WHERE BatchId=@op AND RuntimeIdentityId=@runtime AND SiteId=@site AND MachineId=@machine");Add(retained,("@op",request.OperationId),("@runtime",runtime.RuntimeIdentityId),("@site",request.SiteId),("@machine",request.MachineId));
                if(await retained.ExecuteScalarAsync(ct) is not byte[] original||!original.AsSpan().SequenceEqual(raw))throw new SecurityConflictException();return done.Result;
            }
            var cursor=await LoadMtConnectCursor(c,t,request.SiteId,request.MachineId,stream,ct);
            if(cursor is null)
            {if(request.ExpectedBatchId is not null||request.ExpectedInstanceId is not null||request.FromSequence!=frame.FirstSequence)throw new SecurityConflictException();}
            else if(cursor.BatchId!=request.ExpectedBatchId||cursor.InstanceId!=request.ExpectedInstanceId||cursor.InstanceId!=frame.InstanceId||request.FromSequence>cursor.NextSequence||request.FromSequence<cursor.BaselineSequence||frame.NextSequence<cursor.NextSequence)throw new SecurityConflictException();
            if(frame.NextSequence==request.FromSequence&&frame.Observations.Count==0)throw new InvalidDataException("Caught-up response requires no ingestion operation.");
            var mappings=machine.GetProperty("signals").EnumerateArray().Where(x=>x.GetProperty("protocol").GetString()=="mtconnect"&&x.GetProperty("streamKey").GetString()==request.StreamKey)
                .ToDictionary(x=>Convert.ToHexString(x.GetProperty("addressBytes").GetBytesFromBase64()),x=>x.GetProperty("mappingId").GetGuid(),StringComparer.Ordinal);
            var mapped=frame.Observations.Where(x=>mappings.ContainsKey(Convert.ToHexString(AcquisitionUtf8.GetBytes(x.DataItemId)))).ToArray();
            var rows=JsonSerializer.Serialize(mapped.Select(x=>new{sequence=x.Sequence.ToString(CultureInfo.InvariantCulture),mapping=mappings[Convert.ToHexString(AcquisitionUtf8.GetBytes(x.DataItemId))],address=Convert.ToHexString(AcquisitionUtf8.GetBytes(x.DataItemId)),value=x.RawValue,timestamp=x.SourceTimestamp,observed=x.Timestamp,quality=x.Quality,canonical=x.CanonicalJson,hash=x.ContentHash}));
            if(AcquisitionUtf8.GetByteCount(rows)>33554432)throw new InvalidDataException("SQL acquisition batch encoding bound exceeded.");
            // One set-based comparison: exact overlap may deduplicate; different source content never overwrites history.
            await using(var check=Command(c,t,"""
                IF EXISTS(SELECT 1 FROM OPENJSON(@rows) WITH(Sequence decimal(20,0) '$.sequence',CanonicalJson nvarchar(max) '$.canonical') r
                    JOIN acquisition.MtConnectObservation existing ON existing.MachineId=@machine AND existing.StreamKeyBytes=@stream AND existing.InstanceId=@instance AND existing.Sequence=r.Sequence
                    WHERE existing.CanonicalBytes<>CONVERT(varbinary(max),CONVERT(varchar(max),r.CanonicalJson))) THROW 51043,'Conflicting source observation identity.',1;
                """))
            {IngestionArgs(check,request.SiteId,request.MachineId,stream,frame.InstanceId);check.Parameters.Add("@rows",SqlDbType.NVarChar,-1).Value=rows;try{await check.ExecuteNonQueryAsync(ct);}catch(SqlException error)when(error.Number==51043){throw new SecurityConflictException();}}
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,batchId=request.OperationId,instanceId=frame.InstanceId.ToString(CultureInfo.InvariantCulture),nextSequence=frame.NextSequence.ToString(CultureInfo.InvariantCulture),mappedObservations=mapped.Length,status="accepted-retained-window-not-physical-completeness"});
            await Exec(c,t,"INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes) VALUES(@op,@runtime,@bytes,@hash,'M',@site,@machine,@result)",ct,
                ("@op",request.OperationId),("@runtime",runtime.RuntimeIdentityId),("@bytes",bytes),("@hash",SHA256.HashData(bytes)),("@site",request.SiteId),("@machine",request.MachineId),("@result",result));
            await using(var q=Command(c,t,"""
                INSERT acquisition.MtConnectBatch(BatchId,SiteId,MachineId,StreamKeyBytes,AcknowledgementId,RuntimeIdentityId,InstanceId,FromSequence,NextSequence,FirstAvailableSequence,RawDocument,DocumentHash,MappedCount,ReceivedAt)
                    VALUES(@op,@site,@machine,@stream,@ack,@runtime,@instance,@from,@next,@first,@raw,@documentHash,@count,@now);
                INSERT acquisition.MtConnectObservation(SiteId,MachineId,StreamKeyBytes,InstanceId,Sequence,BatchId,MappingId,AddressBytes,RawValue,SourceTimestamp,ObservedAt,Quality,CanonicalBytes,ContentHash)
                    SELECT @site,@machine,@stream,@instance,r.Sequence,@op,r.Mapping,CONVERT(varbinary(512),r.Address,2),r.Value,r.Timestamp,r.Observed,r.Quality,CONVERT(varbinary(max),CONVERT(varchar(max),r.CanonicalJson)),CONVERT(binary(32),r.Hash,2)
                    FROM OPENJSON(@rows) WITH(Sequence decimal(20,0) '$.sequence',Mapping uniqueidentifier '$.mapping',Address varchar(1024) '$.address',Value nvarchar(max) '$.value',Timestamp nvarchar(128) '$.timestamp',Observed datetimeoffset(7) '$.observed',Quality varchar(32) '$.quality',CanonicalJson nvarchar(max) '$.canonical',Hash varchar(64) '$.hash') r
                    WHERE NOT EXISTS(SELECT 1 FROM acquisition.MtConnectObservation existing WHERE existing.MachineId=@machine AND existing.StreamKeyBytes=@stream AND existing.InstanceId=@instance AND existing.Sequence=r.Sequence);
                UPDATE acquisition.MtConnectCursor SET BatchId=@op,NextSequence=@next WHERE MachineId=@machine AND StreamKeyBytes=@stream;
                IF @@ROWCOUNT=0 INSERT acquisition.MtConnectCursor VALUES(@site,@machine,@stream,@instance,@next,@from,@op);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(NEWID(),@op,@runtime,'acquisition-mtconnect-batch',N'Atomic retained-window evidence and cursor; not physical completeness');
                """))
            {
                IngestionArgs(q,request.SiteId,request.MachineId,stream,frame.InstanceId);q.Parameters.Add("@op",SqlDbType.UniqueIdentifier).Value=request.OperationId;q.Parameters.Add("@ack",SqlDbType.UniqueIdentifier).Value=pin.Ack;q.Parameters.Add("@runtime",SqlDbType.UniqueIdentifier).Value=runtime.RuntimeIdentityId;
                SequenceParameter(q,"@from",request.FromSequence);SequenceParameter(q,"@next",frame.NextSequence);SequenceParameter(q,"@first",frame.FirstSequence);
                q.Parameters.Add("@raw",SqlDbType.VarBinary,-1).Value=raw;q.Parameters.Add("@documentHash",SqlDbType.Binary,32).Value=SHA256.HashData(raw);q.Parameters.Add("@count",SqlDbType.Int).Value=mapped.Length;q.Parameters.Add("@now",SqlDbType.DateTimeOffset).Value=final.Now;q.Parameters.Add("@rows",SqlDbType.NVarChar,-1).Value=rows;
                await q.ExecuteNonQueryAsync(ct);
            }
            Activity.Current?.SetTag("security.acquisition.observations",mapped.Length);return result;
        },ct);
    }
    public Task<IReadOnlyList<MtConnectAcceptedObservation>> ReadMtConnectEvidenceAsync(byte[] sessionHash,Guid siteId,Guid machineId,string streamKey,int take=20,CancellationToken ct=default)
    {
        if(take is <1 or >100)throw new ArgumentOutOfRangeException(nameof(take));var(stream,_)=MtConnectStream(streamKey);var token=sessionHash.ToArray();var scope=AuthorizationScope.Machine(siteId,machineId);
        return Transaction(null,false,async(c,t)=>
        {
            Activity.Current?.SetTag("security.acquisition.read",true);await Lock(c,t,ConfigurationLockResources.Machine(installationId,machineId),false,ct);
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);await Authorize(c,t,actor,"commissioning.read",scope,state.Now,ct);
            await using var q=Command(c,t,"""
                IF NOT EXISTS(SELECT 1 FROM [master].Machine WHERE SiteId=@site AND MachineId=@machine) THROW 51042,'Unknown evidence machine.',1;
                DECLARE @selected TABLE(InstanceId decimal(20,0),Sequence decimal(20,0),PRIMARY KEY(InstanceId,Sequence));
                INSERT @selected SELECT TOP(@take) InstanceId,Sequence FROM acquisition.MtConnectObservation WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream ORDER BY InstanceId DESC,Sequence DESC;
                IF (SELECT SUM(CONVERT(bigint,DATALENGTH(o.RawValue))+DATALENGTH(o.SourceTimestamp)+DATALENGTH(o.AddressBytes)+256)
                    FROM acquisition.MtConnectObservation o JOIN @selected selected ON selected.InstanceId=o.InstanceId AND selected.Sequence=o.Sequence
                    WHERE o.MachineId=@machine AND o.StreamKeyBytes=@stream)>8388608 THROW 51044,'Evidence page exceeds byte budget; reduce requested row count.',1;
                SELECT o.Sequence,o.InstanceId,o.MappingId,o.AddressBytes,o.RawValue,o.Quality,o.SourceTimestamp,o.ObservedAt,b.ReceivedAt,o.BatchId,b.AcknowledgementId,b.DocumentHash
                FROM acquisition.MtConnectObservation o JOIN @selected selected ON selected.InstanceId=o.InstanceId AND selected.Sequence=o.Sequence JOIN acquisition.MtConnectBatch b ON b.BatchId=o.BatchId
                WHERE o.SiteId=@site AND o.MachineId=@machine AND o.StreamKeyBytes=@stream ORDER BY o.InstanceId DESC,o.Sequence DESC;
                """);IngestionArgs(q,siteId,machineId,stream,null);q.Parameters.Add("@take",SqlDbType.Int).Value=take;var rows=new List<MtConnectAcceptedObservation>();
            await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))rows.Add(new(decimal.ToUInt64(r.GetDecimal(0)).ToString(CultureInfo.InvariantCulture),decimal.ToUInt64(r.GetDecimal(1)).ToString(CultureInfo.InvariantCulture),r.GetGuid(2),AcquisitionUtf8.GetString((byte[])r[3]),r.GetString(4),r.GetString(5),r.GetString(6),r.GetDateTimeOffset(7),r.GetDateTimeOffset(8),r.GetGuid(9),r.GetGuid(10),Convert.ToHexString((byte[])r[11]).ToLowerInvariant()));
            return (IReadOnlyList<MtConnectAcceptedObservation>)rows.AsReadOnly();
        },ct);
    }
    private static (byte[] Stream,string Device) MtConnectStream(string stream)
    {
        const string prefix="mtconnect:uuid:";if(!stream.StartsWith(prefix,StringComparison.Ordinal)||stream.Length==prefix.Length)throw new ArgumentException("Exact mtconnect:uuid: device stream profile required.");
        var bytes=AcquisitionUtf8.GetBytes(stream);var device=stream[prefix.Length..];if(bytes.Length>512||AcquisitionUtf8.GetByteCount(device)>256)throw new ArgumentException("MTConnect stream profile bound exceeded.");return(bytes,device);
    }
    private static JsonElement IngestionMachine(JsonElement root,Guid machineId,string stream)
    {
        var matches=root.GetProperty("machines").EnumerateArray().Where(x=>x.GetProperty("machineId").GetGuid()==machineId).ToArray();if(matches.Length!=1)throw new SecurityDeniedException();
        var signals=matches[0].GetProperty("signals").EnumerateArray().Where(x=>x.GetProperty("protocol").GetString()=="mtconnect"&&x.GetProperty("streamKey").GetString()==stream).ToArray();
        if(signals.Length==0||signals.Any(x=>x.GetProperty("addressEncoding").GetString()!="utf-8"||x.GetProperty("normalization").GetString()!="identity-v1"||x.GetProperty("addressBytes").GetBytesFromBase64().Length is <1 or >512)||signals.Select(x=>Convert.ToHexString(x.GetProperty("addressBytes").GetBytesFromBase64())).Distinct(StringComparer.Ordinal).Count()!=signals.Length)throw new SecurityDeniedException();return matches[0];
    }
    private static async Task<IngestionAuthority> LoadIngestionAuthority(SqlConnection c,SqlTransaction t,Guid site,Guid machine,Guid? expected,RuntimeMachineAuthority runtime,DateTimeOffset now,CancellationToken ct)
    {
        await using var q=Command(c,t,"""
            SELECT a.AcknowledgementId,a.ActivationId,a.ActivationGeneration,a.PackageRevisionId,a.ContentHash,a.ReviewId,r.EffectiveFrom,r.EffectiveTo,p.CanonicalBytes,a.AppliedFrom,a.EffectiveTo
            FROM configuration.MachineAcknowledgementHead h JOIN configuration.RuntimeActivationAcknowledgement a ON a.AcknowledgementId=h.AcknowledgementId AND a.ActivationGeneration=h.ActivationGeneration
            JOIN configuration.ActivationRequest r ON r.ActivationId=a.ActivationId
            JOIN configuration.ConfigurationPackageRevision p ON p.PackageRevisionId=a.PackageRevisionId AND p.ContentHash=a.ContentHash AND p.SiteId=a.SiteId
            JOIN configuration.PackageReview v ON v.ReviewId=a.ReviewId AND v.Decision='approved' AND v.ContentHash=p.ContentHash AND v.PackageRevisionId=p.PackageRevisionId
            WHERE h.SiteId=@site AND h.MachineId=@machine AND (@ack IS NULL OR a.AcknowledgementId=@ack) AND p.SubmittedAt IS NOT NULL
                AND a.RuntimeIdentityId=@runtime AND a.AssignmentId=@assignment AND a.AssignmentGeneration=@assignmentGeneration AND a.CredentialGeneration=@credentialGeneration AND a.OperationalGeneration=@operationalGeneration
                AND a.AppliedFrom<=@now AND (a.EffectiveTo IS NULL OR a.EffectiveTo>@now) AND r.EffectiveFrom<=@now AND (r.EffectiveTo IS NULL OR r.EffectiveTo>@now)
                AND NOT EXISTS(SELECT 1 FROM configuration.ActivationRequestRevocation WHERE ActivationId=a.ActivationId);
            """);Add(q,("@site",site),("@machine",machine),("@runtime",runtime.RuntimeIdentityId),("@assignment",runtime.AssignmentId),("@assignmentGeneration",runtime.AssignmentGeneration),("@credentialGeneration",runtime.CredentialGeneration),("@operationalGeneration",runtime.OperationalGeneration),("@now",now));q.Parameters.Add("@ack",SqlDbType.UniqueIdentifier).Value=(object?)expected??DBNull.Value;
        await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new SecurityDeniedException();var canonical=(byte[])r[8];var hash=(byte[])r[4];if(canonical.Length>1048576||!CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(canonical)))throw new SecurityDeniedException();
        return new(r.GetGuid(0),r.GetGuid(1),r.GetInt64(2),r.GetGuid(3),hash,r.GetGuid(5),r.GetDateTimeOffset(6),r.IsDBNull(7)?null:r.GetDateTimeOffset(7),canonical,r.GetDateTimeOffset(9),r.IsDBNull(10)?null:r.GetDateTimeOffset(10));
    }
    private static async Task VerifyIngestionAuthority(SqlConnection c,SqlTransaction t,IngestionAuthority pin,RuntimeMachineAuthority runtime,JsonElement root,JsonElement machine,DateTimeOffset now,CancellationToken ct)
    {
        if(pin.From>now||pin.Until is {} ended&&ended<=now||pin.AppliedFrom>now||pin.AppliedUntil is {} appliedEnded&&appliedEnded<=now)throw new SecurityDeniedException();
        if(root.GetProperty("siteId").GetGuid()!=runtime.SiteId||root.GetProperty("revisionId").GetGuid()!=pin.Revision||!Contains(machine.GetProperty("effectiveFrom").GetDateTimeOffset(),OptionalInstant(machine,"effectiveTo"),pin.From,pin.Until))throw new SecurityDeniedException();
        await ValidateActivationPrerequisites(c,t,new(Guid.NewGuid(),pin.Activation,runtime.SiteId,runtime.MachineId,pin.Revision,pin.Hash,pin.Review,runtime.RuntimeIdentityId,runtime.AssignmentId,runtime.AssignmentGeneration,runtime.CredentialGeneration,pin.From,pin.Until,0,null,0,"ingestion authority"),machine,pin.From,pin.Until,now,ct);
        await ValidatePinnedPolicies(c,t,root,machine,runtime.SiteId,now,ct);
        await using(var q=Command(c,t,"""
            SELECT p.TimezoneRevisionId,p.TimezoneHash,a.ScheduleRevisionId,a.ScheduleHash,a.BreakPolicyRevisionId,a.BreakPolicyHash,a.ProductionLineId,a.EffectiveFrom,a.EffectiveTo
            FROM configuration.ConfigurationPackageRevision p JOIN configuration.PackageMachineAssignment a ON a.PackageRevisionId=p.PackageRevisionId
            WHERE p.PackageRevisionId=@revision AND a.MachineId=@machine AND a.SiteId=@site;
            SELECT MappingId,SemanticKey,Protocol,StreamKey,AddressEncoding,AddressBytes,Normalization FROM configuration.PackageSignalMapping WHERE PackageRevisionId=@revision AND MachineId=@machine;
            """))
        {
            Add(q,("@revision",pin.Revision),("@machine",runtime.MachineId),("@site",runtime.SiteId));await using var r=await q.ExecuteReaderAsync(ct);
            static bool PolicyMatches(JsonElement policy,Guid id,byte[] hash)=>policy.GetProperty("revisionId").GetGuid()==id&&CryptographicOperations.FixedTimeEquals(PinHash(policy),hash);
            if(!await r.ReadAsync(ct)||!PolicyMatches(root.GetProperty("timezone"),r.GetGuid(0),(byte[])r[1])||!PolicyMatches(machine.GetProperty("schedule"),r.GetGuid(2),(byte[])r[3])||!PolicyMatches(machine.GetProperty("breakPolicy"),r.GetGuid(4),(byte[])r[5])||machine.GetProperty("productionLineId").GetGuid()!=r.GetGuid(6)||machine.GetProperty("effectiveFrom").GetDateTimeOffset()!=r.GetDateTimeOffset(7)||OptionalInstant(machine,"effectiveTo")!=(r.IsDBNull(8)?null:r.GetDateTimeOffset(8)))throw new SecurityDeniedException();
            var expected=machine.GetProperty("signals").EnumerateArray().ToDictionary(x=>x.GetProperty("mappingId").GetGuid());await r.NextResultAsync(ct);var count=0;
            while(await r.ReadAsync(ct)){if(!expected.TryGetValue(r.GetGuid(0),out var signal)||signal.GetProperty("semanticKey").GetString()!=r.GetString(1)||signal.GetProperty("protocol").GetString()!=r.GetString(2)||signal.GetProperty("streamKey").GetString()!=r.GetString(3)||signal.GetProperty("addressEncoding").GetString()!=r.GetString(4)||!signal.GetProperty("addressBytes").GetBytesFromBase64().AsSpan().SequenceEqual((byte[])r[5])||signal.GetProperty("normalization").GetString()!=r.GetString(6))throw new SecurityDeniedException();count++;}
            if(count!=expected.Count)throw new SecurityDeniedException();
        }

    }
    private static async Task<MtConnectCursor?> LoadMtConnectCursor(SqlConnection c,SqlTransaction t,Guid site,Guid machine,byte[] stream,CancellationToken ct)
    {
        await using var q=Command(c,t,"""
            SELECT BatchId,InstanceId,NextSequence,BaselineSequence FROM acquisition.MtConnectCursor WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream;
            """);IngestionArgs(q,site,machine,stream,null);await using var r=await q.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct))return new(r.GetGuid(0),decimal.ToUInt64(r.GetDecimal(1)),decimal.ToUInt64(r.GetDecimal(2)),decimal.ToUInt64(r.GetDecimal(3)));await r.DisposeAsync();
        await using var retained=Command(c,t,"SELECT 1 FROM acquisition.MtConnectBatch WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream");IngestionArgs(retained,site,machine,stream,null);if(await retained.ExecuteScalarAsync(ct) is not null)throw new SecurityDeniedException();return null;
    }
    private static void IngestionArgs(SqlCommand q,Guid site,Guid machine,byte[] stream,ulong? instance)
    {q.Parameters.Add("@site",SqlDbType.UniqueIdentifier).Value=site;q.Parameters.Add("@machine",SqlDbType.UniqueIdentifier).Value=machine;q.Parameters.Add("@stream",SqlDbType.VarBinary,512).Value=stream;if(instance is {} value)SequenceParameter(q,"@instance",value);}
    private static void SequenceParameter(SqlCommand q,string name,ulong value){var p=q.Parameters.Add(name,SqlDbType.Decimal);p.Precision=20;p.Scale=0;p.Value=(decimal)value;}
}
