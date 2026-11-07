using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
            await using var command=new SqlCommand("""
                SELECT TOP (@take)
                    o.SiteId,o.MachineId,o.StreamKeyBytes,o.MappingId,o.AddressBytes,
                    o.InstanceId,o.Sequence,o.BatchId,b.AcknowledgementId,
                    o.ObservedAt,o.RawValue,o.Quality,o.SourceTimestamp,
                    o.CanonicalBytes,o.ContentHash,b.RawDocument,b.DocumentHash,
                    b.InstanceId,b.FromSequence,b.NextSequence,b.FirstAvailableSequence
                FROM acquisition.MtConnectObservation o WITH(HOLDLOCK)
                JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK)
                    ON b.BatchId=o.BatchId AND b.SiteId=o.SiteId
                    AND b.MachineId=o.MachineId AND b.StreamKeyBytes=o.StreamKeyBytes
                WHERE o.SiteId=@site AND o.MachineId=@machine
                    AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping
                    AND o.AddressBytes=@address AND o.InstanceId=@instance
                    AND o.Sequence>@after AND o.Sequence<=@through
                ORDER BY o.Sequence
                """,connection,tx);
            command.Parameters.Add("@site",SqlDbType.UniqueIdentifier).Value=siteId;
            command.Parameters.Add("@machine",SqlDbType.UniqueIdentifier).Value=identity.MachineId;
            command.Parameters.Add("@stream",SqlDbType.VarBinary,512).Value=stream;
            command.Parameters.Add("@mapping",SqlDbType.UniqueIdentifier).Value=identity.MappingId;
            command.Parameters.Add("@address",SqlDbType.VarBinary,512).Value=address;
            DecimalParam(command,"@instance",instanceId);
            DecimalParam(command,"@after",afterSequence);
            DecimalParam(command,"@through",throughSequence);
            command.Parameters.Add("@take",SqlDbType.Int).Value=take+1;
            var rows=new List<AcceptedRow>();
            await using(var reader=await command.ExecuteReaderAsync(ct))
            {
                while(await reader.ReadAsync(ct))
                {
                    if(rows.Count==take)
                        throw new InvalidDataException("Accepted source cut exceeds requested bound; narrow the cut.");
                    var canonical=(byte[])reader[13];
                    var canonicalHash=(byte[])reader[14];
                    var raw=(byte[])reader[15];
                    var documentHash=(byte[])reader[16];
                    if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonical),canonicalHash)||
                       !CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw),documentHash))
                        throw new InvalidDataException("Accepted observation or batch integrity mismatch.");
                    var observed=reader.GetDateTimeOffset(9);
                    var sequence=decimal.ToUInt64(reader.GetDecimal(6));
                    var instance=decimal.ToUInt64(reader.GetDecimal(5));
                    var from=decimal.ToUInt64(reader.GetDecimal(18));
                    var next=decimal.ToUInt64(reader.GetDecimal(19));
                    var first=decimal.ToUInt64(reader.GetDecimal(20));
                    if(observed.Offset!=TimeSpan.Zero||
                       instance!=decimal.ToUInt64(reader.GetDecimal(17))||
                       sequence<from||sequence>=next||first>from||
                       !((byte[])reader[2]).AsSpan().SequenceEqual(stream)||
                       !((byte[])reader[4]).AsSpan().SequenceEqual(address)||
                       reader.GetGuid(0)!=siteId||reader.GetGuid(1)!=identity.MachineId||
                       reader.GetGuid(3)!=identity.MappingId)
                        throw new InvalidDataException("Accepted observation provenance or source frontier disagrees.");
                    rows.Add(new(reader.GetGuid(0),reader.GetGuid(1),identity.StreamKey,
                        reader.GetGuid(3),identity.SourceAddress,instance,sequence,
                        reader.GetGuid(7),reader.GetGuid(8),observed,reader.GetString(10),
                        reader.GetString(11),reader.GetString(12),
                        Convert.ToHexString(canonicalHash),Convert.ToHexString(documentHash)));
                }
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
    private static void DecimalParam(SqlCommand command,string name,ulong value)
    {
        var parameter=command.Parameters.Add(name,SqlDbType.Decimal);
        parameter.Precision=20;
        parameter.Scale=0;
        parameter.Value=Convert.ToDecimal(value,CultureInfo.InvariantCulture);
    }
}
