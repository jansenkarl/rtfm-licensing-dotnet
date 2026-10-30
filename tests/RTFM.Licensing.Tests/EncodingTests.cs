using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

public class EncodingTests
{
    [Fact]
    public void Base64UrlIsStrict()
    {
        Assert.Equal(new byte[] { 0xFB, 0xFF }, Base64Url.Decode("-_8"));
        Assert.Equal("-_8", Base64Url.Encode(new byte[] { 0xFB, 0xFF }));
        foreach (var bad in new[] { "+/8", "-_8=", "-_ 8", "A" }) Assert.Null(Base64Url.Decode(bad));
    }

    [Fact]
    public void JsonKeepsBooleansIntegersAndStringsApart()
    {
        var value = Json.ParseObject("""{"a":true,"b":1,"c":"1","d":1.5,"e":9007199254740993,"f":1e3}"""u8)!.Value;
        Assert.True(value.Property("a").BoolValue());
        Assert.Equal(1, value.Property("b").SafeInteger());
        Assert.Null(value.Property("c").SafeInteger());
        Assert.Null(value.Property("d").SafeInteger());
        Assert.Null(value.Property("e").SafeInteger()); // beyond the integers JavaScript represents exactly
        Assert.Null(value.Property("f").SafeInteger());
    }

    [Fact]
    public void DeviceThumbprintMatchesTheServer()
    {
        // The same expected value as the Swift SDK, computed with the server's stableJson and sha256Base64Url.
        var key = new PublicJwk("jQNM0huq222eftfUUjLqkR_ln4PIhxNYjXw04WGLQm4", "odtYB5gf5IkCdS5ME9dLT2i67O_4P7OgzCdUUEQ2VM0");
        Assert.Equal("sha256:jFiTo_MCYQh1NttE1fQKtLQTIu4UJyTH9z_WGLlTJ1M", key.Thumbprint);
    }

    [Fact]
    public void PublicKeysMustBeP256PointsWithoutAPrivatePart()
    {
        var key = new TestSigner().PublicJwk;
        Assert.Throws<VerificationException>(() => new PublicJwk(key.X, key.X));
        var withPrivate = Json.ParseObject(TestSupport.Json(new Dictionary<string, string> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = key.X, ["y"] = key.Y, ["d"] = "secret" }));
        Assert.Throws<VerificationException>(() => PublicJwk.FromJson(withPrivate));
    }

    [Fact]
    public void AppIntegrationFile()
    {
        var root = new TestSigner().PublicJwk;
        var integration = TestSupport.SyntheticIntegration(root, tenantId: null);
        Assert.Equal("https://synthetic-license.invalid", integration.Issuer);
        Assert.Null(integration.TenantId);
        Dictionary<string, object> File(string baseUrl, string schema = "rtfm.app-integration.v1") => new()
        {
            ["schema"] = schema, ["environment"] = "synthetic", ["apiBaseUrl"] = baseUrl, ["productCode"] = "p", ["productVersionCode"] = "v",
            ["rootKeyId"] = "r", ["rootPublicJwk"] = new Dictionary<string, string> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = root.X, ["y"] = root.Y },
        };
        Assert.Throws<VerificationException>(() => new AppIntegration(TestSupport.Json(File("http://synthetic-license.invalid"))));
        Assert.Throws<VerificationException>(() => new AppIntegration(TestSupport.Json(File("https://synthetic-license.invalid/path"))));
        _ = new AppIntegration(TestSupport.Json(File("https://synthetic-license.invalid")));
        Assert.Throws<VerificationException>(() => new AppIntegration(TestSupport.Json(File("https://synthetic-license.invalid", "rtfm.app-integration.v2"))));
    }

    [Fact]
    public void TimestampsNeedATimeZone()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-08-30T12:00:00Z"), Timestamp.Parse("2026-08-30T12:00:00.000Z"));
        Assert.NotNull(Timestamp.Parse("2026-08-30T12:00:00Z"));
        Assert.Null(Timestamp.Parse("2026-08-30T12:00:00"));
        Assert.Null(Timestamp.Parse("2026-08-30"));
    }
}
