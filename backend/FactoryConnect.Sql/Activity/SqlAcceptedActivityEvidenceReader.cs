using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
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
    public sealed record AcceptedRow(
        Guid SiteId,Guid MachineId,string StreamKey,Guid MappingId,string Address,
        ulong InstanceId,ulong Sequence,Guid BatchId,Guid AcknowledgementId,
        DateTimeOffset ObservedAt,string RawValue,string Quality,
        string SourceTimestamp,string CanonicalSha256,string DocumentSha256);

    public async Task<IReadOnlyList<AcceptedRow>> ReadBoundedAsync(
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
        await using var connection=new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
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
                SELECT TOP (@take)
                    o.SiteId,o.MachineId,o.StreamKeyBytes,o.MappingId,o.AddressBytes,
                    o.InstanceId,o.Sequence,o.BatchId,o.ObservedAt,o.RawValue,
                    o.Quality,o.SourceTimestamp,o.CanonicalBytes,o.ContentHash
                FROM acquisition.MtConnectObservation o WITH(HOLDLOCK)
                WHERE o.SiteId=@site AND o.MachineId=@machine
                    AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping
                    AND o.AddressBytes=@address AND o.InstanceId=@instance
                    AND o.Sequence>@after AND o.Sequence<=@through
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
                await using var reader=await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess,ct);
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
                        mapping,observed,rawValue,quality,timestamp,
                        Convert.ToHexString(hash)));
                }
            }
            var batchIds=pending.Select(row=>row.BatchId).Distinct().ToArray();
            var batches=new Dictionary<Guid,VerifiedBatch>();
            if(batchIds.Length>0)
            {
                // One distinct batch row/document per selected source cut.
                var idsJson=JsonSerializer.Serialize(batchIds);
                await using var command=new SqlCommand("""
                    SELECT b.BatchId,b.SiteId,b.MachineId,b.StreamKeyBytes,
                        b.AcknowledgementId,b.InstanceId,b.FromSequence,
                        b.NextSequence,b.FirstAvailableSequence,b.RawDocument,b.DocumentHash
                    FROM acquisition.MtConnectBatch b WITH(HOLDLOCK)
                    WHERE b.BatchId IN
                        (SELECT BatchId FROM OPENJSON(@batchIds)
                            WITH(BatchId uniqueidentifier '$'))
                    """,connection,tx);
                command.Parameters.Add("@batchIds",SqlDbType.NVarChar,-1).Value=idsJson;
                await using var reader=await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess,ct);
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
                    if(!batches.TryAdd(batchId,new(batchId,acknowledgementId,instance,
                        from,next,first,raw,Convert.ToHexString(hash))))
                        throw new InvalidDataException("Duplicate accepted batch identity.");
                }
                if(batches.Count!=batchIds.Length)
                    throw new InvalidDataException("Missing retained accepted batch.");
            }
            var rows=new List<AcceptedRow>(pending.Count);
            foreach(var projected in pending)
            {
                if(!batches.TryGetValue(projected.BatchId,out var batch)||
                   projected.Sequence<batch.From||projected.Sequence>=batch.Next||
                   projected.InstanceId!=batch.Instance)
                    throw new InvalidDataException("Accepted batch cannot authorize observation frontier.");
                ValidateAgainstDocument(batch.RawDocument,identity.StreamKey,
                    projected.Sequence,identity.SourceAddress,projected.ObservedAt,
                    projected.RawValue,projected.Quality,projected.SourceTimestamp);
                rows.Add(new(projected.SiteId,projected.MachineId,identity.StreamKey,
                    projected.MappingId,identity.SourceAddress,projected.InstanceId,
                    projected.Sequence,projected.BatchId,batch.AcknowledgementId,
                    projected.ObservedAt,projected.RawValue,projected.Quality,
                    projected.SourceTimestamp,projected.CanonicalSha256,
                    batch.DocumentSha256));
            }
            await tx.CommitAsync(ct);
            return rows.AsReadOnly();
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
        string SourceTimestamp,string CanonicalSha256);
    private sealed record VerifiedBatch(Guid BatchId,Guid AcknowledgementId,
        ulong Instance,ulong From,ulong Next,ulong First,byte[] RawDocument,
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

    private static void ValidateAgainstDocument(
        byte[] raw,string stream,ulong sequence,string address,
        DateTimeOffset observed,string rawValue,string quality,string sourceTimestamp)
    {
        // The document is already SHA-verified. Its source header supplies the
        // trusted instance/window; the original element supplies timestamp and
        // exact quality semantics missing from canonical observation JSON.
        try
        {
            using var input=new MemoryStream(raw,false);
            using var xml=XmlReader.Create(input,new XmlReaderSettings{
                DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,
                MaxCharactersInDocument=1048576});
            var document=XDocument.Load(xml);
            var device=stream.StartsWith("mtconnect:uuid:",StringComparison.Ordinal)
                ?stream["mtconnect:uuid:".Length..]:throw new InvalidDataException(
                    "Unsupported exact MTConnect stream.");
            var devices=document.Descendants().Where(x=>x.Name.LocalName=="DeviceStream").ToArray();
            if(devices.Length!=1||
               !string.Equals((string?)devices[0].Attribute("uuid"),device,
                   StringComparison.Ordinal))
                throw new InvalidDataException("Source document device differs from projection.");
            var matches=devices[0].Descendants()
                .Where(x=>(string?)x.Attribute("sequence")==
                    sequence.ToString(CultureInfo.InvariantCulture)).Take(2).ToArray();
            if(matches.Length!=1||!string.Equals(
                   (string?)matches[0].Attribute("dataItemId"),address,
                   StringComparison.Ordinal)||
               !string.Equals(matches[0].Value,rawValue,StringComparison.Ordinal)||
               !string.Equals((string?)matches[0].Attribute("timestamp"),
                   sourceTimestamp,StringComparison.Ordinal))
                throw new InvalidDataException("Accepted source projection differs from verified document.");
            var parsed=XmlConvert.ToDateTimeOffset(sourceTimestamp).ToUniversalTime();
            if(parsed!=observed||observed.Offset!=TimeSpan.Zero||
               quality!=(rawValue=="UNAVAILABLE"?"unavailable":"source-reported"))
                throw new InvalidDataException("Accepted timestamp or quality differs from verified source.");
        }
        catch(Exception ex) when(ex is XmlException or FormatException)
        {
            throw new InvalidDataException("Invalid retained MTConnect document or timestamp.",ex);
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
