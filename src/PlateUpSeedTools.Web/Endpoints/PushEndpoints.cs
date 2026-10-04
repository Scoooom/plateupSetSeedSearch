using PlateUpSeedTools.Web.Contracts;
using PlateUpSeedTools.Web.Services;

namespace PlateUpSeedTools.Web.Endpoints;

public static class PushEndpoints
{
    public static void MapPushEndpoints(this WebApplication app)
    {
        app.MapGet("/api/push/public-key", GetPublicKey);
    }

    // The client feature-detects Push support itself, but also needs to know the server actually
    // has WebPush:* configured before it reveals the "Notify on completion" checkbox at all - see
    // PushNotificationSender.IsConfigured.
    private static IResult GetPublicKey(PushNotificationSender sender) =>
        Results.Ok(new VapidPublicKeyResponse(sender.IsConfigured, sender.PublicKey));
}
