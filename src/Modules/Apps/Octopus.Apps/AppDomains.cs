using Octopus.BuildingBlocks;

namespace Octopus.Apps;

/// <summary>
/// Custom-domain validation (host routing is owned by Routing; this only
/// decides which strings are acceptable). Domains are lowercase FQDNs;
/// null/empty clears the mapping.
/// </summary>
public static class AppDomains
{
    public static Result<string?> Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Result<string?>.Ok(null);
        var domain = input.Trim().ToLowerInvariant();
        if (domain.Length is < 3 or > 253)
            return Result<string?>.Fail("Domain must be 3..253 chars.");
        var labels = domain.Split('.');
        if (labels.Length < 2)
            return Result<string?>.Fail("Domain must be a fully-qualified name (e.g. app.example.com).");
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63
                || !char.IsAsciiLetterOrDigit(label[0])
                || !char.IsAsciiLetterOrDigit(label[^1]))
                return Result<string?>.Fail($"Invalid domain label '{label}'.");
            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return Result<string?>.Fail($"Invalid domain label '{label}'.");
            }
        }
        if (!labels[^1].All(char.IsAsciiLetter) || labels[^1].Length < 2)
            return Result<string?>.Fail("Domain TLD must be alphabetic (IP literals are not accepted).");
        return Result<string?>.Ok(domain);
    }
}
