using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.AcquisitionHost;
using FactoryConnect.Sql.Security;

static void Report(object value)=>Console.WriteLine(JsonSerializer.Serialize(value));
using var stopping=new CancellationTokenSource();
try
{
    if(args.Length is <2 or >3||args[0]!="--config"||(args.Length==3&&args[2]!="--read"))throw new ArgumentException("Use --config path [--read].");
    var readOnly=args.Length==3;var options=HostOptions.Load(args[1],readOnly);
    Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stopping.Cancel();};
    using var termination=OperatingSystem.IsWindows()?null:PosixSignalRegistration.Create(PosixSignal.SIGTERM,context=>{context.Cancel=true;stopping.Cancel();});
    if(readOnly)
    {
        if(!Console.IsInputRedirected)throw new ArgumentException("Supply the session bearer through redirected stdin.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));var line=await Task.Run(()=>Console.In.ReadLine()).WaitAsync(timeout.Token);
        if(line is null||line.Length>128)throw new ArgumentException("Base64 session bearer required on stdin.");var bearer=Convert.FromBase64String(line);
        try{if(bearer.Length!=32)throw new ArgumentException("Exact session bearer required.");var token=SHA256.HashData(bearer);var rows=await new SqlSecurityAuthority(options.SqlConnectionString,options.InstallationId).ReadMtConnectEvidenceAsync(token,options.SiteId,options.MachineId,options.StreamKey,20,timeout.Token);Report(new{status="accepted-evidence",historical=true,notConnectivity=true,rows});}
        finally{CryptographicOperations.ZeroMemory(bearer);}
        return 0;
    }
    if(Console.IsInputRedirected)_=Task.Run(StopOnInput);
    return await new AcquisitionWorker(options,Report).Run(stopping.Token);
    async Task StopOnInput(){try{while(!stopping.IsCancellationRequested){var line=await Console.In.ReadLineAsync(stopping.Token);if(line is null)return;if(line=="stop"){stopping.Cancel();return;}}}catch(OperationCanceledException){}}
}
catch(OperationCanceledException)when(stopping.IsCancellationRequested){Report(new{status="stopped"});return 0;}
catch(Exception error){Report(new{status="held",reason="configuration-or-startup-failure",errorType=error.GetType().Name});return 20;}
