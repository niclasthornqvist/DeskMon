using System;
using System.Linq;
using SteamKit2;

namespace DeskMon.Notes;

/// <summary>
/// A game's library logo, from Steam's app info - what the Steam client reads
/// its library art from. Newer games keep the logo under a hashed address
/// (store_item_assets\...\&lt;hash&gt;\logo.png) that the store's web API and
/// pages never mention; only the app info has it.
///
/// Asked through Steam's own servers with an anonymous login - no account,
/// nothing of the user's sent - which is what SteamCMD and SteamDB use for
/// public app info. One short connection per game, and only for a logo that
/// wasn't found any other way; GameArt keeps what it finds.
/// </summary>
internal static class SteamAppInfo
{
    /// <summary>The whole exchange - connect, log on, ask, hang up - normally takes a second or two.</summary>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The logo's path under the app's store_item_assets folder, as
    /// "ff87…/logo.png" - the English one, else the first language there is.
    /// Null when the app has none. Throws when Steam couldn't be reached or
    /// didn't answer, so the caller doesn't remember that as "no logo".
    /// Blocks until it knows, so it is for a worker thread.
    /// </summary>
    public static string? LogoPath(string appId)
        => Common(appId, common =>
        {
            var images = common["library_assets_full"]["library_logo"]["image"];
            string? english = images["english"].Value;
            return !string.IsNullOrEmpty(english)
                ? english
                : images.Children.Select(c => c.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        });

    /// <summary>
    /// The hash naming the game's small square icon - the one Steam's library
    /// lists it with - on Steam's community image server. Null when the app
    /// has none; throws when Steam couldn't be reached, as LogoPath does.
    /// </summary>
    public static string? IconHash(string appId)
        => Common(appId, common => common["icon"].Value is { Length: > 0 } hash ? hash : null);

    /// <summary>One value from the app's "common" section, by the anonymous exchange described above.</summary>
    private static string? Common(string appId, Func<KeyValue, string?> pick)
    {
        if (!uint.TryParse(appId, out uint id))
            return null;

        var client = new SteamClient();
        var callbacks = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;
        var apps = client.GetHandler<SteamApps>()!;

        string? path = null;
        bool answered = false, finished = false;

        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => user.LogOnAnonymous());
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => finished = true);
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(async logon =>
        {
            try
            {
                if (logon.Result != EResult.OK)
                    return;

                // Some apps' info is only given with an access token; anonymous
                // users get one for any public app.
                var tokens = await apps.PICSGetAccessTokens(id, null);
                tokens.AppTokens.TryGetValue(id, out ulong token);

                var info = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(id, token), null);
                var app = info.Results?.SelectMany(r => r.Apps.Values).FirstOrDefault(a => a.ID == id);
                answered = true;
                if (app is null)
                    return;

                path = pick(app.KeyValues["common"]);
            }
            catch
            {
                // Left unanswered: thrown below as not reached.
            }
            finally
            {
                client.Disconnect();
            }
        });

        try
        {
            client.Connect();
            var until = DateTime.UtcNow + GiveUpAfter;
            while (!finished && DateTime.UtcNow < until)
                callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            if (!finished)
                client.Disconnect();
        }

        return answered ? path : throw new TimeoutException("Steam's app info didn't answer.");
    }
}
