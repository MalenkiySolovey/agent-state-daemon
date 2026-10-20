using System.Text;
using System.Text.Json;
using AgentStateDaemon.Core.Accounts;
using AgentStateDaemon.Core.Protocol;
using AgentStateDaemon.Rpc;
using AgentStateDaemon.Storage.Sqlite;
using Xunit;

namespace AgentStateDaemon.Tests;

[Collection(SerialTestGroup.Name)]
public sealed class JsonRpcDispatcherTests
{
    private static readonly JsonSerializerOptions ShadowJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PingUsesJsonRpc20AndReturnsOnlyResult()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateDispatcher(directory.Path);

        using var response = Dispatch(dispatcher, """
            {"jsonrpc":"2.0","id":17,"method":"system.ping"}
            """);

        Assert.Equal("2.0", response.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(17, response.RootElement.GetProperty("id").GetInt32());
        Assert.True(response.RootElement.GetProperty("result").GetProperty("alive").GetBoolean());
        Assert.False(response.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void SystemInfoContainsOnlySafeOperationalMetadata()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateDispatcher(directory.Path);

        using var response = Dispatch(dispatcher, """
            {"jsonrpc":"2.0","id":"safe","method":"system.info","params":{}}
            """);

        var json = response.RootElement.GetProperty("result").GetRawText();
        Assert.Contains("protocolManifestSha256", json, StringComparison.Ordinal);
        Assert.Contains("databaseSchemaVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SID", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShadowCapabilityIsAdvertisedOnlyByShadowDispatcher()
    {
        using var normalDirectory = new TemporaryDirectory();
        using var normal = Dispatch(CreateDispatcher(normalDirectory.Path), """
            {"jsonrpc":"2.0","id":1,"method":"system.info","params":{}}
            """);
        using var unavailable = Dispatch(CreateDispatcher(normalDirectory.Path), """
            {"jsonrpc":"2.0","id":2,"method":"shadow.accounts.reconcile","params":{"collection":{"schemaVersion":1,"accounts":[]}}}
            """);

        var normalCapabilities = normal.RootElement.GetProperty("result").GetProperty("capabilities").EnumerateArray().ToArray();
        Assert.False(normalCapabilities.Single(item => item.GetProperty("capabilityId").GetString() == "shadow.accounts.reconcile")
            .GetProperty("supported").GetBoolean());
        Assert.True(normalCapabilities.Single(item => item.GetProperty("capabilityId").GetString() == "accounts.updateMetadata")
            .GetProperty("supported").GetBoolean());
        Assert.Equal(-32601, unavailable.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        using var shadowDirectory = new TemporaryDirectory();
        using var shadow = Dispatch(CreateShadowDispatcher(shadowDirectory.Path), """
            {"jsonrpc":"2.0","id":3,"method":"system.info","params":{}}
            """);
        var capability = shadow.RootElement.GetProperty("result").GetProperty("capabilities")[0];
        Assert.True(capability.GetProperty("supported").GetBoolean());
        Assert.Equal("shadow.accounts.reconcile", capability.GetProperty("capabilityId").GetString());
        Assert.Equal(shadow.RootElement.GetProperty("result").GetProperty("protocolManifestSha256").GetString(),
            capability.GetProperty("extensions").GetProperty("protocolManifestSha256").GetString());
        Assert.True(shadow.RootElement.GetProperty("result").GetProperty("capabilities").EnumerateArray()
            .Single(item => item.GetProperty("capabilityId").GetString() == "accounts.updateMetadata")
            .GetProperty("supported").GetBoolean());
    }

    [Fact]
    public void ShadowReconcileInsertsUpdatesDeletesAndReadsBackCanonicalProjection()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateShadowDispatcher(directory.Path);

        using var inserted = ShadowRequest(dispatcher, 10, """
            [{"schemaVersion":1,"accountId":"synthetic-a","displayName":"Synthetic A","health":"valid","isActive":true,"sortOrder":0,"createdAt":"2026-10-20T12:00:00Z","planType":"plus"}]
            """);
        Assert.True(inserted.RootElement.TryGetProperty("result", out _), inserted.RootElement.GetRawText());
        Assert.Equal(1, inserted.RootElement.GetProperty("result").GetProperty("inserted").GetInt32());
        Assert.Equal(0, inserted.RootElement.GetProperty("result").GetProperty("updated").GetInt32());

        using var list = Dispatch(dispatcher, """
            {"jsonrpc":"2.0","id":11,"method":"accounts.list"}
            """);
        var readBack = list.RootElement.GetProperty("result").GetProperty("collection").GetProperty("accounts")[0];
        Assert.Equal("synthetic-a", readBack.GetProperty("accountId").GetString());
        Assert.Equal("Synthetic A", readBack.GetProperty("displayName").GetString());
        Assert.Equal("valid", readBack.GetProperty("health").GetString());
        Assert.True(readBack.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, readBack.GetProperty("sortOrder").GetInt32());
        Assert.Equal("2026-10-20T12:00:00.000Z", readBack.GetProperty("createdAt").GetString());
        Assert.Equal("plus", readBack.GetProperty("planType").GetString());

        using var unchanged = ShadowRequest(dispatcher, 12, """
            [{"schemaVersion":1,"accountId":"synthetic-a","displayName":"Synthetic A","health":"valid","isActive":true,"sortOrder":0,"createdAt":"2026-10-20T12:00:00Z","planType":"plus"}]
            """);
        Assert.Equal(1, unchanged.RootElement.GetProperty("result").GetProperty("unchanged").GetInt32());

        using var changed = ShadowRequest(dispatcher, 13, """
            [{"schemaVersion":1,"accountId":"synthetic-a","displayName":"Synthetic A2","health":"needs_relogin","isActive":false,"sortOrder":1,"createdAt":"2026-10-20T12:02:00Z","planType":"team"}]
            """);
        Assert.Equal(1, changed.RootElement.GetProperty("result").GetProperty("updated").GetInt32());

        using var replaced = ShadowRequest(dispatcher, 14, """
            [{"schemaVersion":1,"accountId":"synthetic-b","displayName":"Synthetic B","health":"needs_relogin","isActive":false,"sortOrder":1,"createdAt":"2026-10-20T12:01:00Z","planType":null}]
            """);
        Assert.Equal(1, replaced.RootElement.GetProperty("result").GetProperty("inserted").GetInt32());
        Assert.Equal(1, replaced.RootElement.GetProperty("result").GetProperty("deleted").GetInt32());

        using var finalList = Dispatch(dispatcher, """
            {"jsonrpc":"2.0","id":15,"method":"accounts.list"}
            """);
        var finalAccounts = finalList.RootElement.GetProperty("result").GetProperty("collection").GetProperty("accounts");
        Assert.Single(finalAccounts.EnumerateArray());
        Assert.Equal("synthetic-b", finalAccounts[0].GetProperty("accountId").GetString());
    }

    [Fact]
    public void ShadowReconcileRejectsUnknownFieldsAndDuplicateAccountIds()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateShadowDispatcher(directory.Path);

        using var unknown = Dispatch(dispatcher, """
            {"jsonrpc":"2.0","id":1,"method":"shadow.accounts.reconcile","params":{"extra":true,"collection":{"schemaVersion":1,"accounts":[]}}}
            """);
        Assert.Equal(-32602, unknown.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        const string duplicate = """
            [{"schemaVersion":1,"accountId":"synthetic-a","displayName":"A","health":"valid","isActive":false,"sortOrder":0,"createdAt":"2026-10-20T12:00:00Z","planType":null},{"schemaVersion":1,"accountId":"synthetic-a","displayName":"B","health":"valid","isActive":false,"sortOrder":1,"createdAt":"2026-10-20T12:00:00Z","planType":null}]
            """;
        using var invalid = ShadowRequest(dispatcher, 2, duplicate);
        Assert.Equal(-32602, invalid.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        using var list = Dispatch(dispatcher, """
            {"jsonrpc":"2.0","id":3,"method":"accounts.list"}
            """);
        Assert.Empty(list.RootElement.GetProperty("result").GetProperty("collection").GetProperty("accounts").EnumerateArray());
    }

    [Fact]
    public void ShadowReconcileRejectsCollectionAboveResourceLimitWithoutEchoingInput()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateShadowDispatcher(directory.Path);
        var accounts = Enumerable.Range(0, AccountService.MaximumAccountCount + 1)
            .Select(index => new AccountDescriptor
            {
                AccountId = $"synthetic-{index}",
                DisplayName = "Synthetic Account",
                Health = "unknown",
                IsActive = false,
                SortOrder = index,
                CreatedAt = "2026-10-20T12:00:00Z",
                PlanType = null
            });
        var payload = JsonSerializer.Serialize(accounts, ShadowJsonOptions);

        using var response = ShadowRequest(dispatcher, 1, payload);

        Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("Synthetic Account", response.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShadowReconcileFailureRollsBackEntireCollection()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateShadowDispatcher(directory.Path, () => throw new IOException("synthetic commit failure"));

        using var failed = ShadowRequest(dispatcher, 1, """
            [{"schemaVersion":1,"accountId":"synthetic-a","displayName":"Synthetic A","health":"valid","isActive":false,"sortOrder":0,"createdAt":"2026-10-20T12:00:00Z","planType":null}]
            """);
        Assert.Equal(-32603, failed.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        using var list = Dispatch(dispatcher, """
            {"jsonrpc":"2.0","id":2,"method":"accounts.list"}
            """);
        Assert.Empty(list.RootElement.GetProperty("result").GetProperty("collection").GetProperty("accounts").EnumerateArray());
    }

    [Fact]
    public void ValidNotificationRunsWithoutResponse()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateDispatcher(directory.Path);

        var response = dispatcher.Dispatch(Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","method":"system.ping"}
            """));

        Assert.Null(response);
    }

    [Fact]
    public void ParseErrorUsesStandardCodeAndNullId()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateDispatcher(directory.Path);

        using var response = JsonDocument.Parse(dispatcher.Dispatch(Encoding.UTF8.GetBytes("{"))!);

        Assert.Null(response.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32700, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("parse_error", response.RootElement.GetProperty("error").GetProperty("data").GetProperty("errorCode").GetString());
    }

    [Fact]
    public void InvalidParamsAreSanitizedAndOnlySafeFieldsAreLogged()
    {
        using var directory = new TemporaryDirectory();
        var log = new List<string>();
        var dispatcher = CreateDispatcher(directory.Path, (method, result, duration) => log.Add($"{method}|{result}|{duration}"));
        const string marker = "SYNTHETIC_ACCESS_TOKEN_DO_NOT_PRINT";

        var request = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"accounts.get\",\"params\":{\"accountId\":\"synthetic-account\",\"apiKey\":\"" + marker + "\"}}";
        using var response = Dispatch(dispatcher, request);
        var serialized = response.RootElement.GetRawText();

        Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain(marker, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, string.Join('\n', log), StringComparison.Ordinal);
        Assert.StartsWith("accounts.get|invalid_params|", log.Single(), StringComparison.Ordinal);

        var errorSchema = ProtocolPinTests.LoadSchema(Path.Combine(
            AppContext.BaseDirectory, "canonical-protocol", "schemas", "v1", "common", "error-detail.schema.json"));
        using var detail = JsonDocument.Parse(response.RootElement.GetProperty("error").GetProperty("data").GetRawText());
        Assert.True(errorSchema.Evaluate(detail.RootElement, new Json.Schema.EvaluationOptions()).IsValid);
    }

    [Fact]
    public void InvalidIdentifierIsRejectedBeforeAnyMethodRuns()
    {
        using var directory = new TemporaryDirectory();
        var dispatcher = CreateDispatcher(directory.Path);

        using var response = Dispatch(dispatcher, """
            {"jsonrpc":"2.0","id":[],"method":"accounts.updateMetadata","params":{"schemaVersion":1,"accountId":"synthetic-account","nickname":"Changed","expectedRevision":"revision"}}
            """);

        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(-32600, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Throws<AccountNotFoundException>(() => new SqliteStateStore(directory.Path).GetById("synthetic-account"));
    }

    [Fact]
    public void AccountCollectionOverResourceLimitIsRejectedWithBoundedError()
    {
        var records = Enumerable.Range(0, AccountService.MaximumAccountCount + 1)
            .Select(index => new AccountRecord($"synthetic-{index}", new string('A', 256), null, "valid", false, index,
                DateTimeOffset.UnixEpoch, null, Guid.NewGuid().ToString("N")))
            .ToArray();
        var dispatcher = new JsonRpcDispatcher(new AccountService(new ManyAccountRepository(records)), "test", DateTimeOffset.UtcNow);

        var response = dispatcher.Dispatch(Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","id":1,"method":"accounts.list"}
            """));

        Assert.NotNull(response);
        Assert.True(response.Length <= ProtocolLimits.MaximumFrameBytes);
        using var document = JsonDocument.Parse(response);
        Assert.Equal("resource_limit_exceeded", document.RootElement.GetProperty("error").GetProperty("data").GetProperty("errorCode").GetString());
    }

    private static JsonRpcDispatcher CreateDispatcher(string dataDirectory, Action<string, string, long>? safeLog = null)
    {
        var store = new SqliteStateStore(dataDirectory);
        store.Initialize();
        return new JsonRpcDispatcher(new AccountService(store), "0.1.0-experimental", DateTimeOffset.UtcNow, safeLog: safeLog);
    }

    private static JsonRpcDispatcher CreateShadowDispatcher(string dataDirectory, Action? beforeCommit = null)
    {
        var store = new SqliteStateStore(dataDirectory, TimeProvider.System, null, beforeCommit);
        store.Initialize();
        return new JsonRpcDispatcher(
            new AccountService(store),
            "0.1.0-experimental",
            DateTimeOffset.UtcNow,
            shadowAccounts: new ShadowAccountService(store),
            shadowMode: true);
    }

    private static JsonDocument ShadowRequest(JsonRpcDispatcher dispatcher, int id, string accountsJson) =>
        Dispatch(dispatcher, ShadowRequestJson(id, accountsJson));

    private static string ShadowRequestJson(int id, string accountsJson) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"method\":\"shadow.accounts.reconcile\",\"params\":{\"collection\":{\"schemaVersion\":1,\"accounts\":" + accountsJson + "}}}";

    private static JsonDocument Dispatch(JsonRpcDispatcher dispatcher, string json)
    {
        var response = dispatcher.Dispatch(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(response);
        return JsonDocument.Parse(response);
    }

    private sealed class ManyAccountRepository(IReadOnlyList<AccountRecord> records) : IAccountRepository
    {
        public IReadOnlyList<AccountRecord> List() => records;

        public AccountRecord GetById(string accountId) => throw new AccountNotFoundException();

        public AccountRecord UpdateNickname(string accountId, string nickname, string expectedRevision) => throw new AccountNotFoundException();
    }
}
