using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models.Runtime;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/runtime")]
[Authorize]
public sealed class RuntimeController(
    TmsRuntimeOptions runtime,
    TmsWorkerOptions workers,
    MicrosoftGraphEmailOptions graphEmail,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        environment = runtime.EnvironmentName,
        databaseConfigured = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("TmsDb")),
        paths = new
        {
            data = PathState(runtime.DataPath),
            exports = PathState(runtime.ExportPath),
            backups = PathState(runtime.BackupPath),
            logs = PathState(runtime.LoggingPath)
        },
        workers,
        integrations = new
        {
            microsoftGraphEmail = new
            {
                graphEmail.Enabled,
                graphEmail.ManualSendMode,
                graphEmail.AutomatedSendMode,
                graphEmail.DefaultInfoMailbox,
                graphEmail.SaveToSentItems,
                graphEmail.AuditMessageId,
                manualDelegatedSendReady = graphEmail.IsReadyForManualDelegatedSend,
                automatedSharedSendReady = graphEmail.IsReadyForAutomatedSharedSend
            }
        },
        featureFlags = runtime.FeatureFlags
    });

    private static object PathState(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new { configured = false, writable = false };
        try
        {
            Directory.CreateDirectory(path);
            return new { configured = true, writable = true };
        }
        catch
        {
            return new { configured = true, writable = false };
        }
    }
}
