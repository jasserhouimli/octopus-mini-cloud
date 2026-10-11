using Octopus.Routing;

namespace Octopus.Tests;

public sealed class ProxyConfigTests
{
    [Fact]
    public void Path_routes_proxy_to_loopback_port()
    {
        var provider = new OctopusProxyConfigProvider();
        provider.Update([("demo", 5101, (string?)null)]);
        var config = provider.GetConfig();
        Assert.Single(config.Routes);
        var route = config.Routes[0];
        Assert.Equal("app-demo", route.RouteId);
        Assert.Equal("/apps/demo/{**catch-all}", route.Match.Path);
        Assert.Equal("http://127.0.0.1:5101/", config.Clusters[0].Destinations["d1"].Address);
    }

    [Fact]
    public void Domain_route_precedes_path_route_and_shares_cluster()
    {
        var provider = new OctopusProxyConfigProvider();
        provider.Update([("demo", 5101, "app.example.com"), ("plain", 5102, (string?)null)]);
        var config = provider.GetConfig();
        Assert.Equal(3, config.Routes.Count);
        var domain = config.Routes[0];
        Assert.Equal("app-demo-domain", domain.RouteId);
        Assert.Equal("app-demo", domain.ClusterId);
        Assert.Contains("app.example.com", domain.Match.Hosts!);
        Assert.Equal("/{**catch-all}", domain.Match.Path);
        Assert.Empty(domain.Transforms ?? []);
        // Same cluster as the path route: one destination, no extra clusters.
        Assert.Equal(2, config.Clusters.Count);
    }

    [Fact]
    public void Blank_domain_adds_no_host_route()
    {
        var provider = new OctopusProxyConfigProvider();
        provider.Update([("demo", 5101, "  ")]);
        Assert.Single(provider.GetConfig().Routes);
    }
}
