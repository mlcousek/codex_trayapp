using CodexTrayApp.Core.Security;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class SecretRedactorTests
{
    [Fact]
    public void Redacts_bearer_tokens()
    {
        var text = "Authorization: Bearer abc.DEF-123_456~xyz failed";

        SecretRedactor.Redact(text).ShouldBe("Authorization: Bearer [redacted] failed");
    }

    [Fact]
    public void Redacts_openai_style_keys()
    {
        SecretRedactor.Redact("key sk-ant-fake0-abcdefghijklmnop end").ShouldBe("key [redacted] end");
    }

    [Fact]
    public void Redacts_json_token_fields()
    {
        var json = """{"accessToken":"FAKE-VALUE","refreshToken": "OTHER","expiresAt":1}""";

        var redacted = SecretRedactor.Redact(json);

        redacted.ShouldNotContain("FAKE-VALUE");
        redacted.ShouldNotContain("OTHER");
        redacted.ShouldContain("\"expiresAt\":1");
    }

    [Fact]
    public void Redacts_a_known_secret_verbatim()
    {
        SecretRedactor.Redact("boom FAKE-ACCESS-TOKEN-0123456789 boom", "FAKE-ACCESS-TOKEN-0123456789")
            .ShouldBe("boom [redacted] boom");
    }

    [Fact]
    public void Ignores_secrets_too_short_to_be_meaningful()
    {
        SecretRedactor.Redact("the cat sat", "cat").ShouldBe("the cat sat");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Handles_empty_input(string? text) =>
        SecretRedactor.Redact(text).ShouldBe(string.Empty);
}
