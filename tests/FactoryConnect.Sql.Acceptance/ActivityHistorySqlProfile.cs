using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

// One ad-hoc, application-filtered profile per depth. Latency trials run without XE.
internal static class ActivityHistorySqlProfile
{
    internal sealed record Access(string Phase,string Table,string? Index,string PhysicalOperator,
        long? ActualRowsRead,long ActualRows,long Executions);
    internal sealed record Statement(string EventName,string Phase,long LogicalReads,long PhysicalReads,long RowsAffectedOrReturned,double DurationMs);
    internal sealed record Profile(IReadOnlyList<Statement> Commands,IReadOnlyList<Statement> Statements,IReadOnlyList<Access> Accesses,
        int PlanEvents,long DroppedEvents,bool Truncated,string RawEvidenceFile);

    internal static async Task<Profile> CaptureAsync(SqlConnection control,string application,
        Func<Task<IReadOnlyList<ActivityHistoryMeasurementAcceptance.Span>>> action,string evidenceFile)
    {
        var name="fc2_activity_profile_"+Guid.NewGuid().ToString("N");
        // Both identifiers are generated locally, not supplied by a caller.
        if(!application.StartsWith("fc2-activity-history-",StringComparison.Ordinal) ||
            application.Any(ch=>!char.IsAsciiLetterOrDigit(ch) && ch!='-'))
            throw new InvalidDataException("Unexpected history measurement application identity.");
        await Exec($"""
            CREATE EVENT SESSION [{name}] ON SERVER
            ADD EVENT sqlserver.sql_statement_completed(
                ACTION(sqlserver.client_app_name,sqlserver.session_id)
                WHERE ([sqlserver].[client_app_name]=N'{application}')),
            ADD EVENT sqlserver.sp_statement_completed(
                ACTION(sqlserver.client_app_name,sqlserver.session_id)
                WHERE ([sqlserver].[client_app_name]=N'{application}')),
            ADD EVENT sqlserver.rpc_completed(
                ACTION(sqlserver.client_app_name,sqlserver.session_id)
                WHERE ([sqlserver].[client_app_name]=N'{application}')),
            ADD EVENT sqlserver.sql_batch_completed(
                ACTION(sqlserver.client_app_name,sqlserver.session_id)
                WHERE ([sqlserver].[client_app_name]=N'{application}')),
            ADD EVENT sqlserver.query_post_execution_showplan(
                ACTION(sqlserver.client_app_name,sqlserver.session_id)
                WHERE ([sqlserver].[client_app_name]=N'{application}'))
            ADD TARGET package0.ring_buffer(SET max_memory=4096)
            WITH (MAX_MEMORY=4096 KB,MAX_DISPATCH_LATENCY=1 SECONDS);
            ALTER EVENT SESSION [{name}] ON SERVER STATE=START;
            """);
        try
        {
            var spans=await action();
            // Drain event dispatch outside the measured processing interval.
            await Task.Delay(1200);
            await using var q=new SqlCommand("""
                SELECT s.dropped_event_count,CONVERT(nvarchar(max),t.target_data)
                FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t
                    ON s.address=t.event_session_address
                WHERE s.name=@name AND t.target_name='ring_buffer'
                """,control);
            q.Parameters.AddWithValue("@name",name);
            long dropped;
            string xml;
            await using(var r=await q.ExecuteReaderAsync())
            {
                if(!await r.ReadAsync())throw new InvalidDataException("Activity SQL profile target is missing.");
                dropped=Convert.ToInt64(r[0],CultureInfo.InvariantCulture);
                xml=r.GetString(1);
            }
            // Preserve failed captures too, before parsing/qualification can throw.
            await File.WriteAllTextAsync(evidenceFile,System.Text.Json.JsonSerializer.Serialize(new {
                schemaVersion=1,fixture=true,application,sqlExtendedEventsXml=xml,dropped,
                qualification="raw capture; extraction/coverage validation not yet complete"
            },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            var root=XDocument.Parse(xml).Root??throw new InvalidDataException("Empty activity SQL profile.");
            var truncated=(string?)root.Attribute("truncated")=="1";
            dropped+=Number((string?)root.Attribute("droppedCount"));
            if(dropped!=0 || truncated)
                throw new InvalidDataException("Activity SQL profile dropped or truncated evidence.");
            var commands=new List<Statement>();
            var statements=new List<Statement>();
            var accesses=new List<Access>();
            var plans=0;
            foreach(var item in root.Elements("event"))
            {
                var timestamp=DateTimeOffset.Parse((string)item.Attribute("timestamp")!,CultureInfo.InvariantCulture);
                var phase=spans.Where(span=>timestamp>=span.Start && timestamp<=span.Start+span.Duration)
                    .OrderBy(span=>span.Duration).Select(span=>span.Name).FirstOrDefault()??"outside-client-span";
                string? Data(string field)=>item.Elements("data").SingleOrDefault(x=>(string?)x.Attribute("name")==field)?.Element("value")?.Value;
                var eventName=(string?)item.Attribute("name");
                if(eventName is "sql_statement_completed" or "sp_statement_completed" or "rpc_completed" or "sql_batch_completed")
                {
                    if(Data("logical_reads") is null)
                        throw new InvalidDataException("SQL statement logical reads are missing from the profile.");
                    var counter=new Statement(eventName,phase,Number(Data("logical_reads")),Number(Data("physical_reads")),
                        Number(Data("row_count")),Number(Data("duration"))/1000.0);
                    if(eventName is "rpc_completed" or "sql_batch_completed")commands.Add(counter);
                    else statements.Add(counter);
                }
                else if((string?)item.Attribute("name")=="query_post_execution_showplan")
                {
                    plans++;
                    var value=item.Elements("data").Single(x=>(string?)x.Attribute("name")=="showplan_xml").Element("value")!;
                    var plan=value.Elements().FirstOrDefault()??XElement.Parse(value.Value);
                    XNamespace ns="http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                    foreach(var op in plan.Descendants(ns+"RelOp"))
                    {
                        var objects=op.Descendants(ns+"Object").Where(x=>x.Ancestors(ns+"RelOp").FirstOrDefault()==op);
                        foreach(var table in objects)
                        {
                            var tableName=((string?)table.Attribute("Table"))?.Trim('[',']');
                            if(tableName is not ("MtConnectObservation" or "MtConnectBatch" or "MtConnectCursor" or
                                "ProcessorCheckpoint" or "ProcessorOperation" or "ClosedPeriod"))continue;
                            var counters=op.Element(ns+"RunTimeInformation")?.Elements(ns+"RunTimeCountersPerThread").ToArray()??[];
                            if(counters.Length==0)continue;
                            long? read=counters.All(x=>x.Attribute("ActualRowsRead") is not null)
                                ? counters.Sum(x=>Number((string?)x.Attribute("ActualRowsRead"))):null;
                            accesses.Add(new(phase,tableName,(string?)table.Attribute("Index"),
                                (string?)op.Attribute("PhysicalOp")??"unknown",read,
                                counters.Sum(x=>Number((string?)x.Attribute("ActualRows"))),
                                counters.Sum(x=>Number((string?)x.Attribute("ActualExecutions")))));
                        }
                    }
                }
            }
            if(commands.Count==0 || statements.Count==0 || plans==0 || !accesses.Any(x=>x.Table=="MtConnectObservation" && x.ActualRowsRead>0))
                throw new InvalidDataException($"Activity SQL profile incomplete: commands={commands.Count}, statements={statements.Count}, plans={plans}, accesses={accesses.Count}, observationRowsReadCounters={accesses.Count(x=>x.Table=="MtConnectObservation" && x.ActualRowsRead>0)}.");
            await File.WriteAllTextAsync(evidenceFile,System.Text.Json.JsonSerializer.Serialize(new {
                schemaVersion=1,fixture=true,application,sqlExtendedEventsXml=xml,
                correlation="Server event UTC within the narrowest client diagnostic span; unmatched events are retained explicitly.",
                logicalReadAggregation="RPC/language-batch command totals; nested statement counters retained separately, not added to command totals.",
                commands,statements,accesses,plans,dropped,truncated
            },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            return new(commands,statements,accesses,plans,dropped,truncated,evidenceFile);
        }
        finally
        {
            await Exec($"ALTER EVENT SESSION [{name}] ON SERVER STATE=STOP; DROP EVENT SESSION [{name}] ON SERVER;");
        }
        async Task Exec(string sql)
        {
            await using var command=new SqlCommand(sql,control);await command.ExecuteNonQueryAsync();
        }
    }
    private static long Number(string? value)=>string.IsNullOrEmpty(value)?0:long.Parse(value,CultureInfo.InvariantCulture);
}
