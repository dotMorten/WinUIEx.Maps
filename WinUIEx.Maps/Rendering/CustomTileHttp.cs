using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Web.Http.Filters;
using WinRtHttpClient = Windows.Web.Http.HttpClient;
using WinRtHttpCompletionOption = Windows.Web.Http.HttpCompletionOption;
using WinRtHttpMethod = Windows.Web.Http.HttpMethod;
using WinRtHttpRequestMessage = Windows.Web.Http.HttpRequestMessage;

namespace WinUIEx.Maps.Rendering;

internal sealed class CustomRequestHeaders
{
    private static readonly char[] InvalidNameCharacters =
        "()<>@,;:\\\"/[]?={} \t".ToCharArray();
    private readonly KeyValuePair<string, string>[] _values;
    private readonly RequestOrigin[] _allowedOrigins;

    internal CustomRequestHeaders(
        IReadOnlyDictionary<string, string> values,
        params string?[] configuredUrls)
    {
        _values =
        [
            .. values
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new KeyValuePair<string, string>(
                    pair.Key,
                    pair.Value)),
        ];
        _allowedOrigins =
        [
            .. configuredUrls
                .Where(url => !string.IsNullOrEmpty(url))
                .Select(url => RequestOrigin.FromConfiguredUrl(url!))
                .Distinct(),
        ];

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string name, string value) in _values)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
        Fingerprint = Convert.ToHexString(hash.GetHashAndReset());
    }

    internal string Fingerprint { get; }

    internal static bool IsValid(string? name, string? value)
    {
        if (string.IsNullOrEmpty(name) ||
            name.Any(character =>
                character <= 0x20 ||
                character >= 0x7f ||
                InvalidNameCharacters.Contains(character)))
        {
            return false;
        }

        return value is not null &&
            !value.Any(character =>
                character == '\r' ||
                character == '\n' ||
                character == '\0' ||
                character == 0x7f);
    }

    internal void Apply(WinRtHttpRequestMessage request)
    {
        if (_values.Length == 0 ||
            !_allowedOrigins.Any(origin => origin.Matches(request.RequestUri)))
        {
            return;
        }

        foreach ((string name, string value) in _values)
        {
            if (!request.Headers.TryAppendWithoutValidation(name, value))
            {
                throw new InvalidOperationException(
                    "A configured custom tile request header could not be applied.");
            }
        }
    }

    private readonly record struct RequestOrigin(
        string Scheme,
        string HostPrefix,
        string HostSuffix,
        int Port,
        bool HasSubdomainPlaceholder)
    {
        private const string SubdomainMarker = "winuiexsubdomainmarker";

        internal static RequestOrigin FromConfiguredUrl(string url)
        {
            string marked = url
                .Replace(
                    "{subdomain}",
                    SubdomainMarker,
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    "[subdomain]",
                    SubdomainMarker,
                    StringComparison.OrdinalIgnoreCase);
            Uri uri = new(marked, UriKind.Absolute);
            string host = uri.IdnHost.ToLowerInvariant();
            int markerIndex = host.IndexOf(
                SubdomainMarker,
                StringComparison.Ordinal);
            return markerIndex < 0
                ? new RequestOrigin(
                    uri.Scheme.ToLowerInvariant(),
                    host,
                    string.Empty,
                    uri.Port,
                    false)
                : new RequestOrigin(
                    uri.Scheme.ToLowerInvariant(),
                    host[..markerIndex],
                    host[(markerIndex + SubdomainMarker.Length)..],
                    uri.Port,
                    true);
        }

        internal bool Matches(Uri uri)
        {
            if (!string.Equals(
                    Scheme,
                    uri.Scheme,
                    StringComparison.OrdinalIgnoreCase) ||
                Port != uri.Port)
            {
                return false;
            }

            string host = uri.IdnHost;
            if (!HasSubdomainPlaceholder)
            {
                return string.Equals(
                    HostPrefix,
                    host,
                    StringComparison.OrdinalIgnoreCase);
            }

            if (!host.StartsWith(
                    HostPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                !host.EndsWith(
                    HostSuffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int placeholderLength =
                host.Length - HostPrefix.Length - HostSuffix.Length;
            return placeholderLength > 0 &&
                host.AsSpan(HostPrefix.Length, placeholderLength)
                    .IndexOf('.') < 0;
        }
    }
}

internal static class CustomTileHttp
{
    private static readonly WinRtHttpClient HttpClient = CreateHttpClient();

    internal static async Task<PooledByteBuffer> GetBytesAsync(
        Uri uri,
        string acceptMediaType,
        int maximumBytes,
        CustomRequestHeaders headers,
        CancellationToken cancellationToken)
    {
        using WinRtHttpRequestMessage request = new(WinRtHttpMethod.Get, uri);
        headers.Apply(request);
        request.Headers.Accept.ParseAdd(acceptMediaType);
        using Windows.Web.Http.HttpResponseMessage response = await HttpClient
            .SendRequestAsync(request, WinRtHttpCompletionOption.ResponseHeadersRead)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Custom tile request failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                (System.Net.HttpStatusCode)(int)response.StatusCode);
        }

        return await WinRtHttpContentReader.ReadBoundedAsync(
                response.Content,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static HttpBaseProtocolFilter CreateHttpFilter()
    {
        HttpBaseProtocolFilter filter = new();
        filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.Default;
        filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.Default;
        return filter;
    }

    private static WinRtHttpClient CreateHttpClient()
    {
        WinRtHttpClient client = new(CreateHttpFilter());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinUIEx.Maps/1.0");
        return client;
    }
}
