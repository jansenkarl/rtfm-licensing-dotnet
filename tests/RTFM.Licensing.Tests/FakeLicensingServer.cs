using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

/// <summary>A small in-process stand-in for the licensing API: checks challenge proofs, counts activations, signs
/// leases, and replays an idempotent request's stored response. It can lose a response after doing the work, or fail.</summary>
internal sealed class FakeLicensingServer : HttpMessageHandler
{
    private readonly object gate = new();
    public readonly TestSigner Root = new();
    public Dictionary<string, TestSigner> LeaseSigners = new() { ["lease-1"] = new TestSigner() };
    public string ActiveKid = "lease-1";
    public string ManifestGeneratedAt = "2026-07-29T12:00:00.000Z";
    public int SeatsUsed, MutationsReceived, ActivationsCreated;
    public Dictionary<string, long> Sequences = new();
    public bool LoseNextResponse, Offline;
    public (int Status, string Code, bool Retryable)? FailNext;
    public Action? AfterMutation;
    public string? OverrideThumbprint;
    public long Now = 1_785_499_200;
    public JsonObject? Term;
    public string? LastPlatform;
    private readonly Dictionary<string, (string Nonce, string Thumbprint, bool Used)> challenges = new();
    private readonly Dictionary<string, string> replays = new();
    private readonly Dictionary<string, string> credentials = new();

    public AppIntegration Integration => TestSupport.SyntheticIntegration(Root.PublicJwk);

    public HttpClient Client() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (gate)
        {
            if (Offline) throw new HttpRequestException("offline");
            var body = JsonNode.Parse(text)!.AsObject();
            var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.First() : null;
            var (status, json) = Handle(request.RequestUri!.AbsolutePath, body, key);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private (int, JsonObject) Handle(string path, JsonObject body, string? key)
    {
        if (path == "/v1/products/synthetic-audio/verification-keys") return (200, new JsonObject { ["signature"] = Manifest() });
        if (path == "/v1/device-challenges")
        {
            LastPlatform = (string?)body["client"]?["platform"];
            var id = $"chg_{challenges.Count + 1}";
            var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
            challenges[id] = (nonce, Thumbprint(body), false);
            return (200, new JsonObject { ["challenge_id"] = id, ["nonce"] = nonce, ["expires_at"] = "2026-07-31T12:05:00Z" });
        }
        if (key is not { Length: >= 16 }) return Error(400, "idempotency_key_required");
        if (replays.TryGetValue(key, out var stored)) return (200, JsonNode.Parse(stored)!.AsObject());
        if (FailNext is { } failure)
        {
            FailNext = null;
            return Error(failure.Status, failure.Code, failure.Retryable);
        }
        MutationsReceived++;
        var (status, json) = Mutate(path, body);
        if (status == 200) replays[key] = json.ToJsonString();
        AfterMutation?.Invoke();
        if (LoseNextResponse)
        {
            LoseNextResponse = false;
            throw new HttpRequestException("connection lost");
        }
        return (status, json);
    }

    private (int, JsonObject) Mutate(string path, JsonObject body)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var trial = parts.Length >= 2 && parts[1] == "trials";
        var challenge = body["challenge"]!.AsObject();
        var id = (string)challenge["id"]!;
        if (!challenges.TryGetValue(id, out var stored) || stored.Used || stored.Nonce != (string?)challenge["nonce"] || stored.Thumbprint != Thumbprint(body))
        {
            return Error(401, "invalid_challenge");
        }
        string message;
        if (trial)
        {
            var subject = parts.Length == 4 ? parts[2] : "--";
            message = $"rtfm-device-challenge-v2:{id}:{stored.Nonce}:{(subject == "--" ? "trial_start" : "trial_refresh")}:{subject}";
        }
        else message = $"rtfm-device-challenge-v1:{id}:{stored.Nonce}";
        var device = body["device_public_key"]!;
        var key = new PublicJwk((string)device["x"]!, (string)device["y"]!);
        using (var verifier = key.CreateVerifier())
        {
            if (!verifier.VerifyData(Encoding.UTF8.GetBytes(message), Base64Url.Decode((string)challenge["signature"]!)!, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return Error(401, "invalid_challenge_proof");
            }
        }
        challenges[id] = stored with { Used = true };
        var thumb = OverrideThumbprint ?? key.Thumbprint;
        switch (trial, parts[^1])
        {
            case (false, "activations"):
                if ((string?)body["product_key"] != "SYNTH-GOOD-KEY") return Error(403, "invalid_product_key");
                SeatsUsed++;
                ActivationsCreated++;
                var activation = $"act_{ActivationsCreated}";
                credentials[activation] = $"cred-{activation}";
                return (200, new JsonObject { ["activation_id"] = activation, ["refresh_credential"] = credentials[activation], ["lease"] = PaidLease(activation, thumb) });
            case (false, "leases"):
                if (!credentials.TryGetValue(parts[2], out var credential) || credential != (string?)body["refresh_credential"]) return Error(401, "invalid_activation_credentials");
                return (200, new JsonObject { ["activation_id"] = parts[2], ["lease"] = PaidLease(parts[2], thumb) });
            case (false, "deactivate"):
                if (!credentials.Remove(parts[2])) return Error(401, "invalid_activation_credentials");
                SeatsUsed--;
                return (200, new JsonObject { ["activation_id"] = parts[2], ["status"] = "deactivated", ["seat_released"] = true });
            default:
                return (200, new JsonObject { ["trial_id"] = "trl_1", ["refresh_credential"] = "trial-cred", ["lease"] = TrialLease(thumb) });
        }
    }

    public string Manifest()
    {
        var keys = new JsonArray();
        foreach (var (kid, signer) in LeaseSigners.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            keys.Add(new JsonObject { ["kid"] = kid, ["status"] = "active", ["jwk"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = signer.PublicJwk.X, ["y"] = signer.PublicJwk.Y } });
        }
        return Root.Sign("synthetic-root-v1", CompactJws.ManifestType, new JsonObject
        {
            ["schema"] = "rtfm.product-verification-key-manifest.v1", ["issuer"] = "https://synthetic-license.invalid", ["environment"] = "synthetic",
            ["product_id"] = "synthetic-audio", ["generated_at"] = ManifestGeneratedAt, ["keys"] = keys,
        });
    }

    private JsonObject Claims(string device, string subject)
    {
        var sequence = Sequences.GetValueOrDefault(subject) + 1;
        Sequences[subject] = sequence;
        return new JsonObject
        {
            ["kid"] = ActiveKid, ["iss"] = "https://synthetic-license.invalid", ["tenant_id"] = "tenant_synthetic_publisher", ["product_id"] = "synthetic-audio",
            ["release_family_id"] = "synthetic-audio-1", ["device_thumbprint"] = device, ["iat"] = Now, ["nbf"] = Now, ["refresh_after"] = Now + 75 * 86400,
            ["exp"] = Now + 90 * 86400, ["grace_until"] = Now + 104 * 86400, ["sequence"] = sequence,
            ["entitlements"] = new JsonArray { new JsonObject { ["code"] = "activation.limit", ["value"] = 2 } },
        };
    }

    private string PaidLease(string activation, string device)
    {
        var payload = Claims(device, activation);
        payload["schema"] = "rtfm.license-lease.v1";
        payload["license_id"] = "lic_1";
        payload["activation_id"] = activation;
        payload["revocation_epoch"] = 0;
        if (Term is not null) payload["term"] = Term.DeepClone();
        return LeaseSigners[ActiveKid].Sign(ActiveKid, CompactJws.LeaseType, payload);
    }

    private string TrialLease(string device)
    {
        var payload = Claims(device, "trl_1");
        payload["schema"] = "rtfm.trial-lease.v1";
        payload["lease_kind"] = "trial";
        payload["trial_id"] = "trl_1";
        payload["trial_revision"] = 1;
        payload["trial_ends_at"] = "2026-12-31T00:00:00.000Z";
        payload["refresh_after"] = Now + 20 * 3600;
        payload["exp"] = Now + 24 * 3600;
        payload["grace_until"] = Now + 36 * 3600;
        return LeaseSigners[ActiveKid].Sign(ActiveKid, CompactJws.TrialLeaseType, payload);
    }

    private static string Thumbprint(JsonObject body)
    {
        var device = body["device_public_key"]!;
        return new PublicJwk((string)device["x"]!, (string)device["y"]!).Thumbprint;
    }

    private static (int, JsonObject) Error(int status, string code, bool retryable = false) =>
        (status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = $"Synthetic {code}.", ["request_id"] = "req_synthetic", ["retryable"] = retryable } });
}
