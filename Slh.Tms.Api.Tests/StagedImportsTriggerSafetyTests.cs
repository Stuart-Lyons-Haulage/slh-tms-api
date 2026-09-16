namespace Slh.Tms.Api.Tests;

public sealed class StagedImportsTriggerSafetyTests
{
    [Fact]
    public void DbContext_disables_sql_server_output_for_triggered_staged_imports_table()
    {
        var source = File.ReadAllText(Path.Combine("..", "Data", "TmsDbContext.cs"));

        Assert.Contains("ToTable(\"StagedImports\", table => table.UseSqlOutputClause(false))", source);
        Assert.Contains("IX_StagedImports_Entity_Status_ReceivedAtUtc", source);
    }

    [Fact]
    public void Migration_adds_operational_staging_queue_index_without_destructive_table_changes()
    {
        var migration = File.ReadAllText(Path.Combine("..", "Migrations", "20260916111500_StagedImportsTriggerSafeAndOperationalIndex.cs"));

        Assert.Contains("CREATE INDEX [IX_StagedImports_Entity_Status_ReceivedAtUtc]", migration);
        Assert.Contains("IF NOT EXISTS", migration);
        Assert.DoesNotContain("DROP TABLE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM [dbo].[StagedImports]", migration, StringComparison.OrdinalIgnoreCase);
    }
}
