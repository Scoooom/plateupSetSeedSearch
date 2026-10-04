namespace PlateUpSeedTools.Web.Services;

/// <summary>
/// VAPID credentials for Web Push (see PushNotificationSender). Deliberately never given defaults
/// in appsettings.json - unlike CacheOptions' SqlitePath, these are credentials, not operational
/// config, so they're supplied via `dotnet user-secrets` in dev and WebPush__PublicKey/
/// WebPush__PrivateKey/WebPush__Subject env vars in production (same `__`-binding convention
/// Docker already uses for CacheStore__SqlitePath). All three empty/unset is a supported, fully
/// degraded state - see PushNotificationSender.IsConfigured.
/// </summary>
public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";

    // A contact URL/mailto required by the VAPID spec (RFC 8292) so a push service can reach the
    // sender if a subscription is being misused - not secret, but still environment-specific, kept
    // in this same options class rather than appsettings.json for consistency with its two siblings.
    public string Subject { get; set; } = "";
}
