using System.Security.Cryptography;
using System.Text.Json;
using Json.Schema;
using Xunit;

namespace AgentStateDaemon.Tests;

[Collection(SerialTestGroup.Name)]
public sealed class ProtocolPinTests
{
    private static string ProtocolRoot => Path.Combine(AppContext.BaseDirectory, "canonical-protocol");

    [Fact]
    public void VendoredProtocolMatchesCanonicalCommitAndManifestDigest()
    {
        var manifestPath = Path.Combine(ProtocolRoot, "protocol-manifest.json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        Assert.Equal("85215509e03709696f44e775728a9d2de7d5b70b6ab174c0e59f4ad4af2a4d36", Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));

        using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ProtocolRoot, "PROVENANCE.json")));
        Assert.Equal("d4d28da16431a4e8a4551413f6e95f8162279b91", provenance.RootElement.GetProperty("schemaCommit").GetString());
        Assert.Equal(1, provenance.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("85215509e03709696f44e775728a9d2de7d5b70b6ab174c0e59f4ad4af2a4d36", provenance.RootElement.GetProperty("manifestSha256").GetString());

        using var manifest = JsonDocument.Parse(manifestBytes);
        var schemaEntries = manifest.RootElement.GetProperty("schemas").EnumerateArray().ToArray();
        Assert.Equal(9, schemaEntries.Length);
        foreach (var entry in schemaEntries)
        {
            var relativePath = entry.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar);
            var schemaPath = Path.Combine(ProtocolRoot, relativePath);
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(schemaPath)));
            Assert.Equal(entry.GetProperty("sha256").GetString(), actual);
        }
    }

    [Fact]
    public void AllCanonicalPositiveExamplesValidateAgainstTheirPinnedSchemas()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ProtocolRoot, "protocol-manifest.json")));
        var checkedExamples = 0;
        foreach (var entry in manifest.RootElement.GetProperty("schemas").EnumerateArray())
        {
            var schemaRelativePath = entry.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar);
            var schema = LoadSchema(Path.Combine(ProtocolRoot, schemaRelativePath));
            foreach (var example in entry.GetProperty("examples").EnumerateArray())
            {
                var examplePath = Path.Combine(ProtocolRoot, example.GetString()!.Replace('/', Path.DirectorySeparatorChar));
                using var document = JsonDocument.Parse(File.ReadAllBytes(examplePath));
                var result = schema.Evaluate(document.RootElement, new EvaluationOptions { RequireFormatValidation = true });
                Assert.True(result.IsValid, $"Canonical example did not validate: {Path.GetFileName(examplePath)}");
                checkedExamples++;
            }
        }

        Assert.Equal(9, checkedExamples);
    }

    [Fact]
    public void CanonicalInvalidAccountFixturesAreRejected()
    {
        var schema = LoadSchema(Path.Combine(ProtocolRoot, "schemas", "v1", "accounts", "account-descriptor.schema.json"));
        var fixtures = Directory.GetFiles(Path.Combine(ProtocolRoot, "negative-fixtures"), "*.json");
        Assert.Equal(4, fixtures.Length);
        foreach (var fixture in fixtures)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(fixture));
            var result = schema.Evaluate(document.RootElement, new EvaluationOptions { RequireFormatValidation = true });
            Assert.False(result.IsValid, $"Invalid fixture was accepted: {Path.GetFileName(fixture)}");
        }
    }

    internal static JsonSchema LoadSchema(string path) => JsonSchema.FromFile(
        path,
        new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry()
        });
}
