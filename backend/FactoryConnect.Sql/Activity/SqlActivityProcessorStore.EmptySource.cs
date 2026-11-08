using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.ActivityProcessing;

public sealed partial class SqlActivityProcessorStore
{
    private static async Task VerifyEmptySourceInTransactionAsync(SqlConnection c,SqlTransaction t,
        ActivityQualificationIdentity identity,AcceptedSourceBinding binding,CancellationToken ct)
    {
        if(binding.Rows.Count!=0 || binding.EmptyBatches is not {Count:>0 and <=100})
            throw new InvalidDataException("Exact verified no-state batch proof required.");
        await using var q=new SqlCommand("""
            IF NOT EXISTS(SELECT 1 FROM acquisition.MtConnectCursor WITH(HOLDLOCK)
                WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND NextSequence>@through)
                THROW 51093,'No-state frontier is not durably accepted.',1;
            DECLARE @proof TABLE(BatchId uniqueidentifier PRIMARY KEY,Ack uniqueidentifier,Instance decimal(20,0),
                FromSequence decimal(20,0),NextSequence decimal(20,0),FirstSequence decimal(20,0),Hash binary(32));
            INSERT @proof SELECT BatchId,Ack,Instance,FromSequence,NextSequence,FirstSequence,CONVERT(binary(32),Hash,2)
                FROM OPENJSON(@proofJson) WITH(BatchId uniqueidentifier '$.BatchId',Ack uniqueidentifier '$.AcknowledgementId',
                    Instance decimal(20,0) '$.InstanceId',FromSequence decimal(20,0) '$.FromSequence',
                    NextSequence decimal(20,0) '$.NextSequence',FirstSequence decimal(20,0) '$.FirstAvailableSequence',Hash varchar(64) '$.DocumentSha256');
            DECLARE @actual TABLE(BatchId uniqueidentifier PRIMARY KEY);
            INSERT @actual SELECT TOP(101) BatchId FROM acquisition.MtConnectBatch WITH(HOLDLOCK,INDEX(IX_MtConnectBatch_ActivityWindow),FORCESEEK)
                WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance
                    AND FromSequence<=@through AND NextSequence>@after+1;
            IF (SELECT COUNT(*) FROM @actual)<>@count OR (SELECT COUNT(*) FROM @proof)<>@count
                OR EXISTS(SELECT BatchId FROM @actual EXCEPT SELECT BatchId FROM @proof)
                OR EXISTS(SELECT BatchId FROM @proof EXCEPT SELECT BatchId FROM @actual)
                THROW 51093,'No-state source cut changed.',1;
            IF EXISTS(SELECT 1 FROM @proof p LEFT LOOP JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK,FORCESEEK) ON b.BatchId=p.BatchId
                WHERE b.BatchId IS NULL OR b.SiteId<>@site OR b.MachineId<>@machine OR b.StreamKeyBytes<>@stream OR
                    b.InstanceId<>@instance OR p.Instance<>@instance OR b.AcknowledgementId<>@ack OR p.Ack<>@ack OR
                    b.FromSequence<>p.FromSequence OR b.NextSequence<>p.NextSequence OR b.FirstAvailableSequence<>p.FirstSequence OR
                    b.DocumentHash<>p.Hash OR HASHBYTES('SHA2_256',b.RawDocument)<>p.Hash)
                THROW 51093,'No-state document proof differs from retained evidence.',1;
            IF EXISTS(SELECT 1 FROM acquisition.MtConnectObservation WITH(HOLDLOCK)
                WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance
                    AND Sequence>@after AND Sequence<=@through AND (MappingId=@mapping OR AddressBytes=@address))
                THROW 51093,'A no-state cut cannot omit activity observations.',1;
            """,c,t);
        BindScope(q,identity);q.Parameters.AddWithValue("@site",binding.SiteId);
        WorkerNumber(q,"@instance",binding.InstanceId);WorkerNumber(q,"@after",binding.AfterSequence);WorkerNumber(q,"@through",binding.ThroughSequence);
        q.Parameters.AddWithValue("@ack",binding.Approved.AcknowledgementId);
        q.Parameters.Add("@address",SqlDbType.VarBinary,512).Value=Utf8.GetBytes(identity.SourceAddress);
        q.Parameters.AddWithValue("@count",binding.EmptyBatches.Count);
        q.Parameters.Add("@proofJson",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(binding.EmptyBatches);
        await q.ExecuteNonQueryAsync(ct);
    }
    private static async Task VerifyFrontierBatchAsync(SqlConnection c,SqlTransaction t,ActivityQualificationIdentity identity,
        Guid site,ActivityAcceptedFrontier frontier,SqlAcceptedActivityEvidenceReader.BatchProof batch,CancellationToken ct)
    {
        if(frontier.SiteId!=site || batch.InstanceId!=frontier.InstanceId || batch.AcknowledgementId!=frontier.AcknowledgementId ||
            frontier.ThroughSequence==ulong.MaxValue || batch.NextSequence!=frontier.ThroughSequence+1)
            throw new InvalidDataException("No-state checkpoint frontier witness differs from its scope.");
        await using var q=new SqlCommand("""
            SELECT 1 FROM acquisition.MtConnectBatch WITH(HOLDLOCK)
            WHERE BatchId=@batch AND SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream
                AND InstanceId=@instance AND AcknowledgementId=@ack AND FromSequence=@from AND NextSequence=@next AND
                FirstAvailableSequence=@first AND DocumentHash=@hash AND HASHBYTES('SHA2_256',RawDocument)=@hash;
            """,c,t);
        BindScope(q,identity);q.Parameters.AddWithValue("@site",site);q.Parameters.AddWithValue("@batch",batch.BatchId);q.Parameters.AddWithValue("@ack",batch.AcknowledgementId);
        WorkerNumber(q,"@instance",batch.InstanceId);WorkerNumber(q,"@from",batch.FromSequence);WorkerNumber(q,"@next",batch.NextSequence);WorkerNumber(q,"@first",batch.FirstAvailableSequence);
        q.Parameters.Add("@hash",SqlDbType.Binary,32).Value=Convert.FromHexString(batch.DocumentSha256);
        if(await q.ExecuteScalarAsync(ct) is null)throw new InvalidDataException("No-state checkpoint frontier document is missing or altered.");
    }
}
