using System.Reflection;

namespace Octopus.BuildingBlocks;

/// <summary>Build identity for /health and logs. No secrets, just version facts.</summary>
public static class BuildInfo
{
    public static string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

    public static string Framework =>
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
}
