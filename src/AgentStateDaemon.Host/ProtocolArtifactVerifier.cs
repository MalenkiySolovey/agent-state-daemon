using System.Security.Cryptography;
using System.Text.Json;
using AgentStateDaemon.Core.Protocol;

namespace AgentStateDaemon.Host;

internal static class ProtocolArtifactVerifier
{
    public static void Verify(string baseDirectory)
    {
        var manifestPath = Path.Combine(baseDirectory, "protocol-manifest.json");
        var provenancePath = Path.Combine(baseDirectory, "PROVENANCE.json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), CanonicalProtocolPin.ManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException();
        }

        using var provenance = JsonDocument.Parse(File.ReadAllBytes(provenancePath));
        var provenanceRoot = provenance.RootElement;
        if (provenanceRoot.GetProperty("schemaCommit").GetString() != CanonicalProtocolPin.SchemaCommit ||
            provenanceRoot.GetProperty("schemaVersion").GetInt32() != CanonicalProtocolPin.SchemaVersion ||
            provenanceRoot.GetProperty("manifestSha256").GetString() != CanonicalProtocolPin.ManifestSha256)
        {
            throw new InvalidDataException();
        }

        using var manifest = JsonDocument.Parse(manifestBytes);
        foreach (var schema in manifest.RootElement.GetProperty("schemas").EnumerateArray())
        {
            var relativePath = schema.GetProperty("path").GetString() ?? throw new InvalidDataException();
            var relativeDigest = schema.GetProperty("sha256").GetString() ?? throw new InvalidDataException();
            var schemaPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var schemaRoot = Path.GetFullPath(Path.Combine(baseDirectory, "schemas")) + Path.DirectorySeparatorChar;
            if (!schemaPath.StartsWith(schemaRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException();
            }

            var schemaHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(schemaPath)));
            if (!string.Equals(schemaHash, relativeDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException();
            }
        }
    }
}
