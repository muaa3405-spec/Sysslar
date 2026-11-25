using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tasky.Services.Sync;

/// <summary>
/// Sync files kept in the user's own Google Drive, which Sysslar talks to itself after they sign in.
/// Nothing else on the computer has to be installed, and the files are the same encrypted ones a
/// sync folder would hold: Google stores them and cannot read them.
///
/// Drive has no folders in the way a disk does — a name is just a label, and two files in one folder
/// may share a name — so every sync file goes directly in one "Sysslar Sync" folder with its whole
/// name ("ws-abc/device.sysslarsync") as the label. Nothing nests, so two computers can never
/// disagree about which folder a file belongs in.
///
/// Only the drive.file permission is asked for, so Sysslar sees nothing in the Drive but the files
/// it made itself.
/// </summary>
public sealed class GoogleDriveStore : ISyncStore, IDisposable
{
    private const string FilesApi  = "https://www.googleapis.com/drive/v3/files";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly GoogleAuth _auth;
    private readonly SyncConfig _config;
    /// <summary>File name in the store → its Drive id, as far as this computer knows.</summary>
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private string? _folderId;

    public GoogleDriveStore(GoogleClientApp app, SyncConfig config, ISyncSecretStore secrets)
    {
        _config   = config;
        _folderId = config.DriveFolderId;
        _auth     = new GoogleAuth(app, secrets, "gdrive-" + config.DeviceId);
    }

    public string Describe => CloudFolders.GoogleDrive;

    /// <summary>Whether this computer still has a sign-in (otherwise the user has to connect again).</summary>
    public bool IsConnected => _auth.HasRefreshToken;

    /// <summary>Asks the user to allow Sysslar, in their browser.</summary>
    public Task ConnectAsync(Action<string>? showUrl = null, CancellationToken cancel = default)
        => _auth.SignInAsync(showUrl, cancel);

    public void Disconnect() => _auth.Forget();

    public void Dispose() { }

    // ── The store ────────────────────────────────────────────────────────

    public void Prepare() => Wait(PrepareAsync());

    public IReadOnlyList<SyncStoreFile> List() => Wait(ListAsync());

    public byte[]? Read(string name) => Wait(ReadAsync(name));

    public void Write(string name, byte[] content) => Wait(WriteAsync(name, content));

    public void Delete(string name) => Wait(DeleteAsync(name));

    public void SaveTo(SyncConfig config)
    {
        config.Folder        = null;
        config.Transport     = SyncTransports.GoogleDrive;
        config.DriveFolderId = _folderId;
    }

    /// <summary>
    /// The store interface is synchronous because the coordinator already calls it on a background
    /// thread, inside Task.Run. Waiting here therefore blocks no UI.
    /// </summary>
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private async Task<string> FolderIdAsync(CancellationToken cancel = default)
    {
        if (_folderId is { Length: > 0 } known) return known;

        // Another computer may have made the folder already. With drive.file we only ever see folders
        // Sysslar itself made, so a match here is always ours.
        var query = $"mimeType='{FolderMimeType}' and name='{SyncFolderLayout.RootName}' and trashed=false";
        var found = await GetJsonAsync($"{FilesApi}?q={Uri.EscapeDataString(query)}&fields=files(id,createdTime)&pageSize=100&spaces=drive", cancel);
        using (found)
        {
            var folders = found.RootElement.GetProperty("files").EnumerateArray()
                .Select(f => (Id: f.GetProperty("id").GetString()!, Created: f.TryGetProperty("createdTime", out var c) ? c.GetString() ?? "" : ""))
                .OrderBy(f => f.Created, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
            // If two computers made one each at the same moment, every computer picks the same one.
            if (folders.Count > 0) return Remember(folders[0].Id);
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"]     = SyncFolderLayout.RootName,
            ["mimeType"] = FolderMimeType,
        });
        using var created = await SendJsonAsync(HttpMethod.Post, $"{FilesApi}?fields=id",
                                                new StringContent(body, Encoding.UTF8, "application/json"), cancel);
        return Remember(created.RootElement.GetProperty("id").GetString()!);

        string Remember(string id)
        {
            _folderId = id;
            if (_config.DriveFolderId != id && _config.IsGoogleDrive)
            {
                _config.DriveFolderId = id;
                try { _config.Save(); } catch (Exception ex) { CrashLog.Write("The Drive folder couldn't be saved", ex); }
            }
            return id;
        }
    }

    private async Task PrepareAsync(CancellationToken cancel = default) => await FolderIdAsync(cancel);

    private async Task<IReadOnlyList<SyncStoreFile>> ListAsync(CancellationToken cancel = default)
    {
        try { return await ListOnceAsync(cancel); }
        catch (IOException ex) when (IsMissing(ex) && _folderId is not null)
        {
            // The "Sysslar Sync" folder was deleted or emptied in Drive: find or make it again.
            _folderId = null;
            _ids.Clear();
            return await ListOnceAsync(cancel);
        }
    }

    /// <summary>Whether Drive said the thing we named isn't there any more.</summary>
    private static bool IsMissing(IOException ex)
        => ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("no longer in the Drive folder", StringComparison.Ordinal);

    private async Task<IReadOnlyList<SyncStoreFile>> ListOnceAsync(CancellationToken cancel)
    {
        var folderId = await FolderIdAsync(cancel);
        var files = new List<SyncStoreFile>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            var query = $"'{folderId}' in parents and trashed=false";
            var url = $"{FilesApi}?q={Uri.EscapeDataString(query)}"
                    + "&fields=nextPageToken,files(id,name,size,modifiedTime,version)&pageSize=1000&spaces=drive"
                    + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var page = await GetJsonAsync(url, cancel);
            foreach (var file in page.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("name").GetString();
                var id   = file.GetProperty("id").GetString();
                if (name is null || id is null) continue;
                var version  = file.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                var length   = file.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var parsed) ? parsed : 0;
                var modified = file.TryGetProperty("modifiedTime", out var m) && m.GetString() is { } text
                    ? DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
                    : DateTime.UtcNow;

                // Two computers can't write one name, but a half-finished upload could leave a twin.
                // Keep the newest and let the cleanup remove the rest.
                if (seen.TryGetValue(name, out var existing))
                {
                    var older = files.FindIndex(f => f.Name == name);
                    if (older >= 0 && files[older].Modified >= modified) continue;
                    files.RemoveAt(older);
                }
                seen[name] = id;
                files.Add(new SyncStoreFile(name, version.Length > 0 ? version : modified.Ticks.ToString(), length, modified));
            }
            pageToken = page.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (pageToken is not null);

        // The listing is the whole truth: a file deleted in Drive, or by "Remove computer", must not
        // leave a stale id behind for the next read or write to fail on.
        _ids.Clear();
        foreach (var (name, id) in seen) _ids[name] = id;
        return files;
    }

    private async Task<string?> IdOfAsync(string name, CancellationToken cancel)
    {
        if (_ids.TryGetValue(name, out var known)) return known;
        var folderId = await FolderIdAsync(cancel);
        var query = $"'{folderId}' in parents and name='{Escape(name)}' and trashed=false";
        using var found = await GetJsonAsync($"{FilesApi}?q={Uri.EscapeDataString(query)}&fields=files(id)&pageSize=10&spaces=drive", cancel);
        var first = found.RootElement.GetProperty("files").EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined || first.GetProperty("id").GetString() is not { } id) return null;
        return _ids[name] = id;
    }

    private async Task<byte[]?> ReadAsync(string name, CancellationToken cancel = default)
    {
        if (await IdOfAsync(name, cancel) is not { } id) return null;
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{FilesApi}/{id}?alt=media"), cancel);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _ids.Remove(name);
            return null;
        }
        await ThrowIfFailedAsync(response, cancel);
        return await response.Content.ReadAsByteArrayAsync(cancel);
    }

    private async Task WriteAsync(string name, byte[] content, CancellationToken cancel = default)
    {
        if (await IdOfAsync(name, cancel) is { } id)
        {
            // One upload replaces the whole file at once, so another computer never reads half of it.
            using var updated = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch,
                $"{UploadApi}/{id}?uploadType=media&fields=id") { Content = Binary(content) }, cancel);
            if (updated.StatusCode != HttpStatusCode.NotFound)
            {
                await ThrowIfFailedAsync(updated, cancel);
                return;
            }
            // Deleted in Drive since we last looked: fall through and make it again.
            _ids.Remove(name);
        }

        var folderId = await FolderIdAsync(cancel);
        var metadata = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"]    = name,
            ["parents"] = new[] { folderId },
        });
        using var multipart = new MultipartContent("related")
        {
            new StringContent(metadata, Encoding.UTF8, "application/json"),
            Binary(content),
        };
        using var created = await SendJsonAsync(HttpMethod.Post, $"{UploadApi}?uploadType=multipart&fields=id", multipart, cancel);
        if (created.RootElement.TryGetProperty("id", out var newId) && newId.GetString() is { } value) _ids[name] = value;
    }

    private async Task DeleteAsync(string name, CancellationToken cancel = default)
    {
        if (await IdOfAsync(name, cancel) is not { } id) return;
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{FilesApi}/{id}"), cancel);
        _ids.Remove(name);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await ThrowIfFailedAsync(response, cancel);
    }

    private static ByteArrayContent Binary(byte[] content)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return part;
    }

    /// <summary>A name inside a Drive query: quotes and backslashes have to be escaped.</summary>
    private static string Escape(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");

    // ── Talking to Drive ─────────────────────────────────────────────────

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancel)
        => await SendJsonAsync(HttpMethod.Get, url, null, cancel);

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancel)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(method, url) { Content = content }, cancel);
        await ThrowIfFailedAsync(response, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        return body.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(body);
    }

    /// <summary>
    /// Sends a request with the current access token, and tries again when Drive asks us to wait
    /// (too many requests, or a hiccup at Google's end).
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken cancel)
    {
        const int attempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            var token = await _auth.AccessTokenAsync(cancel);
            var request = make();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                if (attempt >= attempts) throw new SyncStoreUnreachableException("Sysslar couldn't reach Google Drive. Your changes are saved here.", ex);
                await Task.Delay(Backoff(attempt), cancel);
                continue;
            }

            bool retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                          or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                          || (response.StatusCode == HttpStatusCode.Forbidden && await IsRateLimitAsync(response, cancel));
            if (!retryable || attempt >= attempts) return response;
            var wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
            response.Dispose();
            await Task.Delay(wait, cancel);
        }
    }

    private static TimeSpan Backoff(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(16000, 500 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(0, 400));

    private static async Task<bool> IsRateLimitAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        // Drive answers 403 both for "slow down" and for "you may not": only the first is worth retrying.
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancel);
            return body.Contains("rateLimitExceeded", StringComparison.Ordinal)
                || body.Contains("userRateLimitExceeded", StringComparison.Ordinal)
                || body.Contains("sharingRateLimitExceeded", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancel);
        var message = DescribeError(body);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                new SyncStoreSignedOutException("Google ended this computer's sign-in. Sign in again to keep syncing."),
            HttpStatusCode.Forbidden when body.Contains("storageQuotaExceeded", StringComparison.Ordinal) =>
                new IOException("Your Google Drive is full, so Sysslar couldn't save the sync files."),
            HttpStatusCode.Forbidden =>
                new IOException("Google Drive refused the request: " + message),
            HttpStatusCode.NotFound =>
                new IOException("That sync file is no longer in the Drive folder."),
            _ => new IOException($"Google Drive answered {(int)response.StatusCode}: {message}"),
        };
    }

    private static string DescribeError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var m) && m.GetString() is { } text) return text;
                if (error.GetString() is { } simple) return simple;
            }
        }
        catch (JsonException) { /* not JSON */ }
        return body.Length > 200 ? body[..200] : body;
    }
}
