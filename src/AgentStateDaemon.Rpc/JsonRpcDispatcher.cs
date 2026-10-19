using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStateDaemon.Core.Accounts;
using AgentStateDaemon.Core.Protocol;

namespace AgentStateDaemon.Rpc;

internal sealed class JsonRpcDispatcher : IJsonRpcDispatcher
{
    private static readonly JsonElement NullId = CreateNullId();
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly AccountService _accounts;
    private readonly DateTimeOffset _startedAt;
    private readonly TimeProvider _timeProvider;
    private readonly string _daemonVersion;
    private readonly Action<string, string, long>? _safeLog;
    private readonly ShadowAccountService? _shadowAccounts;
    private readonly bool _shadowMode;

    public JsonRpcDispatcher(
        AccountService accounts,
        string daemonVersion,
        DateTimeOffset startedAt,
        TimeProvider? timeProvider = null,
        Action<string, string, long>? safeLog = null,
        ShadowAccountService? shadowAccounts = null,
        bool shadowMode = false)
    {
        _accounts = accounts;
        _daemonVersion = daemonVersion;
        _startedAt = startedAt.ToUniversalTime();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _safeLog = safeLog;
        _shadowAccounts = shadowAccounts;
        _shadowMode = shadowMode && shadowAccounts is not null;
    }

    public byte[]? Dispatch(ReadOnlyMemory<byte> request)
    {
        if (request.Length > ProtocolLimits.MaximumFrameBytes)
        {
            return SerializeError(NullId, -32600, "Invalid Request", CreateDetail("invalid_request", "The request is invalid."));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(request, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return SerializeError(NullId, -32700, "Parse error", CreateDetail("parse_error", "The request could not be parsed."));
        }

        using (document)
        {
            return DispatchDocument(document.RootElement);
        }
    }

    private byte[]? DispatchDocument(JsonElement root)
    {
        var idElement = default(JsonElement);
        var hasId = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out idElement);
        var validId = hasId && idElement.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number;
        var responseId = validId ? idElement.Clone() : NullId;
        var stopwatch = Stopwatch.StartNew();
        var method = "other";
        var resultCategory = "invalid_request";
        byte[]? response;

        try
        {
            if (hasId && !validId)
            {
                response = SerializeError(NullId, -32600, "Invalid Request", CreateDetail("invalid_request", "The request is invalid."));
                return LogAndReturn(response, method, resultCategory, stopwatch);
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                version.GetString() != "2.0" ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                response = SerializeError(NullId, -32600, "Invalid Request", CreateDetail("invalid_request", "The request is invalid."));
                return LogAndReturn(response, method, resultCategory, stopwatch);
            }

            var requestedMethod = methodElement.GetString() ?? string.Empty;
            method = IsKnownMethod(requestedMethod) ? requestedMethod : "other";
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;
            var result = Invoke(requestedMethod, parameters);
            resultCategory = "ok";
            response = hasId ? SerializeSuccess(responseId, result) : null;
        }
        catch (AccountNotFoundException)
        {
            resultCategory = "not_found";
            response = hasId
                ? SerializeError(responseId, -32004, "Account was not found.", CreateDetail("not_found", "Account was not found."))
                : null;
        }
        catch (AccountRevisionConflictException)
        {
            resultCategory = "revision_conflict";
            response = hasId
                ? SerializeError(responseId, -32009, "Account metadata changed.", CreateDetail("revision_conflict", "Account metadata changed since it was read."))
                : null;
        }
        catch (AccountValidationException)
        {
            resultCategory = "validation_failed";
            response = hasId
                ? SerializeError(responseId, -32602, "Invalid params", CreateDetail("validation_failed", "Request parameters are invalid."))
                : null;
        }
        catch (DaemonStorageException exception)
        {
            resultCategory = exception.ErrorCode;
            var detail = exception.ErrorCode switch
            {
                "unsupported_schema_version" => CreateDetail("unsupported_schema_version", "The state database version is not supported."),
                "database_unhealthy" => CreateDetail("database_unhealthy", "The state database is unavailable."),
                "resource_limit_exceeded" => CreateDetail("resource_limit_exceeded", "The account collection exceeds the supported limit."),
                _ => CreateDetail("storage_failure", "The daemon state store is unavailable.", retryable: exception.Retryable)
            };
            response = hasId ? SerializeError(responseId, -32000, "State storage error.", detail) : null;
        }
        catch (RpcInvalidParamsException)
        {
            resultCategory = "invalid_params";
            response = hasId
                ? SerializeError(responseId, -32602, "Invalid params", CreateDetail("validation_failed", "Request parameters are invalid."))
                : null;
        }
        catch (RpcMethodNotFoundException)
        {
            resultCategory = "method_not_found";
            response = hasId
                ? SerializeError(responseId, -32601, "Method not found", CreateDetail("method_not_found", "The method is not available."))
                : null;
        }
        catch (Exception)
        {
            resultCategory = "internal_error";
            response = hasId
                ? SerializeError(responseId, -32603, "Internal error", CreateDetail("internal_error", "The request could not be completed."))
                : null;
        }

        return LogAndReturn(response, method, resultCategory, stopwatch);
    }

    private object Invoke(string method, JsonElement parameters) => method switch
    {
        "system.ping" => InvokePing(parameters),
        "system.info" => InvokeInfo(parameters),
        "accounts.list" => InvokeList(parameters),
        "accounts.get" => InvokeGet(parameters),
        "accounts.updateMetadata" => InvokeUpdate(parameters),
        "shadow.accounts.reconcile" => InvokeShadowReconcile(parameters),
        _ => throw new RpcMethodNotFoundException()
    };

    private static PingResult InvokePing(JsonElement parameters)
    {
        ValidateEmptyParameters(parameters);
        return new PingResult(true);
    }

    private SystemInfoResult InvokeInfo(JsonElement parameters)
    {
        ValidateEmptyParameters(parameters);
        var now = _timeProvider.GetUtcNow();
        return new SystemInfoResult(
            _daemonVersion,
            CanonicalProtocolPin.SchemaVersion,
            CanonicalProtocolPin.ManifestSha256,
            SqliteSchemaVersion: 1,
            StartedAt: FormatTimestamp(_startedAt),
            UptimeSeconds: Math.Max(0, (long)(now - _startedAt).TotalSeconds),
            Capabilities:
            [
                new CapabilityResult(
                    1,
                    "shadow.accounts.reconcile",
                    _shadowMode,
                    new CapabilityExtensions(
                        CanonicalProtocolPin.SchemaVersion,
                        CanonicalProtocolPin.ManifestSha256))
            ]);
    }

    private AccountListResult InvokeList(JsonElement parameters)
    {
        ValidateEmptyParameters(parameters);
        return _accounts.List();
    }

    private RevisionedAccount InvokeGet(JsonElement parameters)
    {
        var values = ReadObject(parameters, ["accountId"]);
        return _accounts.Get(ReadRequiredString(values, "accountId", 1, 256));
    }

    private RevisionedAccount InvokeUpdate(JsonElement parameters)
    {
        var values = ReadObject(parameters, ["schemaVersion", "accountId", "nickname", "expectedRevision"]);
        if (!values.TryGetValue("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var version) ||
            !values.TryGetValue("expectedRevision", out var expectedRevision))
        {
            throw new RpcInvalidParamsException();
        }

        string? expected = expectedRevision.ValueKind switch
        {
            JsonValueKind.String => expectedRevision.GetString(),
            JsonValueKind.Null => null,
            _ => throw new RpcInvalidParamsException()
        };

        return _accounts.UpdateMetadata(new AccountMetadataUpdate
        {
            SchemaVersion = version,
            AccountId = ReadRequiredString(values, "accountId", 1, 256),
            Nickname = ReadRequiredString(values, "nickname", 0, 256),
            ExpectedRevision = expected
        });
    }

    private ShadowReconcileRpcResult InvokeShadowReconcile(JsonElement parameters)
    {
        if (!_shadowMode || _shadowAccounts is null)
            throw new RpcMethodNotFoundException();

        var values = ReadObject(parameters, ["collection"]);
        if (!values.TryGetValue("collection", out var collectionElement))
            throw new RpcInvalidParamsException();

        var collectionValues = ReadObject(collectionElement, ["schemaVersion", "accounts"]);
        var schemaVersion = ReadRequiredInt32(collectionValues, "schemaVersion");
        if (!collectionValues.TryGetValue("accounts", out var accountsElement) || accountsElement.ValueKind != JsonValueKind.Array)
            throw new RpcInvalidParamsException();

        var accounts = new List<AccountDescriptor>();
        foreach (var accountElement in accountsElement.EnumerateArray())
        {
            var accountValues = ReadObject(accountElement,
                ["schemaVersion", "accountId", "displayName", "health", "isActive", "sortOrder", "createdAt", "planType"]);
            var planType = ReadNullableString(accountValues, "planType", 128);
            accounts.Add(new AccountDescriptor
            {
                SchemaVersion = ReadRequiredInt32(accountValues, "schemaVersion"),
                AccountId = ReadRequiredString(accountValues, "accountId", 0, 256),
                DisplayName = ReadRequiredString(accountValues, "displayName", 0, 256),
                Health = ReadRequiredString(accountValues, "health", 0, 32),
                IsActive = ReadRequiredBoolean(accountValues, "isActive"),
                SortOrder = ReadRequiredInt32(accountValues, "sortOrder"),
                CreatedAt = ReadRequiredString(accountValues, "createdAt", 0, 64),
                PlanType = planType
            });
        }

        var result = _shadowAccounts.Reconcile(new AccountCollectionDocument
        {
            SchemaVersion = schemaVersion,
            Accounts = accounts
        });
        return new ShadowReconcileRpcResult(1, result.Inserted, result.Updated, result.Deleted, result.Unchanged);
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement parameters, string[] allowedNames)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new RpcInvalidParamsException();
        }

        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
            {
                throw new RpcInvalidParamsException();
            }
        }

        return values;
    }

    private static string ReadRequiredString(Dictionary<string, JsonElement> values, string name, int minLength, int maxLength)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new RpcInvalidParamsException();
        }

        var text = value.GetString();
        if (text is null || text.Length < minLength || text.Length > maxLength)
        {
            throw new RpcInvalidParamsException();
        }

        return text;
    }

    private static int ReadRequiredInt32(Dictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new RpcInvalidParamsException();
        return result;
    }

    private static bool ReadRequiredBoolean(Dictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new RpcInvalidParamsException();
        return value.GetBoolean();
    }

    private static string? ReadNullableString(Dictionary<string, JsonElement> values, string name, int maximumLength)
    {
        if (!values.TryGetValue(name, out var value))
            throw new RpcInvalidParamsException();
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new RpcInvalidParamsException();

        var result = value.GetString();
        if (result is null || result.Length > maximumLength)
            throw new RpcInvalidParamsException();
        return result;
    }

    private static void ValidateEmptyParameters(JsonElement parameters)
    {
        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return;
        }

        if (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Any())
        {
            throw new RpcInvalidParamsException();
        }
    }

    private byte[]? LogAndReturn(byte[]? response, string method, string resultCategory, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        _safeLog?.Invoke(method, resultCategory, stopwatch.ElapsedMilliseconds);
        return response;
    }

    private bool IsKnownMethod(string method) => method is
        "system.ping" or "system.info" or "accounts.list" or "accounts.get" or "accounts.updateMetadata" ||
        (_shadowMode && method == "shadow.accounts.reconcile");

    private static byte[] SerializeSuccess(JsonElement id, object result)
    {
        var response = JsonSerializer.SerializeToUtf8Bytes(new RpcSuccess(id, result), SerializerOptions);
        return response.Length <= ProtocolLimits.MaximumFrameBytes
            ? response
            : SerializeError(id, -32000, "Response too large.", CreateDetail("response_too_large", "The response exceeds the supported size."));
    }

    private static byte[] SerializeError(JsonElement id, int code, string message, ErrorDetail detail) =>
        JsonSerializer.SerializeToUtf8Bytes(new RpcFailure(id, new RpcError(code, message, detail)), SerializerOptions);

    private static ErrorDetail CreateDetail(string code, string message, bool? retryable = null) => new()
    {
        ErrorCode = code,
        Message = message,
        Retryable = retryable
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static JsonElement CreateNullId()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private sealed record PingResult(
        [property: JsonPropertyName("alive")] bool Alive);

    private sealed record SystemInfoResult(
        [property: JsonPropertyName("daemonVersion")] string DaemonVersion,
        [property: JsonPropertyName("protocolSchemaVersion")] int ProtocolSchemaVersion,
        [property: JsonPropertyName("protocolManifestSha256")] string ProtocolManifestSha256,
        [property: JsonPropertyName("databaseSchemaVersion")] int SqliteSchemaVersion,
        [property: JsonPropertyName("startedAt")] string StartedAt,
        [property: JsonPropertyName("uptimeSeconds")] long UptimeSeconds,
        [property: JsonPropertyName("capabilities")] IReadOnlyList<CapabilityResult> Capabilities);

    private sealed record CapabilityResult(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("capabilityId")] string CapabilityId,
        [property: JsonPropertyName("supported")] bool Supported,
        [property: JsonPropertyName("extensions")] CapabilityExtensions Extensions);

    private sealed record CapabilityExtensions(
        [property: JsonPropertyName("protocolSchemaVersion")] int ProtocolSchemaVersion,
        [property: JsonPropertyName("protocolManifestSha256")] string ProtocolManifestSha256);

    private sealed record ShadowReconcileRpcResult(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("inserted")] int Inserted,
        [property: JsonPropertyName("updated")] int Updated,
        [property: JsonPropertyName("deleted")] int Deleted,
        [property: JsonPropertyName("unchanged")] int Unchanged);

    private sealed record RpcSuccess(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("result")] object Result)
    {
        public RpcSuccess(JsonElement id, object result) : this("2.0", id, result)
        {
        }
    }

    private sealed record RpcFailure(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("error")] RpcError Error)
    {
        public RpcFailure(JsonElement id, RpcError error) : this("2.0", id, error)
        {
        }
    }

    private sealed record RpcError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("data")] ErrorDetail Data);

    private sealed record ErrorDetail
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("errorCode")]
        public required string ErrorCode { get; init; }

        [JsonPropertyName("message")]
        public required string Message { get; init; }

        [JsonPropertyName("details")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Details { get; init; }

        [JsonPropertyName("retryable")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Retryable { get; init; }
    }

    private sealed class RpcInvalidParamsException : Exception
    {
    }

    private sealed class RpcMethodNotFoundException : Exception
    {
    }
}
