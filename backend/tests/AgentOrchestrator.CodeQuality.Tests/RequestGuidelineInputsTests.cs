namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RequestGuidelineInputsTests
{
    [Fact]
    public void No_request_guidance_keeps_the_established_v1_hash()
    {
        var inputs = new ResolvedInputs("code", "file", 100, 18,
            [new ReviewInput("style", "style.md", "project", 12, ["code"], ["file"],
                true, "Use explicit names", "Use explicit names", false)], []);
        const string expected = "7287f6d5923b741e040d6dba8348b29366331c029595974ae1c724f59085b573";

        Assert.Equal(expected, inputs.EffectiveHash("template"));
        Assert.Equal(expected, inputs.EffectiveHash("template", []));
        Assert.Equal(expected, inputs.EffectiveHash("template", RequestGuidelineInputs.ForRequest(" \n ", null)));
    }

    [Fact]
    public void Request_context_uses_trimmed_content_and_distinguishes_the_scope()
    {
        var inputs = new ResolvedInputs("code", "file", 0, 0, [], []);
        var global = RequestGuidelineInputs.ForRequest("same content", null);
        var trimmed = RequestGuidelineInputs.ForRequest("  same content\n ", null);
        var project = RequestGuidelineInputs.ForRequest(null, "same content");
        var changed = RequestGuidelineInputs.ForRequest("different content", null);

        Assert.Equal(inputs.EffectiveHash("template", global), inputs.EffectiveHash("template", trimmed));
        Assert.NotEqual(inputs.EffectiveHash("template", global), inputs.EffectiveHash("template", project));
        Assert.NotEqual(inputs.EffectiveHash("template", global), inputs.EffectiveHash("template", changed));
        Assert.NotEqual(inputs.EffectiveHash("template", global), inputs.EffectiveHash("template"));
    }

    [Fact]
    public void Ordinary_standard_ids_are_not_mistaken_for_recorded_request_context()
    {
        var standards = RequestGuidelineInputs.ForRequest("global", "project");
        var metadata = new ReviewInputs(ManifestHash.ReviewInput(new string('a', 64)), true,
            standards.Select(input => input with { Version = ReviewInput.Unversioned }).ToArray(),
            [], new PromptReference("prompt", "1.0.0", "sha256:" + new string('b', 64)));

        Assert.Empty(RequestGuidelineInputs.FromStored(metadata));
        Assert.Equal(standards, RequestGuidelineInputs.FromStored(metadata with { Standards = standards }));
    }
}
