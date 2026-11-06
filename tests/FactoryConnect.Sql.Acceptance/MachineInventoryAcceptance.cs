using FactoryConnect.Sql.Inventory;
using Microsoft.Data.SqlClient;

internal static class MachineInventoryAcceptance
{
    public static async Task<int> Run(string connectionString, Guid installationId)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + label);
            count++;
            Console.WriteLine("PASS: " + label);
        }

        var source = new SqlMachineInventoryReader(connectionString, installationId);
        var before = await source.ReadAsync();
        Check(before.Count == 1, "inventory includes existing master machine");
        Check(before[0].MachineId == Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "inventory retains exact machine identity");
        Check(before[0].DisplayName == "Renamed machine", "inventory reads current display name");

        await using (var sql = new SqlConnection(connectionString))
        {
            await sql.OpenAsync();
            await using var command = new SqlCommand("""
                INSERT [master].Machine(MachineId,SiteId,DisplayName,IsRetired)
                VALUES('44444444-4444-4444-4444-444444444444',
                       '22222222-2222-2222-2222-222222222222',N'Retired',1);
                """, sql);
            await command.ExecuteNonQueryAsync();
        }

        var after = await source.ReadAsync();
        Check(after.Count == 2, "inventory includes retired identities without activation claims");
        Check(after[0].SiteId != after[1].SiteId, "inventory preserves cross-site ownership");
        Check(after.Any(machine => machine.IsRetired && machine.DisplayName == "Retired"),
            "retirement remains a fact rather than silently filtering active roster");

        try
        {
            await new SqlMachineInventoryReader(connectionString, Guid.NewGuid()).ReadAsync();
            throw new InvalidOperationException("Wrong installation was accepted.");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("Installation identity does not match", StringComparison.Ordinal))
        {
            Check(true, "wrong installation rejected");
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.InitialCatalog = "master";
        try
        {
            await new SqlMachineInventoryReader(builder.ConnectionString, installationId).ReadAsync();
            throw new InvalidOperationException("System catalog was accepted.");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("independent V2", StringComparison.Ordinal))
        {
            Check(true, "system catalog rejected before SQL access");
        }

        return count;
    }
}
