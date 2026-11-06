namespace OpenMono.Win.AgentHost;

// How the Linux agent container reaches Foundry Local on the Windows host.
// Docker Desktop gives containers the host.docker.internal route to the host
// (the same route the macOS path uses). This mirrors AgentContainer.MapEndpoint
// for the Windows case: loopback endpoints are rewritten to that route with
// the port preserved, anything else passes through. Upstream keeps a
// compose-network name (llama-server) for Linux; Windows has no such service
// because inference is host-native Foundry, not a compose service.
// If upstream ever adds an explicit Windows branch, this becomes a thin call.
public static class WindowsContainerEndpoint
{
    public const string ContainerHostRoute = "host.docker.internal";

    public static string MapForContainer(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            return endpoint;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        if (!IsLoopback(uri.Host))
        {
            return endpoint;
        }

        // Same /v1 carve-out as upstream: a path starting with /v1 is part of
        // the endpoint (Strata-style), otherwise the root is host + port.
        if (uri.AbsolutePath.StartsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{ContainerHostRoute}:{uri.Port}{uri.PathAndQuery}";
        }

        return $"{uri.Scheme}://{ContainerHostRoute}:{uri.Port}";
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}
