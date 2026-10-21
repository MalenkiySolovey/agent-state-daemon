using System.Security.Cryptography;
using System.Text.Json;
using AgentStateDaemon.Host;
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
        Assert.Equal("72602ae175fd1261f870e2815065da6d96fdf4bd81fce93419e4f031916e90ad", Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));

        using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ProtocolRoot, "PROVENANCE.json")));
        Assert.Equal("d0c2c9de482fbd368c73b9c81a94340da126005b", provenance.RootElement.GetProperty("schemaCommit").GetString());
        Assert.Equal(1, provenance.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("72602ae175fd1261f870e2815065da6d96fdf4bd81fce93419e4f031916e90ad", provenance.RootElement.GetProperty("manifestSha256").GetString());

        using var manifest = JsonDocument.Parse(manifestBytes);
        var schemaEntries = manifest.RootElement.GetProperty("schemas").EnumerateArray().ToArray();
        Assert.Equal(14, schemaEntries.Length);
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

        Assert.Equal(19, checkedExamples);
    }

    [Fact]
    public void CanonicalInvalidAccountFixturesAreRejected()
    {
        var schema = LoadSchema(Path.Combine(ProtocolRoot, "schemas", "v1", "accounts", "account-descriptor.schema.json"));
        var fixtureRoot = Path.Combine(ProtocolRoot, "negative-fixtures");
        var fixtureNames = new[]
        {
            "account-wrong-version.json",
            "account-non-utc-timestamp.json",
            "account-unknown-field.json",
            "account-missing-version.json"
        };
        var fixtures = fixtureNames.Select(name => Path.Combine(fixtureRoot, name)).ToArray();
        foreach (var fixture in fixtures)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(fixture));
            var result = schema.Evaluate(document.RootElement, new EvaluationOptions { RequireFormatValidation = true });
            Assert.False(result.IsValid, $"Invalid fixture was accepted: {Path.GetFileName(fixture)}");
        }
    }

    [Fact]
    public void ProtocolArtifactVerifierRejectsTamperedManifest()
    {
        using var directory = new TemporaryDirectory();
        var sourceRoot = AppContext.BaseDirectory;
        File.Copy(Path.Combine(sourceRoot, "protocol-manifest.json"), Path.Combine(directory.Path, "protocol-manifest.json"));
        File.Copy(Path.Combine(sourceRoot, "PROVENANCE.json"), Path.Combine(directory.Path, "PROVENANCE.json"));
        var sourceSchemas = Path.Combine(sourceRoot, "schemas");
        foreach (var sourceFile in Directory.GetFiles(sourceSchemas, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceSchemas, sourceFile);
            var destinationFile = Path.Combine(directory.Path, "schemas", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }

        File.AppendAllText(Path.Combine(directory.Path, "protocol-manifest.json"), " ");

        Assert.Throws<InvalidDataException>(() => ProtocolArtifactVerifier.Verify(directory.Path));
    }

    internal static JsonSchema LoadSchema(string path) => JsonSchema.FromFile(
        path,
        new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry()
        });
}
