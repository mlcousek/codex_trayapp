using System.Text.Json;
using CodexTrayApp.Core.Diagnostics;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class JsonShapeDescriberTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_short_string_containing_an_at_sign_is_never_shown_even_when_short_strings_are_included()
    {
        var element = Parse("""{"email":"a@b.co","name":"bob"}""");

        var description = JsonShapeDescriber.Describe(element, maxDepth: 4, includeShortStrings: true);

        // The value that could be an account email is always hidden, whatever includeShortStrings says.
        description.ShouldContain("email: string(6)");
        description.ShouldNotContain("a@b.co");
        // A short string with no '@' is still shown when asked for, proving the '@' check is what hid the email.
        description.ShouldContain("name: \"bob\"");
    }

    [Fact]
    public void A_short_string_is_hidden_by_default_and_only_shown_when_asked_for()
    {
        var element = Parse("""{"status":"ok"}""");

        JsonShapeDescriber.Describe(element).ShouldBe("{status: string(2)}");
        JsonShapeDescriber.Describe(element, includeShortStrings: true).ShouldBe("""{status: "ok"}""");
    }

    [Fact]
    public void A_string_longer_than_the_short_string_limit_is_never_shown_literally()
    {
        var longValue = new string('x', JsonShapeDescriber.ShortStringLimit + 1);
        var element = Parse($$"""{"value":"{{longValue}}"}""");

        var description = JsonShapeDescriber.Describe(element, includeShortStrings: true);

        description.ShouldBe($"{{value: string({longValue.Length})}}");
    }

    [Fact]
    public void An_object_nested_past_the_max_depth_is_rendered_as_a_placeholder()
    {
        var element = Parse("""{"outer":{"inner":{"value":1}}}""");

        // depth 0 is the root object itself, so maxDepth: 1 lets the root through but truncates "outer"'s value.
        var description = JsonShapeDescriber.Describe(element, maxDepth: 1);

        description.ShouldBe("{outer: {...}}");
    }

    [Fact]
    public void An_object_within_the_max_depth_is_rendered_in_full()
    {
        var element = Parse("""{"outer":{"inner":1}}""");

        JsonShapeDescriber.Describe(element, maxDepth: 2).ShouldBe("{outer: {inner: 1}}");
    }

    [Fact]
    public void An_array_longer_than_the_max_item_count_is_truncated_with_an_ellipsis()
    {
        var element = Parse("[1,2,3,4,5,6,7]");

        var description = JsonShapeDescriber.Describe(element);

        description.ShouldBe("[7: 1, 2, 3, 4, 5, ...]");
    }

    [Fact]
    public void An_array_within_the_max_item_count_shows_every_element_without_an_ellipsis()
    {
        var element = Parse("[1,2,3]");

        JsonShapeDescriber.Describe(element).ShouldBe("[3: 1, 2, 3]");
    }

    [Fact]
    public void Numbers_booleans_and_null_render_as_their_raw_json_text()
    {
        var element = Parse("""{"count":3,"ratio":1.5,"enabled":true,"missing":null}""");

        JsonShapeDescriber.Describe(element).ShouldBe("{count: 3, ratio: 1.5, enabled: true, missing: null}");
    }
}
