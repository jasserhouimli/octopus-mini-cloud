using System.Collections.ObjectModel;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace Octopus.Routing;

/// <summary>
/// In-memory YARP config updated from the Apps table (Running apps only).
/// Route: /apps/{slug}/{**catch-all} -> http://127.0.0.1:{port}/.
/// Apps with a custom domain additionally get a host route (listed first so
/// the Host match wins): {domain}/{**catch-all} -> same destination, no
/// prefix strip. TLS termination is outside this service (reverse proxy up).
/// </summary>
public sealed class OctopusProxyConfigProvider : IProxyConfigProvider
{
    private volatile OctopusProxyConfig _config = new([], []);

    public IProxyConfig GetConfig() => _config;

    public void Update(IReadOnlyList<(string Slug, int Port, string? CustomDomain)> running)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        // Host routes first: a path route has no Host constraint, so host
        // matches must precede them to avoid cross-host path collisions.
        foreach (var (slug, _, domain) in running)
        {
            if (string.IsNullOrWhiteSpace(domain)) continue;
            routes.Add(new RouteConfig
            {
                RouteId = $"app-{slug}-domain",
                ClusterId = $"app-{slug}",
                Match = new RouteMatch { Hosts = [domain], Path = "/{**catch-all}" },
            });
        }

        foreach (var (slug, port, _) in running)
        {
            var routeId = $"app-{slug}";
            routes.Add(new RouteConfig
            {
                RouteId = routeId,
                ClusterId = routeId,
                Match = new RouteMatch { Path = $"/apps/{slug}/{{**catch-all}}" },
                Transforms = new[] { new Dictionary<string, string> { ["PathRemovePrefix"] = $"/apps/{slug}" } },
            });
            clusters.Add(new ClusterConfig
            {
                ClusterId = routeId,
                Destinations = new ReadOnlyDictionary<string, DestinationConfig>(
                    new Dictionary<string, DestinationConfig>
                    {
                        ["d1"] = new() { Address = $"http://127.0.0.1:{port}/" },
                    }),
            });
        }

        var old = _config;
        _config = new OctopusProxyConfig(routes, clusters);
        old.SignalChange();
    }

    private sealed class OctopusProxyConfig(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters) : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new();
        public IReadOnlyList<RouteConfig> Routes => routes;
        public IReadOnlyList<ClusterConfig> Clusters => clusters;
        public IChangeToken ChangeToken => new CancellationChangeToken(_cts.Token);
        public void SignalChange() => _cts.Cancel();
    }
}
