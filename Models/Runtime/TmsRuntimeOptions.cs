namespace Slh.Tms.Api.Models.Runtime;

public sealed class TmsRuntimeOptions
{
    public string EnvironmentName { get; init; } = "Development";
    public string DataPath { get; init; } = "";
    public string ExportPath { get; init; } = "";
    public string BackupPath { get; init; } = "";
    public string LoggingPath { get; init; } = "";
    public Dictionary<string, bool> FeatureFlags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TmsWorkerOptions
{
    public bool DotTrackingIngestion { get; init; } = true;
    public bool IntegrationBackgroundSync { get; init; } = true;
    public bool MailboxOrderIngestion { get; init; } = false;
    public bool EtaSnapshotCapture { get; init; } = false;
}

public sealed class MicrosoftGraphEmailOptions
{
    public bool Enabled { get; init; }
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ManualSendMode { get; init; } = "DelegatedUser";
    public string AutomatedSendMode { get; init; } = "SharedMailbox";
    public string SharedSenderUpn { get; init; } = "";
    public string DefaultInfoMailbox { get; init; } = "info@lyonshaulage.com";
    public bool SaveToSentItems { get; init; } = true;
    public bool AuditMessageId { get; init; } = true;

    public bool IsReadyForManualDelegatedSend =>
        Enabled &&
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        ManualSendMode.Equals("DelegatedUser", StringComparison.OrdinalIgnoreCase);

    public bool IsReadyForAutomatedSharedSend =>
        Enabled &&
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(SharedSenderUpn) &&
        AutomatedSendMode.Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase);
}
