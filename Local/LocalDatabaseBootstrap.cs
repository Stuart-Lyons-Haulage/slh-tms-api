using Microsoft.Data.SqlClient;

namespace Slh.Tms.Api.Local;

public static class LocalDatabaseBootstrap
{
    public static async Task EnsureCreatedAsync(IConfiguration configuration, ILogger logger, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("LocalTest:Enabled")) return;

        var configured = configuration.GetConnectionString("TmsDb");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Local test mode requires ConnectionStrings:TmsDb.");

        var builder = new SqlConnectionStringBuilder(configured);
        var databaseName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "SLH_TMS_LOCAL" : builder.InitialCatalog;
        if (!databaseName.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'))
            throw new InvalidOperationException("Local database name contains unsupported characters.");

        var dataRoot = configuration["LocalStorage:DatabaseRoot"] ??
                       Path.Combine(AppContext.BaseDirectory, "local-data", "data");
        dataRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(dataRoot);

        var mdf = Path.Combine(dataRoot, $"{databaseName}.mdf");
        var ldf = Path.Combine(dataRoot, $"{databaseName}_log.ldf");

        var masterBuilder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "master",
            AttachDBFilename = string.Empty
        };

        await using var connection = new SqlConnection(masterBuilder.ConnectionString);
        await connection.OpenAsync(ct);

        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(1) FROM sys.databases WHERE name = @name;";
        exists.Parameters.AddWithValue("@name", databaseName);
        var count = Convert.ToInt32(await exists.ExecuteScalarAsync(ct));
        if (count > 0) return;

        var escapedMdf = mdf.Replace("'", "''", StringComparison.Ordinal);
        var escapedLdf = ldf.Replace("'", "''", StringComparison.Ordinal);
        var quotedName = databaseName.Replace("]", "]]", StringComparison.Ordinal);

        await using var create = connection.CreateCommand();
        create.CommandText = $"""
CREATE DATABASE [{quotedName}]
ON PRIMARY (NAME = N'{quotedName}', FILENAME = N'{escapedMdf}')
LOG ON (NAME = N'{quotedName}_log', FILENAME = N'{escapedLdf}');
""";
        await create.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Created contained local test database {DatabaseName} under {DataRoot}.", databaseName, dataRoot);
    }
}
