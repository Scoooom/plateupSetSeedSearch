using System.Text.Json;
using Microsoft.Extensions.Options;
using PlateUpSeedTools.Web.Contracts;
using WebPush;

namespace PlateUpSeedTools.Web.Services;

/// <summary>
/// Thin wrapper over the WebPush package (web-push-libs/web-push-csharp) - sends exactly one kind
/// of notification, "this search job finished," to exactly one subscription at a time (a job's own
/// PushSubscriptionDto - see SearchJob.PushSubscription). Never throws: a dead/expired subscription
/// (WebPushException) or a misconfigured server is logged and swallowed, since a push failure must
/// never take down the job whose completion it was reporting.
/// </summary>
public sealed class PushNotificationSender
{
    private readonly VapidDetails? _vapidDetails;
    private readonly WebPushClient _client = new();
    private readonly ILogger<PushNotificationSender> _logger;

    public bool IsConfigured { get; }
    public string? PublicKey { get; }

    public PushNotificationSender(IOptions<WebPushOptions> options, ILogger<PushNotificationSender> logger)
    {
        _logger = logger;
        var o = options.Value;
        IsConfigured = !string.IsNullOrWhiteSpace(o.PublicKey)
            && !string.IsNullOrWhiteSpace(o.PrivateKey)
            && !string.IsNullOrWhiteSpace(o.Subject);

        if (IsConfigured)
        {
            PublicKey = o.PublicKey;
            _vapidDetails = new VapidDetails(o.Subject, o.PublicKey, o.PrivateKey);
        }
    }

    public async Task SendCompletionPushAsync(string jobId, PushSubscriptionDto subscriptionDto, string title, string body, string url, CancellationToken cancellationToken)
    {
        if (!IsConfigured || _vapidDetails is null)
        {
            return;
        }

        var subscription = new PushSubscription(subscriptionDto.Endpoint, subscriptionDto.P256dh, subscriptionDto.Auth);
        var payload = JsonSerializer.Serialize(new { title, body, url, jobId });

        try
        {
            await _client.SendNotificationAsync(subscription, payload, _vapidDetails, cancellationToken);
        }
        catch (WebPushException ex)
        {
            // The subscription is gone (browser uninstalled/revoked permission/push service
            // expired it) or the push service rejected the request - nothing to retry, nothing
            // for the job itself to do differently. Just log and move on.
            _logger.LogInformation(ex, "Push notification for search job {JobId} failed ({StatusCode}).", jobId, ex.StatusCode);
        }
    }
}
