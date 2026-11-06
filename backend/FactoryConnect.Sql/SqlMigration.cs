using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace FactoryConnect.Sql;

public sealed record SqlMigration(string Id, string Sql)
{
    public byte[] Checksum => SHA256.HashData(Encoding.UTF8.GetBytes(Sql));

    public static IReadOnlyList<SqlMigration> BuiltIn()
    {
        var assembly = typeof(SqlMigration).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return new SqlMigration(name[(name.IndexOf(".Migrations.", StringComparison.Ordinal) + 12)..^4], reader.ReadToEnd());
            }).ToArray();
    }
}
