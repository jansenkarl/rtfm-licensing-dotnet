using System.Text.Json;

namespace RTFM.Licensing;

/// <summary>The public App integration file a publisher downloads from the License Manager for one Product Version
/// (<c>rtfm.app-integration.v1</c>). It holds no secrets. The pinned root key comes only from this file, which ships
/// inside the app, never from a downloaded manifest.</summary>
public sealed record AppIntegration
{
    /// <summary>The licensing environment, such as <c>production</c>.</summary>
    public string Environment { get; }

    /// <summary>The licensing API origin, which is also the issuer named in manifests and leases.</summary>
    public string ApiBaseUrl { get; }

    /// <summary>The product code.</summary>
    public string ProductCode { get; }

    /// <summary>The Product Version code.</summary>
    public string ProductVersionCode { get; }

    /// <summary>The pinned root key's ID.</summary>
    public string RootKeyId { get; }

    /// <summary>The pinned root key every manifest is checked against.</summary>
    public PublicJwk RootPublicKey { get; }

    /// <summary>Checked against each lease when present. Older App integration files do not include it.</summary>
    public string? TenantId { get; }

    /// <summary>The issuer named in manifests and leases.</summary>
    public string Issuer => ApiBaseUrl;

    /// <summary>Reads the file's JSON; throws <see cref="VerificationException"/> if it is not a valid App integration
    /// file.</summary>
    public AppIntegration(ReadOnlySpan<byte> json)
    {
        if (Json.ParseObject(json) is not { } root) throw VerificationException.Malformed("App integration file is not a JSON object");
        if (!root.Property("schema").IsString("rtfm.app-integration.v1"))
        {
            throw VerificationException.Malformed("App integration schema is not rtfm.app-integration.v1");
        }
        string Text(string key) => root.Property(key).StringValue() is { Length: > 0 } value
            ? value
            : throw VerificationException.Malformed($"App integration file is missing {key}");
        var baseUrl = Text("apiBaseUrl");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || baseUrl != $"https://{uri.Authority}")
        {
            throw VerificationException.Malformed("apiBaseUrl must be an https origin");
        }
        Environment = Text("environment");
        ApiBaseUrl = baseUrl;
        ProductCode = Text("productCode");
        ProductVersionCode = Text("productVersionCode");
        RootKeyId = Text("rootKeyId");
        RootPublicKey = PublicJwk.FromJson(root.Property("rootPublicJwk"));
        if (root.Property("tenantId") is { } tenant)
        {
            TenantId = tenant.ValueKind == JsonValueKind.String && tenant.GetString() is { Length: > 0 } value
                ? value
                : throw VerificationException.Malformed("tenantId must be a non-empty string");
        }
    }

    /// <summary>Reads an App integration file from disk.</summary>
    public static AppIntegration FromFile(string path) => new(File.ReadAllBytes(path));
}
