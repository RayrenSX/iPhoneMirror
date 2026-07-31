using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace IPhoneMirror.Shared.Networking;

internal sealed record SegmentedDownloadOptions(
    long MaximumBytes,
    long? ExpectedBytes = null,
    int MaximumConcurrency = 6,
    long MinimumSegmentBytes = 4L * 1024 * 1024,
    int BufferSize = 128 * 1024);

internal sealed record SegmentedDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond,
    int SegmentCount);

internal sealed record SegmentedDownloadResult(
    long BytesReceived,
    int SegmentCount);

internal static class SegmentedHttpDownloader
{
    private sealed class RangeNotSupportedException : Exception
    {
        internal RangeNotSupportedException(string message) : base(message) { }
    }

    internal static async Task<SegmentedDownloadResult> DownloadAsync(
        HttpClient client,
        Uri uri,
        string destination,
        SegmentedDownloadOptions options,
        Func<Uri, bool> isTrustedFinalUri,
        IProgress<SegmentedDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isTrustedFinalUri);
        if (options.MaximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumBytes));
        if (options.ExpectedBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.ExpectedBytes));
        if (options.ExpectedBytes > options.MaximumBytes)
            throw new InvalidDataException("The expected download exceeds the size limit.");
        if (options.MaximumConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumConcurrency));
        if (options.MinimumSegmentBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumSegmentBytes));
        if (options.BufferSize < 4096)
            throw new ArgumentOutOfRangeException(nameof(options.BufferSize));

        using var probeRequest = CreateRequest(uri, 0, 0);
        using var probeResponse = await client.SendAsync(probeRequest,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var resolvedUri = ValidateFinalUri(probeResponse, isTrustedFinalUri);

        if (probeResponse.StatusCode == HttpStatusCode.OK)
            return await WriteSingleResponseAsync(probeResponse, destination,
                options, progress, cancellationToken);

        if (probeResponse.StatusCode != HttpStatusCode.PartialContent)
        {
            probeResponse.EnsureSuccessStatusCode();
            throw new InvalidDataException("The download server returned an unexpected status.");
        }

        var totalBytes = ValidateContentRange(probeResponse, 0, 0, null);
        ValidateTotalBytes(totalBytes, options);
        var segmentCount = CalculateSegmentCount(totalBytes, options);
        if (segmentCount <= 1)
        {
            probeResponse.Dispose();
            return await DownloadSingleAsync(client, resolvedUri, destination, options,
                isTrustedFinalUri, progress, cancellationToken);
        }
        probeResponse.Dispose();

        try
        {
            return await DownloadSegmentsAsync(client, resolvedUri, destination, totalBytes,
                segmentCount, options, isTrustedFinalUri, progress, cancellationToken);
        }
        catch (RangeNotSupportedException)
        {
            TryDelete(destination);
            return await DownloadSingleAsync(client, resolvedUri, destination, options,
                isTrustedFinalUri, progress, cancellationToken);
        }
    }

    private static int CalculateSegmentCount(long totalBytes,
        SegmentedDownloadOptions options)
    {
        var possibleSegments = (int)Math.Min(int.MaxValue,
            (totalBytes + options.MinimumSegmentBytes - 1) /
            options.MinimumSegmentBytes);
        return Math.Clamp(possibleSegments, 1, options.MaximumConcurrency);
    }

    private static async Task<SegmentedDownloadResult> DownloadSingleAsync(
        HttpClient client,
        Uri uri,
        string destination,
        SegmentedDownloadOptions options,
        Func<Uri, bool> isTrustedFinalUri,
        IProgress<SegmentedDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri);
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ValidateFinalUri(response, isTrustedFinalUri);
        response.EnsureSuccessStatusCode();
        return await WriteSingleResponseAsync(response, destination, options,
            progress, cancellationToken);
    }

    private static async Task<SegmentedDownloadResult> WriteSingleResponseAsync(
        HttpResponseMessage response,
        string destination,
        SegmentedDownloadOptions options,
        IProgress<SegmentedDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (options.ExpectedBytes is { } expected &&
            contentLength is > 0 && contentLength != expected)
            throw new InvalidDataException(
                "The download size does not match the expected size.");
        var totalBytes = options.ExpectedBytes ?? contentLength;
        if (totalBytes is > 0) ValidateTotalBytes(totalBytes.Value, options);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, options.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[options.BufferSize];
        var stopwatch = Stopwatch.StartNew();
        long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            received = checked(received + count);
            if (received > options.MaximumBytes)
                throw new InvalidDataException("The download exceeded the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            Report(progress, received, totalBytes, stopwatch, 1);
        }
        await output.FlushAsync(cancellationToken);
        ValidateCompletedBytes(received, totalBytes);
        Report(progress, received, totalBytes, stopwatch, 1);
        return new SegmentedDownloadResult(received, 1);
    }

    private static async Task<SegmentedDownloadResult> DownloadSegmentsAsync(
        HttpClient client,
        Uri uri,
        string destination,
        long totalBytes,
        int segmentCount,
        SegmentedDownloadOptions options,
        Func<Uri, bool> isTrustedFinalUri,
        IProgress<SegmentedDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, options.BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        output.SetLength(totalBytes);
        var handle = output.SafeFileHandle;
        var segmentSize = (totalBytes + segmentCount - 1) / segmentCount;
        var stopwatch = Stopwatch.StartNew();
        long received = 0;
        long lastReportTimestamp = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        var tasks = Enumerable.Range(0, segmentCount).Select(async index =>
        {
            var start = checked(index * segmentSize);
            var end = Math.Min(totalBytes - 1, start + segmentSize - 1);
            if (start > end) return;
            try
            {
                await DownloadSegmentAsync(client, uri, handle, start, end,
                    totalBytes, options, isTrustedFinalUri, count =>
                    {
                        var aggregate = Interlocked.Add(ref received, count);
                        var now = Stopwatch.GetTimestamp();
                        var previous = Volatile.Read(ref lastReportTimestamp);
                        if (Stopwatch.GetElapsedTime(previous, now) <
                            TimeSpan.FromMilliseconds(100)) return;
                        if (Interlocked.CompareExchange(ref lastReportTimestamp,
                                now, previous) == previous)
                            Report(progress, aggregate, totalBytes, stopwatch,
                                segmentCount);
                    }, linked.Token);
            }
            catch
            {
                linked.Cancel();
                throw;
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            linked.Cancel();
            var rangeFailure = tasks
                .Where(task => task.Exception is not null)
                .SelectMany(task => task.Exception!.Flatten().InnerExceptions)
                .OfType<RangeNotSupportedException>()
                .FirstOrDefault();
            if (rangeFailure is not null) throw rangeFailure;
            throw;
        }
        await output.FlushAsync(cancellationToken);
        ValidateCompletedBytes(received, totalBytes);
        Report(progress, received, totalBytes, stopwatch, segmentCount);
        return new SegmentedDownloadResult(received, segmentCount);
    }

    private static async Task DownloadSegmentAsync(
        HttpClient client,
        Uri uri,
        SafeFileHandle output,
        long start,
        long end,
        long totalBytes,
        SegmentedDownloadOptions options,
        Func<Uri, bool> isTrustedFinalUri,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri, start, end);
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ValidateFinalUri(response, isTrustedFinalUri);
        if (response.StatusCode == HttpStatusCode.OK)
            throw new RangeNotSupportedException(
                "The server ignored a segmented range request.");
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new RangeNotSupportedException(
                "The server did not return a partial response.");
        _ = ValidateContentRange(response, start, end, totalBytes);

        var expectedSegmentBytes = end - start + 1;
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != expectedSegmentBytes)
            throw new InvalidDataException(
                "A download segment has an unexpected size.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[options.BufferSize];
        long segmentBytes = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            segmentBytes = checked(segmentBytes + count);
            if (segmentBytes > expectedSegmentBytes)
                throw new InvalidDataException(
                    "A download segment exceeded its requested range.");
            await RandomAccess.WriteAsync(output, buffer.AsMemory(0, count),
                start + segmentBytes - count, cancellationToken);
            reportBytes(count);
        }
        if (segmentBytes != expectedSegmentBytes)
            throw new EndOfStreamException(
                "A download segment ended before its requested range completed.");
    }

    private static HttpRequestMessage CreateRequest(Uri uri,
        long? start = null, long? end = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (start is not null) request.Headers.Range = new RangeHeaderValue(start, end);
        return request;
    }

    private static long ValidateContentRange(HttpResponseMessage response,
        long expectedStart, long expectedEnd, long? expectedTotal)
    {
        var range = response.Content.Headers.ContentRange;
        if (range?.From != expectedStart || range.To != expectedEnd ||
            range.Length is not { } totalBytes || totalBytes <= expectedEnd)
            throw new InvalidDataException(
                "The server returned an invalid content range.");
        if (expectedTotal is not null && totalBytes != expectedTotal)
            throw new InvalidDataException(
                "The segmented download size changed between requests.");
        return totalBytes;
    }

    private static Uri ValidateFinalUri(HttpResponseMessage response,
        Func<Uri, bool> isTrustedFinalUri)
    {
        var finalUri = response.RequestMessage?.RequestUri ??
            throw new InvalidDataException("The download response has no final URL.");
        if (finalUri.Scheme != Uri.UriSchemeHttps || !isTrustedFinalUri(finalUri))
            throw new InvalidDataException(
                "The download redirected to an untrusted host.");
        return finalUri;
    }

    private static void ValidateTotalBytes(long totalBytes,
        SegmentedDownloadOptions options)
    {
        if (totalBytes <= 0 || totalBytes > options.MaximumBytes)
            throw new InvalidDataException("The download size is outside the allowed range.");
        if (options.ExpectedBytes is { } expected && totalBytes != expected)
            throw new InvalidDataException(
                "The download size does not match the expected size.");
    }

    private static void ValidateCompletedBytes(long received, long? totalBytes)
    {
        if (totalBytes is > 0 && received != totalBytes)
            throw new EndOfStreamException(
                $"The download is incomplete ({received} of {totalBytes} bytes).");
    }

    private static void Report(IProgress<SegmentedDownloadProgress>? progress,
        long received, long? totalBytes, Stopwatch stopwatch, int segmentCount)
    {
        if (progress is null) return;
        var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        progress.Report(new SegmentedDownloadProgress(received, totalBytes,
            received / seconds, segmentCount));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
