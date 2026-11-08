using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.ActivityProcessing;

/// <summary>
/// Protected, bounded retrieval of retained accepted source observations.
/// This is an evidence verification primitive, not mapping approval, continuity
/// qualification or a production activity worker. In particular, callers may
/// not infer ContinuityFromPrevious from adjacent sequence numbers.
/// </summary>
public sealed class SqlAcceptedActivityEvidenceReader(string connectionString)
{
    private static readonly UTF8Encoding ExactUtf8=new(false,true);
    private static readonly ActivitySource Diagnostics=new("FactoryConnect.V2.Activity.AcceptedEvidence");
    public sealed record AcceptedRow(
        Guid SiteId,Guid MachineId,string StreamKey,Guid MappingId,string Address,
        ulong InstanceId,ulong Sequence,Guid BatchId,Guid AcknowledgementId,
        DateTimeOffset ObservedAt,string RawValue,string Quality,
        string SourceTimestamp,string CanonicalSha256,string DocumentSha256);

    public sealed record BatchProof(Guid BatchId,Guid AcknowledgementId,ulong InstanceId,
        ulong FromSequence,ulong NextSequence,ulong FirstAvailableSequence,string DocumentSha256);
    public sealed record VerifiedCut(IReadOnlyList<AcceptedRow> Rows,IReadOnlyList<BatchProof> Batches);

    public async Task<IReadOnlyList<AcceptedRow>> ReadBoundedAsync(Guid siteId,ActivityQualificationIdentity identity,
        ulong instanceId,ulong afterSequence,ulong throughSequence,int take=100,CancellationToken ct=default)
        => (await ReadVerifiedCutAsync(siteId,identity,instanceId,afterSequence,throughSequence,take,ct)).Rows;

    public async Task<VerifiedCut> ReadVerifiedCutAsync(
        Guid siteId,ActivityQualificationIdentity identity,
        ulong instanceId,ulong afterSequence,ulong throughSequence,
        int take=100,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if(siteId==Guid.Empty||identity.MachineId==Guid.Empty||
           identity.MappingId==Guid.Empty||string.IsNullOrWhiteSpace(identity.StreamKey)||
           string.IsNullOrWhiteSpace(identity.SourceAddress)||
           throughSequence<=afterSequence||take is <1 or >100)
            throw new ArgumentException("Exact bounded accepted activity source cut required.");
        var stream=ExactUtf8.GetBytes(identity.StreamKey);
        var address=ExactUtf8.GetBytes(identity.SourceAddress);
        if(stream.Length>512||address.Length>512)
            throw new ArgumentException("Source identity exceeds SQL profile.");
        using var activity=Diagnostics.StartActivity("activity.accepted-source.read",ActivityKind.Internal);
        await using var connection=new SqlConnection(connectionString);
        if(activity is not null)connection.StatisticsEnabled=true;
        await connection.OpenAsync(ct);
        if(activity is not null)connection.ResetStatistics();
        await using var tx=(SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,ct);
        try
        {
            // First materialize only a bounded observation cut. Never duplicate
            // a 1 MiB document for every row in a joined SQL result.
            const long aggregateByteBudget=8L*1024*1024;
            long transferred=0;
            static void Account(ref long total,long bytes)
            {
                total=checked(total+bytes);
                if(total>aggregateByteBudget)
                    throw new InvalidDataException("Accepted evidence aggregate byte budget exceeded.");
            }
            var pending=new List<ObservationProjection>();
            await using(var command=new SqlCommand("""
                DECLARE @selected TABLE(Sequence decimal(20,0) NOT NULL PRIMARY KEY);
                INSERT @selected(Sequence)
                SELECT TOP (@take) o.Sequence
                FROM acquisition.MtConnectObservation o WITH(HOLDLOCK)
                WHERE o.SiteId=@site AND o.MachineId=@machine
                    AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping
                    AND o.AddressBytes=@address AND o.InstanceId=@instance
                    AND o.Sequence>@after AND o.Sequence<=@through
                ORDER BY o.Sequence;
                IF (SELECT COUNT_BIG(*) FROM @selected)>@limit
                    THROW 51073,'Accepted source cut exceeds requested row bound.',1;
                IF COALESCE((
                    SELECT SUM(CONVERT(bigint,DATALENGTH(o.CanonicalBytes))+
                        DATALENGTH(o.RawValue)+DATALENGTH(o.SourceTimestamp)+256)
                    FROM acquisition.MtConnectObservation o
                    JOIN @selected selected ON selected.Sequence=o.Sequence
                    WHERE o.SiteId=@site AND o.MachineId=@machine
                        AND o.StreamKeyBytes=@stream AND o.InstanceId=@instance
                ),0)>@budget
                    THROW 51074,'Accepted source observation byte budget exceeded.',1;
                SELECT o.SiteId,o.MachineId,o.StreamKeyBytes,o.MappingId,o.AddressBytes,
                    o.InstanceId,o.Sequence,o.BatchId,o.ObservedAt,o.RawValue,
                    o.Quality,o.SourceTimestamp,o.CanonicalBytes,o.ContentHash
                FROM acquisition.MtConnectObservation o WITH(HOLDLOCK)
                JOIN @selected selected ON selected.Sequence=o.Sequence
                WHERE o.SiteId=@site AND o.MachineId=@machine
                    AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping
                    AND o.AddressBytes=@address AND o.InstanceId=@instance
                ORDER BY o.Sequence
                """,connection,tx))
            {
                command.Parameters.Add("@site",SqlDbType.UniqueIdentifier).Value=siteId;
                command.Parameters.Add("@machine",SqlDbType.UniqueIdentifier).Value=identity.MachineId;
                command.Parameters.Add("@stream",SqlDbType.VarBinary,512).Value=stream;
                command.Parameters.Add("@mapping",SqlDbType.UniqueIdentifier).Value=identity.MappingId;
                command.Parameters.Add("@address",SqlDbType.VarBinary,512).Value=address;
                DecimalParam(command,"@instance",instanceId);
                DecimalParam(command,"@after",afterSequence);
                DecimalParam(command,"@through",throughSequence);
                command.Parameters.Add("@take",SqlDbType.Int).Value=take+1;
                command.Parameters.Add("@limit",SqlDbType.Int).Value=take;
                command.Parameters.Add("@budget",SqlDbType.BigInt).Value=aggregateByteBudget;
                await using var reader=await command.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                {
                    if(pending.Count==take)
                        throw new InvalidDataException("Accepted source cut exceeds requested bound; narrow the cut.");
                    var observed=reader.GetDateTimeOffset(8);
                    var rawValue=reader.GetString(9);
                    var quality=reader.GetString(10);
                    var timestamp=reader.GetString(11);
                    var canonical=(byte[])reader[12];
                    var hash=(byte[])reader[13];
                    Account(ref transferred,canonical.Length+
                        2L*rawValue.Length+2L*timestamp.Length+256);
                    if(canonical.Length==0 ||
                       !CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonical),hash))
                        throw new InvalidDataException("Accepted observation canonical digest mismatch.");
                    var sequence=decimal.ToUInt64(reader.GetDecimal(6));
                    var instance=decimal.ToUInt64(reader.GetDecimal(5));
                    var actualStream=(byte[])reader[2];
                    var actualAddress=(byte[])reader[4];
                    var site=reader.GetGuid(0);
                    var machine=reader.GetGuid(1);
                    var mapping=reader.GetGuid(3);
                    var batchId=reader.GetGuid(7);
                    if(site!=siteId||machine!=identity.MachineId||
                       mapping!=identity.MappingId||instance!=instanceId||
                       sequence<=afterSequence||sequence>throughSequence||
                       !actualStream.AsSpan().SequenceEqual(stream)||
                       !actualAddress.AsSpan().SequenceEqual(address))
                        throw new InvalidDataException("Accepted source identity mismatch.");
                    // Bind all materialized fields to the *verified canonical* JSON.
                    // Timestamp/quality are not in that JSON: verify against the
                    // independently verified immutable XML document below.
                    ValidateCanonical(canonical,sequence,identity.SourceAddress,rawValue);
                    pending.Add(new(site,machine,instance,sequence,batchId,
                        mapping,observed,rawValue,quality,timestamp,canonical,
                        Convert.ToHexString(hash)));
                }
            }

            var batches=new Dictionary<Guid,VerifiedBatch>();
            {
                // One distinct batch row/document per selected source cut.
                await using var command=new SqlCommand("""
                    DECLARE @selected TABLE(BatchId uniqueidentifier NOT NULL PRIMARY KEY);
                    INSERT @selected(BatchId)
                    SELECT TOP (101) BatchId FROM acquisition.MtConnectBatch WITH(HOLDLOCK,INDEX(IX_MtConnectBatch_ActivityWindow),FORCESEEK)
                    WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream
                        AND InstanceId=@instance AND FromSequence<=@through AND NextSequence>@after+1
                    ORDER BY FromSequence,BatchId;
                    IF (SELECT COUNT_BIG(*) FROM @selected)>100
                        THROW 51075,'Accepted source cut exceeds batch bound.',1;
                    IF (SELECT COUNT_BIG(*) FROM @selected selected
                        INNER LOOP JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK,FORCESEEK)
                            ON b.BatchId=selected.BatchId)<>(SELECT COUNT_BIG(*) FROM @selected)
                        THROW 51075,'Missing source batch.',1;
                    IF COALESCE((
                        SELECT SUM(CONVERT(bigint,DATALENGTH(b.RawDocument))+512)
                        FROM @selected selected
                        INNER LOOP JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK,FORCESEEK)
                            ON b.BatchId=selected.BatchId
                    ),0)>@remaining
                        THROW 51074,'Accepted source batch document budget exceeded.',1;
                    SELECT b.BatchId,b.SiteId,b.MachineId,b.StreamKeyBytes,
                        b.AcknowledgementId,b.InstanceId,b.FromSequence,
                        b.NextSequence,b.FirstAvailableSequence,b.RawDocument,b.DocumentHash
                    FROM @selected selected
                    INNER LOOP JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK,FORCESEEK)
                        ON b.BatchId=selected.BatchId
                    """,connection,tx);
                command.Parameters.AddWithValue("@site",siteId);
                command.Parameters.AddWithValue("@machine",identity.MachineId);
                command.Parameters.Add("@stream",SqlDbType.VarBinary,512).Value=stream;
                DecimalParam(command,"@instance",instanceId);
                DecimalParam(command,"@after",afterSequence);
                DecimalParam(command,"@through",throughSequence);
                command.Parameters.Add("@remaining",SqlDbType.BigInt).Value=
                    aggregateByteBudget-transferred;
                await using var reader=await command.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                {
                    var batchId=reader.GetGuid(0);
                    var site=reader.GetGuid(1);
                    var machine=reader.GetGuid(2);
                    var streamBytes=(byte[])reader[3];
                    var acknowledgementId=reader.GetGuid(4);
                    var instance=decimal.ToUInt64(reader.GetDecimal(5));
                    var from=decimal.ToUInt64(reader.GetDecimal(6));
                    var next=decimal.ToUInt64(reader.GetDecimal(7));
                    var first=decimal.ToUInt64(reader.GetDecimal(8));
                    var raw=(byte[])reader[9];
                    var hash=(byte[])reader[10];
                    Account(ref transferred,raw.Length+512);
                    if(site!=siteId||machine!=identity.MachineId||
                       !streamBytes.AsSpan().SequenceEqual(stream)||
                       instance!=instanceId||from>next||first>from||
                       !CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw),hash))
                        throw new InvalidDataException("Accepted batch scope, frontier or digest mismatch.");
                    // The existing bounded MTConnect parser is the canonical
                    // authority for source fields. Parse each distinct, verified
                    // batch once, then reuse its indexed observations for this cut.
                    const string prefix="mtconnect:uuid:";
                    if(!identity.StreamKey.StartsWith(prefix,StringComparison.Ordinal))
                        throw new InvalidDataException("Unsupported accepted source stream.");
                    var device=identity.StreamKey[prefix.Length..];
                    var parsed=MtConnectSampleParser.Parse(raw,device,from);
                    if(parsed.InstanceId!=instance || parsed.FirstSequence!=first ||
                       parsed.NextSequence!=next)
                        throw new InvalidDataException("Retained batch headers disagree with verified document.");
                    var bySequence=parsed.Observations.ToDictionary(x=>x.Sequence);
                    if(!batches.TryAdd(batchId,new(batchId,acknowledgementId,instance,
                        from,next,first,bySequence,Convert.ToHexString(hash))))
                        throw new InvalidDataException("Duplicate accepted batch identity.");
                }
                var expected=batches.Values.SelectMany(batch=>batch.BySequence.Values)
                    .Where(source=>source.Sequence>afterSequence && source.Sequence<=throughSequence &&
                        string.Equals(source.DataItemId,identity.SourceAddress,StringComparison.Ordinal))
                    .Select(source=>source.Sequence).Order().ToArray();
                if(!expected.SequenceEqual(pending.Select(row=>row.Sequence)))
                    throw new InvalidDataException("Accepted source cut omits or duplicates document observations.");
            }
            var rows=new List<AcceptedRow>(pending.Count);
            foreach(var projected in pending)
            {
                if(!batches.TryGetValue(projected.BatchId,out var batch)||
                   projected.Sequence<batch.From||projected.Sequence>=batch.Next||
                   projected.InstanceId!=batch.Instance)
                    throw new InvalidDataException("Accepted batch cannot authorize observation frontier.");
                if(!batch.BySequence.TryGetValue(projected.Sequence,out var source) ||
                   !string.Equals(source.DataItemId,identity.SourceAddress,StringComparison.Ordinal) ||
                   !string.Equals(source.RawValue,projected.RawValue,StringComparison.Ordinal) ||
                   !string.Equals(source.Quality,projected.Quality,StringComparison.Ordinal) ||
                   !string.Equals(source.SourceTimestamp,projected.SourceTimestamp,StringComparison.Ordinal) ||
                   source.Timestamp!=projected.ObservedAt ||
                   !ExactUtf8.GetBytes(source.CanonicalJson).AsSpan()
                       .SequenceEqual(projected.CanonicalBytes) ||
                   !string.Equals(source.ContentHash,projected.CanonicalSha256,StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Accepted observation projection disagrees with authenticated source document.");
                rows.Add(new(projected.SiteId,projected.MachineId,identity.StreamKey,
                    projected.MappingId,identity.SourceAddress,projected.InstanceId,
                    projected.Sequence,projected.BatchId,batch.AcknowledgementId,
                    projected.ObservedAt,projected.RawValue,projected.Quality,
                    projected.SourceTimestamp,projected.CanonicalSha256,
                    batch.DocumentSha256));
            }
            await tx.CommitAsync(ct);
            if(activity is not null)
            {
                activity.SetTag("accepted-source.observations",rows.Count);
                activity.SetTag("accepted-source.distinct-batches",batches.Count);
                activity.SetTag("accepted-source.batch-hash-verifications",batches.Count);
                activity.SetTag("accepted-source.batch-parses",batches.Count);
                activity.SetTag("accepted-source.aggregate-bytes",transferred);
                activity.SetTag("accepted-source.sql.roundtrips",
                    connection.RetrieveStatistics()["ServerRoundtrips"]);
            }
            return new(rows.AsReadOnly(),batches.Values.Select(batch=>new BatchProof(batch.BatchId,
                batch.AcknowledgementId,batch.Instance,batch.From,batch.Next,batch.First,batch.DocumentSha256)).ToArray());
        }
        catch
        {
            try{await tx.RollbackAsync(CancellationToken.None);}
            catch(Exception ex)when(ex is SqlException or InvalidOperationException){}
            throw;
        }
    }
    private sealed record ObservationProjection(
        Guid SiteId,Guid MachineId,ulong InstanceId,ulong Sequence,Guid BatchId,
        Guid MappingId,DateTimeOffset ObservedAt,string RawValue,string Quality,
        string SourceTimestamp,byte[] CanonicalBytes,string CanonicalSha256);
    private sealed record VerifiedBatch(Guid BatchId,Guid AcknowledgementId,
        ulong Instance,ulong From,ulong Next,ulong First,
        IReadOnlyDictionary<ulong,MtConnectSampleObservation> BySequence,
        string DocumentSha256);

    private static void ValidateCanonical(
        byte[] canonical,ulong sequence,string address,string rawValue)
    {
        try
        {
            using var document=JsonDocument.Parse(canonical);
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||
               root.GetProperty("sequence").GetString()!=
                   sequence.ToString(CultureInfo.InvariantCulture)||
               !string.Equals(root.GetProperty("value").GetString(),rawValue,
                   StringComparison.Ordinal))
                throw new InvalidDataException("Canonical source sequence/value differs from projection.");
            var attributes=root.GetProperty("attributes");
            if(attributes.ValueKind!=JsonValueKind.Array||
               attributes.EnumerateArray().Count(x=>x.GetProperty("name").GetString()=="dataItemId"&&
                   string.Equals(x.GetProperty("Value").GetString(),address,
                       StringComparison.Ordinal))!=1||
               attributes.EnumerateArray().Count(x=>x.GetProperty("name").GetString()=="sequence"&&
                   x.GetProperty("Value").GetString()==
                       sequence.ToString(CultureInfo.InvariantCulture))!=1)
                throw new InvalidDataException("Canonical attribute identity differs from projection.");
        }
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or
            InvalidOperationException)
        {
            throw new InvalidDataException("Malformed canonical observation identity.",ex);
        }
    }

    private static void DecimalParam(SqlCommand command,string name,ulong value)
    {
        var parameter=command.Parameters.Add(name,SqlDbType.Decimal);
        parameter.Precision=20;
        parameter.Scale=0;
        parameter.Value=Convert.ToDecimal(value,CultureInfo.InvariantCulture);
    }
}