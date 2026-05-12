using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class B2EImageReferenceTests
{
    private static readonly byte[] TinyPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3Z3ioAAAAASUVORK5CYII="
    );

    private static T2IParamInput BuildInput(string applyAfter, string prompt)
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
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

    [Fact]
    public void B2EImage_base_reference_chains_before_current_stage_reference()
    {
        T2IParamInput input = BuildInput(
            "Base",
            "global <edit[0]>stage0 <edit[1]>stage1 <b2eimage[base]>"
        );
        input.Set(Base2EditExtension.EditStages, new JArray(MakeStage("Edit Stage 0")).ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ComfyNode stage0Sampler = samplers.Single(s =>
            s.FindInput("latent_image")?.Connection is { } c && c.Node.Id == "10" && c.SlotIndex == 0);
        ComfyNode stage1Sampler = samplers.Single(s =>
            s.FindInput("latent_image")?.Connection == stage0Sampler.Outputs[0]);

        ReferenceLatentNode stage1FinalRef = Assert.IsType<ReferenceLatentNode>(
            stage1Sampler.FindInput("positive")?.Connection?.Node);
        Assert.Same(stage0Sampler.Outputs[0], stage1FinalRef.Latent.Connection);

        ReferenceLatentNode stage1ExtraRef = Assert.IsType<ReferenceLatentNode>(stage1FinalRef.Conditioning.Connection?.Node);
        INodeOutput stage1ExtraLatent = stage1ExtraRef.Latent.Connection;
        Assert.NotNull(stage1ExtraLatent);
        Assert.Equal("10", stage1ExtraLatent.Node.Id);
        Assert.Equal(0, stage1ExtraLatent.SlotIndex);
    }

    [Fact]
    public void B2EImage_different_vae_reference_inserts_decode_and_encode()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        T2IModel targetVae = new(vaeHandler, "/tmp", "/tmp/UnitTest_TargetVae.safetensors", "UnitTest_TargetVae.safetensors");
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAELoaderNode targetVaeLoader = bridge.Graph.NodesOfType<VAELoaderNode>()
            .Single(n => $"{((JObject)workflow[n.Id])["inputs"]["vae_name"]}".Contains("UnitTest_TargetVae.safetensors"));

        VAEDecodeNode baseDecode = WorkflowQuery.FindVaeDecodesBySamples(bridge, bridge.ResolvePath(new JArray("10", 0))).Single();
        VAEEncodeNode convertedEncode = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Single(n => n.Pixels.Connection == baseDecode.Outputs[0] && n.Vae.Connection == targetVaeLoader.Outputs[0]);

        Assert.Contains(
            bridge.Graph.NodesOfType<ReferenceLatentNode>(),
            n => n.Latent.Connection == convertedEncode.Outputs[0]
        );
    }

    [Fact]
    public void B2EImage_reuses_decode_encode_for_same_source_and_target_vae_across_stages()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        T2IModel targetVae = new(vaeHandler, "/tmp", "/tmp/UnitTest_TargetVae.safetensors", "UnitTest_TargetVae.safetensors");
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAELoaderNode targetVaeLoader = bridge.Graph.NodesOfType<VAELoaderNode>()
            .Single(n => $"{((JObject)workflow[n.Id])["inputs"]["vae_name"]}".Contains("UnitTest_TargetVae.safetensors"));

        IReadOnlyList<VAEDecodeNode> baseDecodes = WorkflowQuery.FindVaeDecodesBySamples(bridge, bridge.ResolvePath(new JArray("10", 0)));
        Assert.Single(baseDecodes);
        VAEDecodeNode baseDecode = baseDecodes[0];

        List<VAEEncodeNode> convertedEncodes = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Where(n => n.Pixels.Connection == baseDecode.Outputs[0] && n.Vae.Connection == targetVaeLoader.Outputs[0])
            .ToList();
        Assert.Single(convertedEncodes);

        int refsUsingSharedEncode = bridge.Graph.NodesOfType<ReferenceLatentNode>()
            .Count(n => n.Latent.Connection == convertedEncodes[0].Outputs[0]);
        Assert.Equal(2, refsUsingSharedEncode);
    }

    [Fact]
    public void B2EImage_refiner_reference_is_skipped_when_refiner_anchor_unavailable()
    {
        T2IParamInput input = BuildInput("Base", "global <edit[0]>stage0 <b2eimage[refiner]>");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode refLatent = Assert.Single(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        INodeOutput latent = refLatent.Latent.Connection;
        Assert.NotNull(latent);
        Assert.Equal("10", latent.Node.Id);
        Assert.Equal(0, latent.SlotIndex);
    }

    [Fact]
    public void B2EImage_refiner_reference_does_not_duplicate_current_stage_latent_in_final_phase()
    {
        T2IParamInput input = BuildInput("Refiner", "global <edit[0]>stage0 <b2eimage[refiner]>");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, RefinerSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotEmpty(WorkflowQuery.NodesOfType(bridge, "UnitTest_RefinerLatent"));
        Assert.Single(bridge.Graph.NodesOfType<ReferenceLatentNode>());

        ComfyNode sampler = WorkflowQuery.Samplers(bridge).Single();
        INodeOutput samplerLatent = sampler.FindInput("latent_image")?.Connection;
        Assert.NotNull(samplerLatent);

        ReferenceLatentNode finalRef = Assert.IsType<ReferenceLatentNode>(sampler.FindInput("positive")?.Connection?.Node);
        Assert.Same(samplerLatent, finalRef.Latent.Connection);
    }

    [Fact]
    public void B2EImage_refiner_reference_chains_in_final_phase_for_later_edit_stage()
    {
        T2IParamInput input = BuildInput(
            "Refiner",
            "global <edit[0]>stage0 <edit[1]>stage1 <b2eimage[refiner]>"
        );
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditStages, new JArray(MakeStage("Edit Stage 0")).ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, RefinerSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ComfyNode stage1Sampler = null;
        ComfyNode stage0Sampler = null;

        foreach (ComfyNode sampler in samplers)
        {
            if (sampler.FindInput("positive")?.Connection?.Node is not ReferenceLatentNode finalRef)
            {
                stage0Sampler = sampler;
                continue;
            }
            if (finalRef.Conditioning.Connection?.Node is not ReferenceLatentNode prependedRef)
            {
                stage0Sampler = sampler;
                continue;
            }
            if (finalRef.Latent.Connection == prependedRef.Latent.Connection)
            {
                stage0Sampler = sampler;
                continue;
            }

            stage1Sampler = sampler;
        }

        Assert.NotNull(stage1Sampler);
        Assert.NotNull(stage0Sampler);

        ReferenceLatentNode stage1FinalRef = Assert.IsType<ReferenceLatentNode>(
            stage1Sampler.FindInput("positive")?.Connection?.Node);
        ReferenceLatentNode stage1PrependedRef = Assert.IsType<ReferenceLatentNode>(
            stage1FinalRef.Conditioning.Connection?.Node);
        Assert.NotSame(stage1FinalRef.Latent.Connection, stage1PrependedRef.Latent.Connection);

        ComfyNode stage0PositiveNode = stage0Sampler.FindInput("positive")?.Connection?.Node;
        Assert.False(
            stage0PositiveNode is ReferenceLatentNode r0 && r0.Conditioning.Connection?.Node is ReferenceLatentNode,
            "Stage-0 sampler must not have a two-deep RefLatent chain on its positive input.");
    }

    [Fact]
    public void B2EImage_prompt_reference_creates_load_encode_and_reference()
    {
        T2IParamInput input = BuildInput("Base", "global <edit[0]>stage0 <b2eimage[prompt0]>");
        input.Set(T2IParamTypes.PromptImages, [new(TinyPngBytes, MediaType.ImagePng)]);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LoadImageNode imageLoader = bridge.Graph.NodesOfType<LoadImageNode>().Single();
        VAEEncodeNode promptEncode = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Single(n => n.Pixels.Connection == imageLoader.Outputs[0]);

        IReadOnlyList<ReferenceLatentNode> refs = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, n => n.Latent.Connection == promptEncode.Outputs[0]);
    }

    [Fact]
    public void B2EImage_prompt_images_are_not_auto_referenced_in_edit_stages_without_tag()
    {
        using SwarmUiTestContext testContext = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        T2IModelClass flux2ModelClass = new()
        {
            ID = "unit-test-flux2",
            Name = "UnitTest Flux2",
            CompatClass = T2IModelClassSorter.CompatFlux2,
            StandardWidth = 1024,
            StandardHeight = 1024
        };
        T2IModel flux2Model = new(sdHandler, "/tmp", "/tmp/UnitTest_Flux2.safetensors", "UnitTest_Flux2.safetensors")
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
        input.Set(T2IParamTypes.PromptImages, [new(TinyPngBytes, MediaType.ImagePng)]);

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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ReferenceLatentNode> refs = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refs.Count);
        Assert.Empty(bridge.Graph.NodesOfType<LoadImageNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
    }

    [Fact]
    public void B2EImage_prompt_reference_is_skipped_when_prompt_image_missing()
    {
        T2IParamInput input = BuildInput("Base", "global <edit[0]>stage0 <b2eimage[prompt1]>");
        input.Set(T2IParamTypes.PromptImages, [new(TinyPngBytes, MediaType.ImagePng)]);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.NodesOfType<ReferenceLatentNode>());
    }

    [Fact]
    public void B2EImage_self_edit_reference_throws()
    {
        T2IParamInput input = BuildInput("Base", "global <edit[0]>stage0 <b2eimage[edit0]>");

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));
        Assert.Contains("must target an earlier stage", ex.Message);
    }

    [Fact]
    public void B2EImage_is_ignored_when_refine_only_is_enabled()
    {
        T2IParamInput input = BuildInput("Base", "global <edit>stage0 <b2eimage[base]>");
        input.Set(Base2EditExtension.EditRefineOnly, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        ComfyNode sampler = WorkflowQuery.Samplers(bridge).Single();
        Assert.IsType<SwarmClipTextEncodeAdvancedNode>(sampler.FindInput("positive")?.Connection?.Node);
    }

    [Fact]
    public void B2EImage_tag_only_edit_section_does_not_fallback_to_global_prompt()
    {
        T2IParamInput input = BuildInput("Base", "global prompt <edit[0]><b2eimage[base]>");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode editSampler = WorkflowQuery.Samplers(bridge).Single(s =>
            s.FindInput("latent_image")?.Connection is { } c && c.Node.Id == "10" && c.SlotIndex == 0);

        ComfyNode positiveNode = editSampler.FindInput("positive")?.Connection?.Node;
        while (positiveNode is ReferenceLatentNode refNode)
        {
            positiveNode = refNode.Conditioning.Connection?.Node;
        }

        SwarmClipTextEncodeAdvancedNode encoder = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(positiveNode);
        string promptValue = encoder.Prompt.LiteralAsString() ?? string.Empty;
        Assert.True(
            string.IsNullOrWhiteSpace(promptValue),
            $"Edit-stage encoder prompt must be empty, got: \"{promptValue}\"");
    }

    [Fact]
    public void B2EImage_edit_reference_alias_and_stage_label_resolve_the_same()
    {
        static void AssertStage2ReferencesStage0(JObject workflow)
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
            IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
            Assert.Equal(3, samplers.Count);

            ComfyNode stage0Sampler = samplers.Single(s =>
                s.FindInput("latent_image")?.Connection is { } c && c.Node.Id == "10" && c.SlotIndex == 0);
            ComfyNode stage1Sampler = samplers.Single(s =>
                s.FindInput("latent_image")?.Connection == stage0Sampler.Outputs[0]);
            ComfyNode stage2Sampler = samplers.Single(s =>
                s.FindInput("latent_image")?.Connection == stage1Sampler.Outputs[0]);

            ReferenceLatentNode finalRef = Assert.IsType<ReferenceLatentNode>(
                stage2Sampler.FindInput("positive")?.Connection?.Node);
            ReferenceLatentNode prependedRef = Assert.IsType<ReferenceLatentNode>(finalRef.Conditioning.Connection?.Node);
            Assert.Same(stage0Sampler.Outputs[0], prependedRef.Latent.Connection);
        }

        T2IParamInput aliasInput = BuildInput(
            "Base",
            "global <edit[0]>stage0 <edit[1]>stage1 <edit[2]>stage2 <b2eimage[edit0]>"
        );
        aliasInput.Set(Base2EditExtension.EditStages, new JArray(MakeStage("Edit Stage 0"), MakeStage("Edit Stage 1")).ToString());

        T2IParamInput stageLabelInput = BuildInput(
            "Base",
            "global <edit[0]>stage0 <edit[1]>stage1 <edit[2]>stage2 <b2eimage[Edit Stage 0]>"
        );
        stageLabelInput.Set(Base2EditExtension.EditStages, new JArray(MakeStage("Edit Stage 0"), MakeStage("Edit Stage 1")).ToString());

        JObject aliasWorkflow = WorkflowTestHarness.GenerateWithSteps(aliasInput, BaseSteps());
        JObject stageLabelWorkflow = WorkflowTestHarness.GenerateWithSteps(stageLabelInput, BaseSteps());

        AssertStage2ReferencesStage0(aliasWorkflow);
        AssertStage2ReferencesStage0(stageLabelWorkflow);

        bool aliasOk = StageRefStore.TryParseStageIndexKey("edit0", out int aliasIndex);
        bool labelOk = StageRefStore.TryParseStageIndexKey("Edit Stage 0", out int labelIndex);
        Assert.True(aliasOk, "TryParseStageIndexKey must accept alias form 'edit0'");
        Assert.True(labelOk, "TryParseStageIndexKey must accept label form 'Edit Stage 0'");
        Assert.Equal(aliasIndex, labelIndex);
    }

    [Fact]
    public void B2EImage_refiner_phase_stage_can_reference_base_phase_edit_stage()
    {
        T2IParamInput input = BuildInput(
            "Base",
            "global <edit[0]>stage0 <edit[1]>stage1 <b2eimage[edit0]>"
        );
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditStages, new JArray(MakeStage("Refiner")).ToString());

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat([
                    new WorkflowGenerator.WorkflowGenStep(g =>
                    {
                        string midLatent = g.CreateNode("UnitTest_MidLatent", [], id: "2100", idMandatory: false);
                        g.CurrentMedia = new WGNodeData([midLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                    }, 0)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ComfyNode stage0Sampler = samplers.Single(s =>
            s.FindInput("latent_image")?.Connection is { } c && c.Node.Id == "10" && c.SlotIndex == 0);
        ComfyNode stage1Sampler = samplers.Single(s => s.Id != stage0Sampler.Id);

        ReferenceLatentNode finalRef = Assert.IsType<ReferenceLatentNode>(
            stage1Sampler.FindInput("positive")?.Connection?.Node);
        ReferenceLatentNode prependedRef = Assert.IsType<ReferenceLatentNode>(finalRef.Conditioning.Connection?.Node);
        Assert.Same(stage0Sampler.Outputs[0], prependedRef.Latent.Connection);
        Assert.NotSame(stage0Sampler.Outputs[0], finalRef.Latent.Connection);
    }
}
