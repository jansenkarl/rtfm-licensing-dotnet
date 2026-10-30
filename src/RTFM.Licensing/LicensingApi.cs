using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RTFM.Licensing;

/// <summary>Builds requests for the public licensing API and classifies its responses.</summary>
internal sealed class LicensingApi(AppIntegration integration, HttpClient http, string appVersion)
{
    public AppIntegration Integration { get; } = integration;

    /// <summary>The platform this app reports. The server accepts it only if the Product Version lists it.</summary>
    public static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>The <c>client</c> metadata sent with challenges and trial requests; trial requests repeat it exactly.</summary>
    public JsonObject ClientMetadata() => new() { ["app_version"] = appVersion, ["platform"] = Platform, ["protocol_version"] = "2026-08-01" };

    public async Task<string> VerificationManifestJwsAsync(CancellationToken cancel)
    {
        var json = await SendAsync(HttpMethod.Get, $"/v1/products/{Uri.EscapeDataString(Integration.ProductCode)}/verification-keys", null, null, cancel).ConfigureAwait(false);
        return json.Property("signature").StringValue() ?? throw LicensingException.Service("invalid_response");
    }

    /// <summary><paramref name="purpose"/> and <paramref name="subjectId"/> are set for trial challenges (protocol v2).</summary>
    public async Task<(string Id, string Nonce)> ChallengeAsync(InstallationIdentity identity, string? purpose, string? subjectId, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["product_id"] = Integration.ProductCode, ["release_family_id"] = Integration.ProductVersionCode,
            ["device_public_key"] = DevicePublicKey(identity), ["client"] = ClientMetadata(),
        };
        if (purpose is not null)
        {
            body["challenge_protocol_version"] = "v2";
            body["purpose"] = purpose;
            if (subjectId is not null) body["subject_id"] = subjectId;
        }
        var json = await SendAsync(HttpMethod.Post, "/v1/device-challenges", Encode(body), null, cancel).ConfigureAwait(false);
        return json.Property("challenge_id").StringValue() is { } id && json.Property("nonce").StringValue() is { } nonce
            ? (id, nonce)
            : throw LicensingException.Service("invalid_response");
    }

    public static JsonObject DevicePublicKey(InstallationIdentity identity) =>
        new() { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = identity.PublicKey.X, ["y"] = identity.PublicKey.Y };

    public static byte[] Encode(JsonObject body) => Encoding.UTF8.GetBytes(body.ToJsonString());

    /// <summary>Sends a request and returns the JSON object of a 2xx response, or throws a classified
    /// <see cref="LicensingException"/>.</summary>
    public async Task<JsonElement> SendAsync(HttpMethod method, string path, byte[]? body, string? idempotencyKey, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, Integration.ApiBaseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        int status;
        byte[] data;
        try
        {
            using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            status = (int)response.StatusCode;
            data = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cancel.IsCancellationRequested) throw;
            throw LicensingException.Network(error);
        }
        var json = Json.ParseObject(data);
        if (status is >= 200 and < 300) return json ?? throw LicensingException.Service("invalid_response");
        var error_ = json?.Property("error");
        var code = error_?.Property("code").StringValue() ?? $"http_{status}";
        if (status >= 500 || status == 429 || error_?.Property("retryable").BoolValue() == true) throw LicensingException.Service(code);
        throw LicensingException.Refused(code, error_?.Property("message").StringValue() ?? "The licensing service refused the request.");
    }

    public static string NewIdempotencyKey() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
}
