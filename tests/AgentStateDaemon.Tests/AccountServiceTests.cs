using System.Text.Json;
using AgentStateDaemon.Core.Accounts;
using AgentStateDaemon.Core.Protocol;
using AgentStateDaemon.Storage.Sqlite;
using Xunit;

namespace AgentStateDaemon.Tests;

[Collection(SerialTestGroup.Name)]
public sealed class AccountServiceTests
{
    [Fact]
    public void MetadataUpdateMapsNicknameToCanonicalDescriptorAndPreservesBaseDisplayName()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var sourceAccount = SyntheticAccount.Create();
        store.SeedForTests(sourceAccount);
        var service = new AccountService(store);
        var descriptorSchema = ProtocolPinTests.LoadSchema(Path.Combine(
            AppContext.BaseDirectory, "canonical-protocol", "schemas", "v1", "accounts", "account-descriptor.schema.json"));

        var initial = service.Get(sourceAccount.AccountId);
        var renamed = service.UpdateMetadata(new AccountMetadataUpdate
        {
            SchemaVersion = 1,
            AccountId = sourceAccount.AccountId,
            Nickname = "Work",
            ExpectedRevision = initial.Revision
        });
        var cleared = service.UpdateMetadata(new AccountMetadataUpdate
        {
            SchemaVersion = 1,
            AccountId = sourceAccount.AccountId,
            Nickname = string.Empty,
            ExpectedRevision = renamed.Revision
        });

        Assert.Equal("Synthetic Account", initial.Account.DisplayName);
        Assert.Equal("Work", renamed.Account.DisplayName);
        Assert.Equal("Synthetic Account", cleared.Account.DisplayName);
        Assert.NotEqual(initial.Revision, renamed.Revision);
        Assert.NotEqual(renamed.Revision, cleared.Revision);
        using var descriptor = JsonDocument.Parse(JsonSerializer.Serialize(renamed.Account));
        Assert.True(descriptorSchema.Evaluate(descriptor.RootElement, new Json.Schema.EvaluationOptions { RequireFormatValidation = true }).IsValid);
    }

    [Fact]
    public void MetadataUpdateRequiresVersionAndExpectedRevision()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var service = new AccountService(store);

        Assert.Throws<AccountValidationException>(() => service.UpdateMetadata(new AccountMetadataUpdate
        {
            SchemaVersion = 0,
            AccountId = "synthetic-account",
            Nickname = "Example",
            ExpectedRevision = "synthetic-revision"
        }));
        Assert.Throws<AccountValidationException>(() => service.UpdateMetadata(new AccountMetadataUpdate
        {
            SchemaVersion = 1,
            AccountId = "synthetic-account",
            Nickname = "Example",
            ExpectedRevision = null
        }));
    }

    [Fact]
    public void ListReturnsCanonicalCollectionAndOpaqueRevisionSidecar()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);
        var result = new AccountService(store).List();

        Assert.Single(result.Collection.Accounts);
        Assert.Equal(account.Revision, result.Revisions[account.AccountId]);
        Assert.Equal(account.AccountId, result.Collection.Accounts[0].AccountId);
    }
}
