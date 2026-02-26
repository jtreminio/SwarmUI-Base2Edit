using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using Image = SwarmUI.Utils.Image;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class B2EImageReferenceTests
{
    private static readonly byte[] TinyPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3Z3ioAAAAASUVORK5CYII="
    );

    private static IReadOnlyList<WorkflowNode> NodesOfAnyType(JObject workflow, params string[] classTypes) =>
        (classTypes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => WorkflowUtils.NodesOfType(workflow, t))
            .ToList();

    private static IReadOnlyList<WorkflowNode> Samplers(JObject workflow) =>
        NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");

    private static JArray RequireConnectionInput(JObject node, params string[] preferredKeys)
    {
        Assert.NotNull(node);
        Assert.True(node["inputs"] is JObject, "Expected node to have an 'inputs' object.");
        JObject inputs = (JObject)node["inputs"];

        foreach (string key in preferredKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && inputs.TryGetValue(key, out JToken tok) && tok is JArray arr && arr.Count == 2)
            {
                return arr;
            }
        }

        foreach (JProperty prop in inputs.Properties())
        {
            if (prop.Value is JArray arr && arr.Count == 2)
            {
                return arr;
            }
        }

        Assert.Fail("Expected at least one [nodeId, outputIndex] connection input.");
        return null;
    }

    private static string RequireClassType(JObject workflow, string nodeId)
    {
        Assert.True(workflow.TryGetValue(nodeId, out JToken tok), $"Expected workflow node '{nodeId}' to exist.");
        Assert.True(tok is JObject, $"Expected workflow node '{nodeId}' to be an object.");
        JObject obj = (JObject)tok;
        Assert.True(obj.TryGetValue("class_type", out JToken classType), $"Expected workflow node '{nodeId}' to have class_type.");
        return $"{classType}";
    }

    private static T2IParamInput BuildInput(string applyAfter, string prompt)
    {
        _ = WorkflowTestHarness.Base2EditSteps();
        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        if (!string.IsNullOrWhiteSpace(applyAfter))
        {
            input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        }
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        return input;
    }

    private static JObject MakeStage(
        string applyAfter,
        string model = ModelPrep.UseRefiner,
        string vae = "None",
        bool? refineOnly = null)
    {
        JObject stage = new()
        {
            ["applyAfter"] = applyAfter,
            ["keepPreEditImage"] = false,
            ["control"] = 1.0,
            ["model"] = model,
            ["vae"] = vae,
            ["steps"] = 20,
            ["cfgScale"] = 7.0,
            ["sampler"] = "euler",
            ["scheduler"] = "normal"
        };

        if (refineOnly.HasValue)
        {
            stage["refineOnly"] = refineOnly.Value;
        }

        return stage;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps() =>
        WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> RefinerSteps() =>
        WorkflowTestHarness.Template_BaseThenRefiner()
            .Concat(WorkflowTestHarness.Base2EditSteps());

    private static List<string> CollectEncoderPromptsIncludingEmpty(JObject workflow)
    {
        List<string> prompts = [];
        foreach (WorkflowNode node in WorkflowUtils.NodesOfType(workflow, "SwarmClipTextEncodeAdvanced"))
        {
            if (node.Node?["inputs"] is JObject inputs && inputs.TryGetValue("prompt", out JToken promptTok))
            {
                prompts.Add($"{promptTok}");
            }
        }
        return prompts;
    }

    [Fact]
    public void B2EImage_base_reference_chains_before_current_stage_reference()
    {
        T2IParamInput input = BuildInput(
            "Base",
            "global <edit[0]>stage0 <edit[1]>stage1 <b2eimage[base]>"
        );
        input.Set(Base2EditExtension.EditStages, new JArray(MakeStage("Edit Stage 0")).ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        IReadOnlyList<WorkflowNode> samplers = Samplers(workflow);
        Assert.Equal(2, samplers.Count);

        WorkflowNode stage0Sampler = samplers.Single(s =>
            JToken.DeepEquals(RequireConnectionInput(s.Node, "latent_image", "latent"), new JArray("10", 0)));
        WorkflowNode stage1Sampler = samplers.Single(s =>
            JToken.DeepEquals(RequireConnectionInput(s.Node, "latent_image", "latent"), new JArray(stage0Sampler.Id, 0)));

        JArray stage1Positive = RequireConnectionInput(stage1Sampler.Node, "positive");
        Assert.Equal("ReferenceLatent", RequireClassType(workflow, $"{stage1Positive[0]}"));

        WorkflowNode stage1FinalRef = WorkflowAssertions.RequireNodeById(workflow, $"{stage1Positive[0]}");
        Assert.True(JToken.DeepEquals(RequireConnectionInput(stage1FinalRef.Node, "latent"), new JArray(stage0Sampler.Id, 0)));

        JArray chainedConditioning = RequireConnectionInput(stage1FinalRef.Node, "conditioning");
        Assert.Equal("ReferenceLatent", RequireClassType(workflow, $"{chainedConditioning[0]}"));
        WorkflowNode stage1ExtraRef = WorkflowAssertions.RequireNodeById(workflow, $"{chainedConditioning[0]}");
        Assert.True(JToken.DeepEquals(RequireConnectionInput(stage1ExtraRef.Node, "latent"), new JArray("10", 0)));
    }

    [Fact]
    public void B2EImage_different_vae_reference_inserts_decode_and_encode()
    {
        using var _ = new SwarmUiTestContext();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var vaeHandler = new T2IModelHandler { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        var targetVae = new T2IModel(vaeHandler, "/tmp", "/tmp/UnitTest_TargetVae.safetensors", "UnitTest_TargetVae.safetensors");
        vaeHandler.Models[targetVae.Name] = targetVae;

        T2IParamInput input = BuildInput(
            "Base",
            "global <edit[0]>stage0 <edit[1]>stage1 <b2eimage[base]>"
        );
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages, new JArray(
            MakeStage("Edit Stage 0", model: ModelPrep.UseBase, vae: targetVae.Name)
        ).ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        WorkflowNode targetVaeLoader = WorkflowUtils.NodesOfType(workflow, "VAELoader")
            .Single(n => $"{((JObject)n.Node["inputs"])["vae_name"]}".Contains("UnitTest_TargetVae.safetensors"));

        WorkflowNode baseDecode = WorkflowUtils.FindVaeDecodesBySamples(workflow, new JArray("10", 0)).Single();
        WorkflowNode convertedEncode = WorkflowUtils.NodesOfType(workflow, "VAEEncode")
            .Single(n =>
                JToken.DeepEquals(((JObject)n.Node["inputs"])["pixels"], new JArray(baseDecode.Id, 0))
                && JToken.DeepEquals(((JObject)n.Node["inputs"])["vae"], new JArray(targetVaeLoader.Id, 0)));

        Assert.Contains(
            WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"),
            n => JToken.DeepEquals(RequireConnectionInput(n.Node, "latent"), new JArray(convertedEncode.Id, 0))
        );
    }

    [Fact]
    public void B2EImage_reuses_decode_encode_for_same_source_and_target_vae_across_stages()
    {
        using var _ = new SwarmUiTestContext();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var vaeHandler = new T2IModelHandler { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        var targetVae = new T2IModel(vaeHandler, "/tmp", "/tmp/UnitTest_TargetVae.safetensors", "UnitTest_TargetVae.safetensors");
        vaeHandler.Models[targetVae.Name] = targetVae;

        T2IParamInput input = BuildInput(
            "Base",
            "global <edit[0]>s0 <edit[1]>s1 <b2eimage[base]> <edit[2]>s2 <b2eimage[base]>"
        );
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages, new JArray(
            MakeStage("Edit Stage 0", model: ModelPrep.UseBase, vae: targetVae.Name),
            MakeStage("Edit Stage 1", model: ModelPrep.UseBase, vae: targetVae.Name)
        ).ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        WorkflowNode targetVaeLoader = WorkflowUtils.NodesOfType(workflow, "VAELoader")
            .Single(n => $"{((JObject)n.Node["inputs"])["vae_name"]}".Contains("UnitTest_TargetVae.safetensors"));

        IReadOnlyList<WorkflowNode> baseDecodes = WorkflowUtils.FindVaeDecodesBySamples(workflow, new JArray("10", 0));
        Assert.Single(baseDecodes);
        WorkflowNode baseDecode = baseDecodes[0];

        IReadOnlyList<WorkflowNode> convertedEncodes = WorkflowUtils.NodesOfType(workflow, "VAEEncode")
            .Where(n =>
                JToken.DeepEquals(((JObject)n.Node["inputs"])["pixels"], new JArray(baseDecode.Id, 0))
                && JToken.DeepEquals(((JObject)n.Node["inputs"])["vae"], new JArray(targetVaeLoader.Id, 0)))
            .ToList();
        Assert.Single(convertedEncodes);

        int refsUsingSharedEncode = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent")
            .Count(n => JToken.DeepEquals(RequireConnectionInput(n.Node, "latent"), new JArray(convertedEncodes[0].Id, 0)));
        Assert.Equal(2, refsUsingSharedEncode);
    }

    [Fact]
    public void B2EImage_refiner_reference_is_skipped_when_refiner_anchor_unavailable()
    {
        T2IParamInput input = BuildInput("Base", "global <edit[0]>stage0 <b2eimage[refiner]>");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        IReadOnlyList<WorkflowNode> refs = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent");
        Assert.Single(refs);
        Assert.True(JToken.DeepEquals(RequireConnectionInput(refs[0].Node, "latent"), new JArray("10", 0)));
    }

    [Fact]
    public void B2EImage_refiner_reference_does_not_duplicate_current_stage_latent_in_final_phase()
    {
        T2IParamInput input = BuildInput("Refiner", "global <edit[0]>stage0 <b2eimage[refiner]>");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, RefinerSteps());

        IReadOnlyList<WorkflowNode> refs = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent");
        Assert.Single(refs);

        WorkflowNode sampler = Samplers(workflow).Single();
        JArray samplerLatent = RequireConnectionInput(sampler.Node, "latent_image", "latent");
        JArray samplerPositive = RequireConnectionInput(sampler.Node, "positive");

        WorkflowNode finalRef = WorkflowAssertions.RequireNodeById(workflow, $"{samplerPositive[0]}");
        Assert.Equal("ReferenceLatent", RequireClassType(workflow, finalRef.Id));
        Assert.True(JToken.DeepEquals(RequireConnectionInput(finalRef.Node, "latent"), samplerLatent));
    }

    [Fact]
    public void B2EImage_refiner_reference_chains_in_final_phase_for_later_edit_stage()
    {
        T2IParamInput input = BuildInput(
            "Refiner",
            "global <edit[0]>stage0 <edit[1]>stage1 <b2eimage[refiner]>"
        );
        input.Set(Base2EditExtension.EditStages, new JArray(MakeStage("Edit Stage 0")).ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, RefinerSteps());
        IReadOnlyList<WorkflowNode> samplers = Samplers(workflow);
        Assert.Equal(2, samplers.Count);

        bool foundExpectedChain = false;
        foreach (WorkflowNode sampler in samplers)
        {
            JArray samplerPositive = RequireConnectionInput(sampler.Node, "positive");
            WorkflowNode finalRef = WorkflowAssertions.RequireNodeById(workflow, $"{samplerPositive[0]}");
            if (RequireClassType(workflow, finalRef.Id) != "ReferenceLatent")
            {
                continue;
            }

            WorkflowNode prependedRef = WorkflowAssertions.RequireNodeById(workflow, $"{RequireConnectionInput(finalRef.Node, "conditioning")[0]}");
            if (RequireClassType(workflow, prependedRef.Id) != "ReferenceLatent")
            {
                continue;
            }

            JArray finalLatent = RequireConnectionInput(finalRef.Node, "latent");
            JArray prependedLatent = RequireConnectionInput(prependedRef.Node, "latent");
            if (JToken.DeepEquals(finalLatent, prependedLatent))
            {
                continue;
            }

            foundExpectedChain = true;
            break;
        }

        Assert.True(foundExpectedChain, "Expected at least one sampler to have a two-reference conditioning chain in final phase.");
    }

    [Fact]
    public void B2EImage_prompt_reference_creates_load_encode_and_reference()
    {
        T2IParamInput input = BuildInput("Base", "global <edit[0]>stage0 <b2eimage[prompt0]>");
        input.Set(T2IParamTypes.PromptImages, new List<Image> { new(TinyPngBytes, MediaType.ImagePng) });

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        WorkflowNode imageLoader = WorkflowUtils.NodesOfType(workflow, "LoadImage").Single();
        WorkflowNode promptEncode = WorkflowUtils.NodesOfType(workflow, "VAEEncode")
            .Single(n => JToken.DeepEquals(((JObject)n.Node["inputs"])["pixels"], new JArray(imageLoader.Id, 0)));

        IReadOnlyList<WorkflowNode> refs = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent");
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, n => JToken.DeepEquals(RequireConnectionInput(n.Node, "latent"), new JArray(promptEncode.Id, 0)));
    }

    [Fact]
    public void B2EImage_prompt_images_are_not_auto_referenced_in_edit_stages_without_tag()
    {
        using var testContext = new SwarmUiTestContext();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        var flux2ModelClass = new T2IModelClass
        {
            ID = "unit-test-flux2",
            Name = "UnitTest Flux2",
            CompatClass = T2IModelClassSorter.CompatFlux2,
            StandardWidth = 1024,
            StandardHeight = 1024
        };
        var flux2Model = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Flux2.safetensors", "UnitTest_Flux2.safetensors")
        {
            ModelClass = flux2ModelClass
        };
        sdHandler.Models[flux2Model.Name] = flux2Model;

        T2IParamInput input = BuildInput("Refiner", "global <edit[0]>stage0 <edit[1]>stage1");
        input.Set(T2IParamTypes.Model, flux2Model);
        input.Set(T2IParamTypes.RefinerModel, flux2Model);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages, new JArray(
            MakeStage("Edit Stage 0", model: ModelPrep.UseBase)
        ).ToString());
        input.Set(T2IParamTypes.PromptImages, new List<Image> { new(TinyPngBytes, MediaType.ImagePng) });

        WorkflowGenerator.WorkflowGenStep fluxSeedStep = new(g =>
        {
            _ = g.CreateNode("UnitTest_Model", new JObject(), id: "4", idMandatory: false);
            _ = g.CreateNode("UnitTest_Latent", new JObject(), id: "10", idMandatory: false);

            g.CurrentModel = new WGNodeData(["4", 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
            g.CurrentTextEnc = new WGNodeData(["4", 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());
            g.CurrentVae = new WGNodeData(["4", 2], g, WGNodeData.DT_VAE, g.CurrentCompat());
            g.CurrentMedia = new WGNodeData(["10", 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
            g.FinalLoadedModel = flux2Model;
            g.FinalLoadedModelList = [flux2Model];
        }, -1000);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(
            input,
            new[] { fluxSeedStep }.Concat(WorkflowTestHarness.Base2EditSteps())
        );

        IReadOnlyList<WorkflowNode> refs = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent");
        Assert.Equal(2, refs.Count);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LoadImage"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "SwarmLoadImageB64"));
    }

    [Fact]
    public void B2EImage_prompt_reference_is_skipped_when_prompt_image_missing()
    {
        T2IParamInput input = BuildInput("Base", "global <edit[0]>stage0 <b2eimage[prompt1]>");
        input.Set(T2IParamTypes.PromptImages, new List<Image> { new(TinyPngBytes, MediaType.ImagePng) });

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        Assert.Single(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));
    }

    [Fact]
    public void B2EImage_forward_or_self_edit_references_are_skipped()
    {
        T2IParamInput input = BuildInput("Base", "global <edit[0]>stage0 <b2eimage[edit0]> <b2eimage[edit1]>");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.Single(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));
    }

    [Fact]
    public void B2EImage_is_ignored_when_refine_only_is_enabled()
    {
        T2IParamInput input = BuildInput("Base", "global <edit>stage0 <b2eimage[base]>");
        input.Set(Base2EditExtension.EditRefineOnly, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));
        WorkflowNode sampler = Samplers(workflow).Single();
        JArray positive = RequireConnectionInput(sampler.Node, "positive");
        Assert.Equal("SwarmClipTextEncodeAdvanced", RequireClassType(workflow, $"{positive[0]}"));
    }

    [Fact]
    public void B2EImage_tag_only_edit_section_does_not_fallback_to_global_prompt()
    {
        T2IParamInput input = BuildInput("Base", "global prompt <edit[0]><b2eimage[base]>");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        List<string> prompts = CollectEncoderPromptsIncludingEmpty(workflow);
        Assert.DoesNotContain(prompts, p => p.Contains("global prompt", StringComparison.OrdinalIgnoreCase));
    }
}
