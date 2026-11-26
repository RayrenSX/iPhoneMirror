using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using IPhoneMirror.App.Services.ExplorerMapping;

internal static class ExplorerStorageTests
{
    internal static async Task Run()
    {
        const long total = 256_000_000_000;
        var available = 175_450_000_000L;
        var reads = 0;
        var unavailable = false;
        Task<JsonElement> Request(string action, object? arguments, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (action == "explorer_roots") return Task.FromResult(JsonSerializer.SerializeToElement(new
            { roots = new[] { new { root = "media" } }, total = 0, nextOffset = (int?)null }));
            if (action == "device_info")
            {
                reads++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                { disk = new { available = !unavailable, values = new { TotalDiskCapacity = total, TotalDataCapacity = 246_200_000_000L, TotalDataAvailable = available } } }));
            }
            var args = JsonSerializer.SerializeToElement(arguments);
            var path = args.GetProperty("path").GetString()!;
            if (action == "make_directory") return Task.FromResult(JsonSerializer.SerializeToElement(new { status = "created" }));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { path, name = path[(path.LastIndexOf('/') + 1)..], type = "directory", size = 0, modified = 1 }));
        }
        Task<DeviceHubExplorerRootStore> Open() => DeviceHubExplorerRootStore.CreateAsync(Request, "Phone", "Media", "Crashes", "Temp", "Logs", default);
        var store = await Open();
        await using var server = ExplorerWebDavServer.Start(store);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        async Task<XDocument> Query(string body)
        {
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), server.BaseUri);
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.MultiStatus, "Storage PROPFIND failed.");
            return XDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        const string quota = "<D:propfind xmlns:D='DAV:'><D:prop><D:quota-available-bytes/><D:quota-used-bytes/></D:prop></D:propfind>";
        XNamespace dav = "DAV:";
        var all = await Query("<D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>");
        Require(reads == 0 && !all.Descendants(dav + "quota-available-bytes").Any(), "Ordinary cached directory display queried storage or included quota in allprop.");
        var result = await Query(quota);
        Require(result.Descendants(dav + "quota-available-bytes").Single().Value == available.ToString() &&
            result.Descendants(dav + "quota-used-bytes").Single().Value == (total - available).ToString(),
            "Quota used the local cache disk, data-partition capacity or rounded storage values.");
        await Query(quota);
        Require(reads == 1, "Repeated capacity queries did not reuse the short-lived phone snapshot.");
        var names = await Query("<D:propfind xmlns:D='DAV:'><D:propname/></D:propfind>");
        Require(names.Descendants(dav + "quota-available-bytes").Single().Value.Length == 0, "Quota propname returned property values.");

        await store.MakeDirectoryAsync("Media/new", default);
        available = 0;
        result = await Query(quota);
        Require(reads == 2 && result.Descendants(dav + "quota-available-bytes").Single().Value == "0" &&
            result.Descendants(dav + "quota-used-bytes").Single().Value == total.ToString(),
            "A write did not invalidate free-space metadata, or an actually full phone was replaced with a fallback.");

        unavailable = true;
        var unknown = await Open();
        Require(await unknown.GetStorageAsync(default) is null, "Unavailable phone capacity was invented.");
        unavailable = false;
        available = total + 1;
        await ExpectIOException(() => unknown.GetStorageAsync(default));
        available = -1;
        await ExpectIOException(() => unknown.GetStorageAsync(default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await store.GetStorageAsync(cancelled.Token); throw new InvalidOperationException("Cancelled capacity lookup succeeded."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS phone storage: exact physical total/free bytes, WebDAV quotas, zero free space, metadata-only listing, bounded reuse, write invalidation and invalid-data rejection.");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task ExpectIOException(Func<Task> operation)
    {
        try { await operation(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Invalid capacity was accepted.");
    }
}
