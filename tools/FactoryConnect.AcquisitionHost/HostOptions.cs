using System.Text.Json;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Data.SqlClient;
namespace FactoryConnect.AcquisitionHost;

internal sealed record HostOptions(int Version,Guid InstallationId,Guid SiteId,Guid MachineId,string StreamKey,string AgentBaseUri,string? BootstrapFromSequence,
    string SqlConnectionString,string TrustAnchorPath,string AllowedCrlUri,string ServerCertificatePath,string ServerKeyPath,string RuntimeCertificatePath,string RuntimeKeyPath,
    int PollIntervalMilliseconds=250,int BatchCount=100,int OperationTimeoutSeconds=15)
{
    internal static HostOptions Load(string path,bool readOnly)
    {
        if(new FileInfo(path).Length>32768)throw new InvalidDataException("Configuration byte limit exceeded.");
        var options=JsonSerializer.Deserialize<HostOptions>(File.ReadAllBytes(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException();
        if(options.Version!=1||new[]{options.InstallationId,options.SiteId,options.MachineId}.Any(x=>x==Guid.Empty)||string.IsNullOrEmpty(options.StreamKey)||!options.StreamKey.StartsWith("mtconnect:uuid:",StringComparison.Ordinal)||options.StreamKey.Length>512)throw new InvalidDataException("Explicit installation/machine/stream identity required.");
        var sql=new SqlConnectionStringBuilder(options.SqlConnectionString);
        if(string.IsNullOrWhiteSpace(sql.DataSource)||string.IsNullOrWhiteSpace(sql.InitialCatalog)||new[]{"master","model","msdb","tempdb"}.Contains(sql.InitialCatalog,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Explicit existing V2 database required.");
        if(!readOnly)
        {
            _=new MtConnectEndpoint(new Uri(options.AgentBaseUri,UriKind.Absolute));
            if(options.PollIntervalMilliseconds is <50 or >60000||options.BatchCount is <1 or >1000||options.OperationTimeoutSeconds is <1 or >30)throw new InvalidDataException("Polling profile bounds exceeded.");
            foreach(var file in new[]{options.TrustAnchorPath,options.ServerCertificatePath,options.ServerKeyPath,options.RuntimeCertificatePath,options.RuntimeKeyPath})if(string.IsNullOrWhiteSpace(file)||!File.Exists(Path.GetFullPath(file,Path.GetDirectoryName(Path.GetFullPath(path))!))||new FileInfo(Path.GetFullPath(file,Path.GetDirectoryName(Path.GetFullPath(path))!)).Length>32768)throw new InvalidDataException("Provisioned certificate/key file required.");
        }
        var directory=Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Resolve(string value)=>Path.GetFullPath(value,directory);
        return readOnly?options:options with{TrustAnchorPath=Resolve(options.TrustAnchorPath),ServerCertificatePath=Resolve(options.ServerCertificatePath),ServerKeyPath=Resolve(options.ServerKeyPath),RuntimeCertificatePath=Resolve(options.RuntimeCertificatePath),RuntimeKeyPath=Resolve(options.RuntimeKeyPath)};
    }
}
