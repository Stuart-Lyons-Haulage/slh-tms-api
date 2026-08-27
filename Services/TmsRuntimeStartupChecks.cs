using Slh.Tms.Api.Models.Runtime;

namespace Slh.Tms.Api.Services;

public static class TmsRuntimeStartupChecks
{
    public static void Verify(TmsRuntimeOptions options, ILogger logger)
    {
        EnsurePath("data", options.DataPath, logger);
        EnsurePath("export", options.ExportPath, logger);
        EnsurePath("backup", options.BackupPath, logger);
        EnsurePath("logging", options.LoggingPath, logger);
    }

    private static void EnsurePath(string label, string path, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogWarning("TMS runtime {PathLabel} path is not configured.", label);
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".slh-tms-{label}-write-test");
            File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(probe);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "TMS runtime {PathLabel} path {Path} is not writable.", label, path);
        }
    }
}
