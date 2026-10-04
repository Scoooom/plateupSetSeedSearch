namespace PlateUpSeedTools.Web.Contracts;

/// <summary>
/// The browser's PushSubscription, shaped for JSON - see https://developer.mozilla.org/en-US/docs/Web/API/PushSubscription/toJSON.
/// Registered against a specific job (POST /api/search/{jobId}/push-subscription) rather than any
/// account/identity system - there are none in this app, and none are needed: the subscription IS
/// the "identity," scoped to the one job it was created for.
/// </summary>
public sealed record PushSubscriptionDto(string Endpoint, string P256dh, string Auth);

/// <summary>
/// Lets the client know whether to even offer the "Notify on completion" checkbox - Configured is
/// false whenever WebPush:PublicKey/PrivateKey/Subject aren't all set server-side (see
/// PushNotificationSender.IsConfigured), in which case PublicKey is null and the client should hide
/// the checkbox entirely rather than let a user subscribe to a server that can never send anything.
/// </summary>
public sealed record VapidPublicKeyResponse(bool Configured, string? PublicKey);
