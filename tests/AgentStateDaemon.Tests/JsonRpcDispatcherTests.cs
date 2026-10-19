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
