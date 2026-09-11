namespace Slh.Tms.MasterDataSync;

public sealed class SyncOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Hostname { get; set; } = "stuartlyonshaulage.sharepoint.com";
    public string SitePath { get; set; } = "";
    public string SqlConnectionString { get; set; } = "";
    public string DeadLetterQueueName { get; set; } = "master-data-sync-dead-letter";
    public string TmsCacheInvalidateUrl { get; set; } = "";
    public string TmsCacheInvalidateToken { get; set; } = "";

    public void ApplyDefaults()
    {
        TenantId = Environment.GetEnvironmentVariable("MasterDataSync__TenantId") ?? TenantId;
        ClientId = Environment.GetEnvironmentVariable("MasterDataSync__ClientId") ?? ClientId;
        ClientSecret = Environment.GetEnvironmentVariable("MasterDataSync__ClientSecret") ?? ClientSecret;
        Hostname = Environment.GetEnvironmentVariable("MasterDataSync__Hostname") ?? Hostname;
        SitePath = Environment.GetEnvironmentVariable("MasterDataSync__SitePath") ?? SitePath;
        SqlConnectionString = Environment.GetEnvironmentVariable("MasterDataSync__SqlConnectionString") ?? SqlConnectionString;
        DeadLetterQueueName = Environment.GetEnvironmentVariable("MasterDataSync__DeadLetterQueueName") ?? DeadLetterQueueName;
        TmsCacheInvalidateUrl = Environment.GetEnvironmentVariable("MasterDataSync__TmsCacheInvalidateUrl") ?? TmsCacheInvalidateUrl;
        TmsCacheInvalidateToken = Environment.GetEnvironmentVariable("MasterDataSync__TmsCacheInvalidateToken") ?? TmsCacheInvalidateToken;
    }
}
