using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;

namespace TicketFlow.Channels.Api;

/// <summary>A webhook destination was blocked by the SSRF policy (before or while connecting). Nothing was sent.</summary>
public sealed class ApiWebhookBlockedException(string message) : HttpRequestException(message);

/// <summary>
/// SSRF policy for outbound webhooks (modelled on EnvelopeFlow's provider endpoint guidance): absolute <c>https</c> URIs of at most
/// 2048 characters, no user info or fragment, an allowed port (default 443), a DNS host not ending in '.' or a public IP literal, and
/// — when <see cref="AllowedHosts"/> is non-empty — an exact host match. At connect time every resolved address must be public
/// (no loopback, private, link-local, CGNAT, multicast, documentation, benchmarking, cloud metadata or IPv6 non-global ranges);
/// connections go only to the validated addresses, with no redirects, proxies or cookies.
/// </summary>
public sealed class ApiWebhookDestinationPolicy
{
    public const int MaximumUriLength = 2048;

    private readonly FrozenSet<string> hosts;
    private readonly FrozenSet<int> ports;

    public ApiWebhookDestinationPolicy(IEnumerable<string>? allowedHosts = null, IEnumerable<int>? allowedPorts = null)
    {
        var hostSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in allowedHosts ?? [])
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsControl) || host.Contains('*') || host.Contains('/') || host.EndsWith('.')
                || !Uri.TryCreate("https://" + host, UriKind.Absolute, out var parsed) || parsed.HostNameType != UriHostNameType.Dns
                || !string.Equals(parsed.Host, host, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Allowed hosts must be exact DNS host names.", nameof(allowedHosts));
            hostSet.Add(parsed.IdnHost);
        }
        hosts = hostSet.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var portSet = new HashSet<int>(allowedPorts ?? [443]);
        if (portSet.Count == 0 || portSet.Any(port => port is < 1 or > 65535))
            throw new ArgumentException("Allowed ports must be 1-65535.", nameof(allowedPorts));
        ports = portSet.ToFrozenSet();
    }

    public IReadOnlyCollection<string> AllowedHosts => hosts;

    /// <summary>True when <paramref name="destination"/> satisfies the URI rules (addresses are checked again at connect time).</summary>
    public bool Allows(Uri? destination)
    {
        if (destination is null || !destination.IsAbsoluteUri || destination.OriginalString.Length > MaximumUriLength || !string.Equals(destination.Scheme, Uri.UriSchemeHttps
, StringComparison.Ordinal) || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0 || !ports.Contains(destination.Port))
            return false;
        switch (destination.HostNameType)
        {
            case UriHostNameType.Dns:
                if (destination.Host.EndsWith('.') || destination.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || destination.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                    return false;
                return hosts.Count == 0 || hosts.Contains(destination.IdnHost);
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                return hosts.Count == 0 && IPAddress.TryParse(destination.Host.AsSpan().Trim(['[', ']']), out var literal) && IsPublicAddress(literal);
            default:
                return false;
        }
    }

    internal bool AllowsHostPort(string host, int port, out IPAddress? literalAddress)
    {
        literalAddress = null;
        if (string.IsNullOrEmpty(host) || host.Length > MaximumUriLength || !ports.Contains(port))
            return false;
        var hostSpan = host.AsSpan().Trim(['[', ']']);
        if (IPAddress.TryParse(hostSpan, out var parsed))
        {
            literalAddress = parsed;
            return hosts.Count == 0 && IsPublicAddress(parsed);
        }
        if (host.EndsWith('.') || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (hosts.Count == 0)
            return Uri.CheckHostName(host) == UriHostNameType.Dns;
        return hosts.Contains(host) || (Uri.TryCreate($"https://{host}:{port}", UriKind.Absolute, out var uri) && Allows(uri));
    }

    /// <summary>True for globally routable unicast addresses.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var length))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork && length == 4)
        {
            var a = bytes[0];
            var b = bytes[1];
            return a is not (0 or 10 or 127) && a < 224
                && !(a == 100 && b is >= 64 and <= 127)
                && !(a == 169 && b == 254)
                && !(a == 172 && b is >= 16 and <= 31)
                && !(a == 192 && (b == 168 || b == 0 && bytes[2] is 0 or 2 || b == 88 && bytes[2] == 99))
                && !(a == 198 && (b is 18 or 19 || b == 51 && bytes[2] == 100))
                && !(a == 203 && b == 0 && bytes[2] == 113)
                && !(a == 168 && b == 63 && bytes[2] == 129 && bytes[3] == 16);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || length != 16 || address.ScopeId != 0 || (bytes[0] & 0xe0) != 0x20)
            return false;
        return !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] == 0 && bytes[3] <= 0x2f || bytes[2] == 0x0d && bytes[3] == 0xb8))
            && !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }
}

/// <summary>Builds HTTP handlers that enforce <see cref="ApiWebhookDestinationPolicy"/> for every request and connection.</summary>
public static class ApiWebhookHttp
{
    /// <summary>
    /// A handler that validates each request URI and, on connect, resolves the host once, requires every answer to be public and dials
    /// only those addresses. <paramref name="resolve"/> and <paramref name="dial"/> replace DNS and sockets (for tests); redirects,
    /// proxies and cookies are disabled.
    /// </summary>
    public static HttpMessageHandler CreateHandler(ApiWebhookDestinationPolicy policy,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? dial = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var inner = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = (context, cancellationToken) => ConnectAsync(policy, context.DnsEndPoint, resolve, dial, cancellationToken),
        };
        return Guard(policy, inner);
    }

    /// <summary>Wraps <paramref name="inner"/> so every request URI is validated by <paramref name="policy"/> first.</summary>
    public static HttpMessageHandler Guard(ApiWebhookDestinationPolicy policy, HttpMessageHandler inner)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(inner);
        return new PolicyHandler(policy, inner);
    }

    internal static async ValueTask<Stream> ConnectAsync(ApiWebhookDestinationPolicy policy, DnsEndPoint destination,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve, Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? dial, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!policy.AllowsHostPort(destination.Host, destination.Port, out var literal))
            throw new ApiWebhookBlockedException("The webhook destination is blocked.");
        IPAddress[] addresses;
        if (literal is not null)
            addresses = [literal];
        else
            addresses = await (resolve is null ? Dns.GetHostAddressesAsync(destination.Host, cancellationToken) : resolve(destination.Host, cancellationToken)).ConfigureAwait(false);
        if (addresses.Length is 0 or > 64)
            throw new ApiWebhookBlockedException("The webhook destination resolved to a prohibited network.");
        for (var i = 0; i < addresses.Length; i++)
            if (!ApiWebhookDestinationPolicy.IsPublicAddress(addresses[i]))
                throw new ApiWebhookBlockedException("The webhook destination resolved to a prohibited network.");
        foreach (var address in addresses.Distinct())
        {
            try
            {
                if (dial is not null)
                    return await dial(address, destination.Port, cancellationToken).ConfigureAwait(false);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, destination.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            catch (SocketException)
            {
                // Fall back only to the other validated answers; never resolve again.
            }
        }
        throw new HttpRequestException(HttpRequestError.ConnectionError, "The webhook destination could not be reached.");
    }

    private sealed class PolicyHandler(ApiWebhookDestinationPolicy policy, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!policy.Allows(request.RequestUri))
                throw new ApiWebhookBlockedException("The webhook destination is blocked.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
