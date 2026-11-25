using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tasky.Services.Sync;

/// <summary>The Google app Sysslar signs in as. Empty when this build wasn't given one.</summary>
public sealed record GoogleClientApp(string ClientId, string ClientSecret)
{
    /// <summary>
    /// Only files Sysslar itself creates. Google calls this a non-sensitive scope: it cannot see,
    /// read or change anything else in the user's Drive, so Sysslar never asks for that power.
    /// </summary>
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

    /// <summary>
    /// The client of this build. It comes from Tasky/google-client.json (kept out of the repository)
    /// or, for a development run, from SYSSLAR_GOOGLE_CLIENT_ID and SYSSLAR_GOOGLE_CLIENT_SECRET.
    /// Null means this build can't offer Google Drive, and the option stays hidden.
    /// </summary>
    public static GoogleClientApp? Current { get; } = Load();

    private static GoogleClientApp? Load()
    {
        try
        {
            var id     = Environment.GetEnvironmentVariable("SYSSLAR_GOOGLE_CLIENT_ID");
            var secret = Environment.GetEnvironmentVariable("SYSSLAR_GOOGLE_CLIENT_SECRET");
            if (id is { Length: > 0 } && secret is { Length: > 0 }) return new(id, secret);

            using var stream = typeof(GoogleClientApp).Assembly.GetManifestResourceStream("google-client.json");
            if (stream is null) return null;
            using var doc = JsonDocument.Parse(stream);
            // The file Google hands out has everything under "installed".
            var root = doc.RootElement.TryGetProperty("installed", out var installed) ? installed : doc.RootElement;
            var fileId     = root.TryGetProperty("client_id", out var a) ? a.GetString() : null;
            var fileSecret = root.TryGetProperty("client_secret", out var b) ? b.GetString() : null;
            return fileId is { Length: > 0 } && fileSecret is { Length: > 0 } ? new(fileId, fileSecret) : null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("The Google client settings of this build couldn't be read", ex);
            return null;
        }
    }
}

/// <summary>
/// Signing in to Google from a desktop app, the way Google asks installed apps to do it: the browser
/// sends the answer back to a one-off web server on this computer (127.0.0.1 on a free port), and the
/// request is tied to a secret this app just made up (PKCE), so another program on the computer can't
/// use the answer.
///
/// Only the refresh token is kept, in the same key store as the sync key. The access token lives in
/// memory and is renewed when it runs out.
/// </summary>
public sealed class GoogleAuth(GoogleClientApp app, ISyncSecretStore secrets, string account)
{
    private const string AuthEndpoint  = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };

    private string? _accessToken;
    private DateTime _accessUntil = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Whether this computer has a sign-in it can still use.</summary>
    public bool HasRefreshToken => ReadRefreshToken() is not null;

    private string? ReadRefreshToken()
    {
        try
        {
            var bytes = secrets.Read(account);
            return bytes is null || bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        catch (Exception ex)
        {
            CrashLog.Write("The Google sign-in couldn't be read from this computer's key store", ex);
            return null;
        }
    }

    private void SaveRefreshToken(string token) => secrets.Write(account, Encoding.UTF8.GetBytes(token));

    public void Forget()
    {
        _accessToken = null;
        _accessUntil = DateTime.MinValue;
        try { secrets.Delete(account); }
        catch (Exception ex) { CrashLog.Write("The Google sign-in couldn't be removed from the key store", ex); }
    }

    /// <summary>
    /// Opens the browser, waits for the user to allow Sysslar, and keeps the sign-in.
    /// Returns the address shown in the browser, for a message if it didn't open by itself.
    /// </summary>
    public async Task SignInAsync(Action<string>? showUrl = null, CancellationToken cancel = default)
    {
        var verifier  = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state     = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        var port = StartOnFreePort(listener);
        var redirect = $"http://127.0.0.1:{port}/";

        var url = AuthEndpoint
                + "?client_id=" + Uri.EscapeDataString(app.ClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                + "&response_type=code"
                + "&scope=" + Uri.EscapeDataString(GoogleClientApp.DriveFileScope)
                + "&code_challenge=" + challenge
                + "&code_challenge_method=S256"
                + "&state=" + state
                + "&access_type=offline"
                + "&prompt=consent";

        showUrl?.Invoke(url);
        Helpers.FileLauncher.OpenUrl(url);

        string code;
        try
        {
            code = await WaitForCodeAsync(listener, state, cancel);
        }
        finally
        {
            try { listener.Stop(); } catch { /* already closing */ }
        }

        var tokens = await PostAsync(new Dictionary<string, string>
        {
            ["client_id"]     = app.ClientId,
            ["client_secret"] = app.ClientSecret,
            ["code"]          = code,
            ["code_verifier"] = verifier,
            ["grant_type"]    = "authorization_code",
            ["redirect_uri"]  = redirect,
        }, cancel);

        if (tokens.RootElement.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { Length: > 0 } token)
            SaveRefreshToken(token);
        else
            throw new SyncStoreSignedOutException("Google didn't return a lasting sign-in. Try connecting again.");

        Remember(tokens);
    }

    /// <summary>A usable access token, renewed when needed.</summary>
    public async Task<string> AccessTokenAsync(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            if (_accessToken is { } current && DateTime.UtcNow < _accessUntil) return current;
            if (ReadRefreshToken() is not { } refresh)
                throw new SyncStoreSignedOutException("Sysslar isn't signed in to Google on this computer.");

            JsonDocument tokens;
            try
            {
                tokens = await PostAsync(new Dictionary<string, string>
                {
                    ["client_id"]     = app.ClientId,
                    ["client_secret"] = app.ClientSecret,
                    ["refresh_token"] = refresh,
                    ["grant_type"]    = "refresh_token",
                }, cancel);
            }
            catch (GoogleAuthRefusedException ex)
            {
                // The user took the permission back, changed their password, or the sign-in expired.
                Forget();
                throw new SyncStoreSignedOutException("Google ended this computer's sign-in. Sign in again to keep syncing.", ex);
            }
            Remember(tokens);
            return _accessToken!;
        }
        finally { _gate.Release(); }
    }

    private void Remember(JsonDocument tokens)
    {
        _accessToken = tokens.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        var seconds  = tokens.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
        // Renew a minute early, so a request never starts with a token that dies mid-flight.
        _accessUntil = DateTime.UtcNow.AddSeconds(Math.Max(30, seconds - 60));
        tokens.Dispose();
        if (_accessToken is null) throw new SyncStoreSignedOutException("Google didn't return an access token.");
    }

    private static async Task<JsonDocument> PostAsync(Dictionary<string, string> form, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, cancel); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new SyncStoreUnreachableException("Sysslar couldn't reach Google. Check the connection.", ex);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancel);
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(body);
            // 400 invalid_grant, 401: the sign-in itself is no good. Anything else may work later.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                throw new GoogleAuthRefusedException("Google refused the sign-in: " + Describe(body));
            throw new IOException($"Google's sign-in service answered {(int)response.StatusCode}: " + Describe(body));
        }
    }

    private static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_description", out var d) && d.GetString() is { } text) return text;
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { } code) return code;
        }
        catch (JsonException) { /* not JSON: fall through */ }
        return body.Length > 200 ? body[..200] : body;
    }

    private static int StartOnFreePort(HttpListener listener)
    {
        // Google lets an installed app use any loopback port, so take whichever one is free.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var port = Random.Shared.Next(49152, 65535);
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                return port;
            }
            catch (HttpListenerException) { /* taken: try another */ }
            catch (SocketException) { /* taken: try another */ }
        }
        throw new IOException("Sysslar couldn't open a port to receive Google's answer.");
    }

    private static async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken cancel)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                var waiting = listener.GetContextAsync();
                var finished = await Task.WhenAny(waiting, Task.Delay(Timeout.Infinite, cancel));
                if (finished != waiting) throw new OperationCanceledException(cancel);
                context = await waiting;
            }
            catch (HttpListenerException ex)
            {
                throw new IOException("Waiting for Google's answer failed.", ex);
            }

            var query = context.Request.QueryString;
            var code  = query["code"];
            var error = query["error"];
            var given = query["state"];

            if (code is null && error is null)
            {
                // Something else on the computer knocked (favicon, a stray request): keep waiting.
                Answer(context, 404, "Not found");
                continue;
            }
            if (given != state)
            {
                Answer(context, 400, "<h1>Sysslar</h1><p>That answer didn't come from the sign-in Sysslar started. Nothing was changed.</p>");
                throw new SyncStoreSignedOutException("The answer from the browser didn't match the sign-in Sysslar started.");
            }
            if (error is not null)
            {
                Answer(context, 200, "<h1>Sysslar</h1><p>Sysslar was not connected. You can close this tab.</p>");
                throw new OperationCanceledException(error == "access_denied"
                    ? "You didn't allow Sysslar to use Google Drive."
                    : "Google reported: " + error);
            }
            Answer(context, 200, "<h1>Sysslar</h1><p>Sysslar is connected to your Google Drive. You can close this tab and go back to Sysslar.</p>");
            return code!;
        }
    }

    private static void Answer(HttpListenerContext context, int status, string html)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=\"utf-8\"><title>Sysslar</title>" +
                "<body style=\"font:16px -apple-system,Segoe UI,sans-serif;max-width:32em;margin:12vh auto;padding:0 1em\">" + html);
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.OutputStream.Close();
        }
        catch (Exception ex) { CrashLog.Write("Answering the browser after signing in failed", ex); }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Google said no to the sign-in itself, so it has to be made again.</summary>
    private sealed class GoogleAuthRefusedException(string message) : Exception(message);
}
