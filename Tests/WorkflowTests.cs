using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class WorkflowTests
{
    private static ComfyNode RequireSingleSampler(WorkflowBridge bridge) =>
        Assert.Single(WorkflowQuery.Samplers(bridge));

    private static List<string> CollectEncoderPrompts(WorkflowBridge bridge)
    {
        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.NotEmpty(encoders);

        List<string> prompts = [];
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            string prompt = enc.Prompt.LiteralValue.ToString();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                prompts.Add(prompt);
            }
        }

        return prompts;
    }

    private static T2IParamInput BuildEditInput(string applyAfter, bool enableBase2EditGroup = true, string prompt = "global <edit>do the edit")
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        if (enableBase2EditGroup)
        {
            input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        }

        if (!string.IsNullOrWhiteSpace(applyAfter))
        {
            input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        }

        input.Set(T2IParamTypes.Seed, 1);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        return input;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps()
    {
        return WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());
    }

    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public void ApplyEditAfter_controls_where_edit_stage_is_injected(string applyAfter)
    {
        static WorkflowGenerator.WorkflowGenStep ProbeStep(string probeType, double priority) =>
            new(g =>
            {
                _ = g.CreateNode(probeType, new JObject
                {
                    ["latent"] = g.CurrentMedia.Path
                });
            }, priority);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
                {
                    WorkflowTestHarness.MinimalGraphSeedStep(),
                    ProbeStep("UnitTest_ProbeAfterBase", 0),
                    ProbeStep("UnitTest_ProbeAfterFinal", 20)
                }
                .Concat(WorkflowTestHarness.Base2EditSteps());

        T2IParamInput input = BuildEditInput(applyAfter);
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = Assert.IsType<KSamplerAdvancedNode>(Assert.Single(WorkflowQuery.Samplers(bridge)));
        ComfyNode afterBase = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_ProbeAfterBase");
        ComfyNode afterFinal = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_ProbeAfterFinal");

        VAEDecodeNode finalDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, sampler.Outputs[0]);

        INodeInput afterBaseLatent = afterBase.Inputs.Single(i => i.Name == "latent");
        INodeInput afterFinalLatent = afterFinal.Inputs.Single(i => i.Name == "latent");

        Assert.Equal(sampler.Id, afterBaseLatent.Connection?.Node.Id);
        Assert.Equal(finalDecode.Id, afterFinalLatent.Connection?.Node.Id);
    }

    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public void EditStage_runs_after_template_and_wires_sampler_via_reflatent(string applyAfter)
    {
        T2IParamInput input = BuildEditInput(applyAfter);
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode refLatent = WorkflowAssertions.RequireNodeOfType<ReferenceLatentNode>(bridge);
        KSamplerAdvancedNode sampler = (KSamplerAdvancedNode)RequireSingleSampler(bridge);

        Assert.NotNull(refLatent.Latent.Connection);
        Assert.Equal("10", refLatent.Latent.Connection.Node.Id);

        IReadOnlyList<(ComfyNode Node, INodeInput Input)> consumers = bridge.Graph.FindInputsConnectedTo(refLatent.Outputs[0]);
        Assert.True(consumers.Any(c => c.Node == sampler),
            "Edit sampler must consume ReferenceLatent output.");

        _ = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, sampler.Outputs[0]);
    }

    [Fact]
    public void EditStage_refiner_hook_anchors_before_downstream_postprocess_encode()
    {
        T2IParamInput input = BuildEditInput("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string decoded = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "801", idMandatory: false);
                    string post = g.CreateNode("UnitTest_PostProcessImage", new JObject()
                    {
                        ["image"] = new JArray(decoded, 0)
                    }, id: "802", idMandatory: false);
                    string encoded = g.CreateNode(VAEEncodeNode.ClassType, new JObject()
                    {
                        ["pixels"] = new JArray(post, 0),
                        ["vae"] = g.CurrentVae.Path
                    }, id: "803", idMandatory: false);
                    g.CurrentMedia = new WGNodeData(new JArray(encoded, 0), g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                }, 2)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode refLatent = WorkflowAssertions.RequireNodeOfType<ReferenceLatentNode>(bridge);
        Assert.NotNull(refLatent.Latent.Connection);
        Assert.Equal("10", refLatent.Latent.Connection.Node.Id);
        Assert.Equal(0, refLatent.Latent.Connection.SlotIndex);
    }

    [Fact]
    public void EditStage_refiner_hook_retargets_existing_image_consumers_to_post_edit_decode()
    {
        T2IParamInput input = BuildEditInput("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "801", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([preDecode, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                    g.CreateNode("UnitTest_ImageConsumer", new JObject()
                    {
                        ["image"] = g.CurrentMedia.Path
                    }, id: "802", idMandatory: false);
                }, 2)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = WorkflowAssertions.RequireNodeOfType<KSamplerAdvancedNode>(bridge);
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, sampler.Outputs[0]);
        ComfyNode consumer = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_ImageConsumer");
        Assert.Equal(postEditDecode.Id, consumer.Inputs.Single(i => i.Name == "image").Connection?.Node.Id);
    }

    [Fact]
    public void EditStage_refiner_hook_retargets_seedvr_style_chain_even_when_final_image_has_drifted()
    {
        T2IParamInput input = BuildEditInput("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "801", idMandatory: false);
                    string chainA = g.CreateNode("UnitTest_SeedVR2Like_A", new JObject()
                    {
                        ["image"] = new JArray(preDecode, 0)
                    }, id: "802", idMandatory: false);
                    string chainB = g.CreateNode("UnitTest_SeedVR2Like_B", new JObject()
                    {
                        ["image"] = new JArray(chainA, 0)
                    }, id: "803", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([chainB, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = WorkflowAssertions.RequireNodeOfType<KSamplerAdvancedNode>(bridge);
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, sampler.Outputs[0]);
        ComfyNode chainConsumer = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_SeedVR2Like_A");
        Assert.Equal(postEditDecode.Id, chainConsumer.Inputs.Single(i => i.Name == "image").Connection?.Node.Id);

        ComfyNode chainTail = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_SeedVR2Like_B");
        Assert.Equal(new JArray(chainTail.Id, 0), generator.CurrentMedia.Path);
    }

    [Fact]
    public void EditStage_refiner_hook_infers_downstream_tail_when_final_imageout_was_not_set()
    {
        T2IParamInput input = BuildEditInput("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "801", idMandatory: false);
                    string mid = g.CreateNode("UnitTest_SeedVR2Like_Mid", new JObject()
                    {
                        ["image"] = new JArray(preDecode, 0)
                    }, id: "802", idMandatory: false);
                    string tail = g.CreateNode("UnitTest_SeedVR2Like_Tail", new JObject()
                    {
                        ["image"] = new JArray(mid, 0)
                    }, id: "803", idMandatory: false);
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode tail = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_SeedVR2Like_Tail");
        Assert.Equal(new JArray(tail.Id, 0), generator.CurrentMedia.Path);
    }

    [Fact]
    public void EditStage_refiner_hook_cleanup_removes_orphan_preedit_decode_in_refiner_shape()
    {
        T2IParamInput input = BuildEditInput("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string refinerLatent = g.CreateNode("UnitTest_RefinerLatent", new JObject(), id: "23", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([refinerLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                    string preEditDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "8", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([preEditDecode, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                }, -100),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string chainA = g.CreateNode("UnitTest_SeedVR2Like_A", new JObject()
                    {
                        ["image"] = g.CurrentMedia.Path
                    }, id: "802", idMandatory: false);
                    string chainB = g.CreateNode("UnitTest_SeedVR2Like_B", new JObject()
                    {
                        ["image"] = new JArray(chainA, 0)
                    }, id: "803", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([chainB, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = WorkflowAssertions.RequireNodeOfType<KSamplerAdvancedNode>(bridge);
        _ = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, sampler.Outputs[0]);
        ComfyNode chainConsumer = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_SeedVR2Like_A");
        Assert.Equal(sampler.Id, chainConsumer.Inputs.Single(i => i.Name == "image").Connection?.Node.Id);
    }

    [Fact]
    public void EditStage_refiner_hook_skips_retarget_when_it_would_create_feedback_cycle()
    {
        T2IParamInput input = BuildEditInput("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "801", idMandatory: false);
                    string chainTail = g.CreateNode("UnitTest_SeedVR2Like_Tail", new JObject()
                    {
                        ["image"] = new JArray(preDecode, 0)
                    }, id: "802", idMandatory: false);
                    string encoded = g.CreateNode(VAEEncodeNode.ClassType, new JObject()
                    {
                        ["pixels"] = new JArray(chainTail, 0),
                        ["vae"] = g.CurrentVae.Path
                    }, id: "803", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([chainTail, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode chainTail = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_SeedVR2Like_Tail");
        Assert.IsType<VAEDecodeNode>(chainTail.Inputs.Single(i => i.Name == "image").Connection?.Node);
    }

    [Fact]
    public void EditStage_refiner_hook_prefers_image_anchor_when_samples_and_image_drift()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.EditRefineOnly, true);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "801", idMandatory: false);
                    string chainTail = g.CreateNode("UnitTest_SeedVR2Like_Tail", new JObject()
                    {
                        ["image"] = new JArray(preDecode, 0)
                    }, id: "802", idMandatory: false);
                    _ = g.CreateNode(VAEEncodeNode.ClassType, new JObject()
                    {
                        ["pixels"] = new JArray(chainTail, 0),
                        ["vae"] = g.CurrentVae.Path
                    }, id: "803", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([chainTail, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        ComfyNode editSampler = samplers.Single(s =>
            s is KSamplerAdvancedNode ks && ks.Positive.Connection?.Node is SwarmClipTextEncodeAdvancedNode);

        KSamplerAdvancedNode editKs = (KSamplerAdvancedNode)editSampler;
        Assert.NotEqual("803", editKs.LatentImage.Connection?.Node.Id);
    }

    private static void AssertSegmentEditChain(WorkflowBridge bridge, string segmentTailId, string preSegmentDecodeId)
    {
        ComfyNode sampler = RequireSingleSampler(bridge);
        VAEEncodeNode encode = bridge.Graph.NodesOfType<VAEEncodeNode>().Single(
            e => e.Pixels.Connection?.Node.Id == segmentTailId);

        KSamplerAdvancedNode ks = Assert.IsType<KSamplerAdvancedNode>(sampler);
        Assert.Equal(encode.Id, ks.LatentImage.Connection?.Node.Id);
        Assert.Equal(segmentTailId, encode.Pixels.Connection?.Node.Id);

        VAEDecodeNode preSegDecode = WorkflowAssertions.RequireNodeById<VAEDecodeNode>(bridge, preSegmentDecodeId);
        IReadOnlyList<(ComfyNode Node, INodeInput Input)> segTailConsumers = bridge.Graph.FindInputsConnectedTo(preSegDecode.Outputs[0]);
        Assert.True(segTailConsumers.Any(c => c.Node.Id == segmentTailId),
            "Segment tail must consume pre-segment decode output.");
    }

    [Fact]
    public void EditStage_refiner_hook_with_segments_runs_after_segment_image_tail()
    {
        T2IParamInput input = BuildEditInput(
            "Refiner",
            enableBase2EditGroup: true,
            prompt: "global <edit>do edit <segment:face,0.2,0.2>"
        );
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(T2IParamTypes.SegmentApplyAfter, "Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string refinerLatent = g.CreateNode("UnitTest_RefinerLatent", new JObject(), id: "23", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([refinerLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                }, -400),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preSegmentDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "1801", idMandatory: false);
                    string segmentTail = g.CreateNode("UnitTest_SegmentAfterRefiner", new JObject()
                    {
                        ["image"] = new JArray(preSegmentDecode, 0)
                    }, id: "1802", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([segmentTail, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertSegmentEditChain(bridge, "1802", "1801");
    }

    [Fact]
    public void EditStage_segment_after_base_edit_stages_applied_after_segment()
    {
        T2IParamInput input = BuildEditInput(
            "Refiner",
            enableBase2EditGroup: true,
            prompt: "global <edit>do edit <segment:face,0.2,0.2>"
        );
        input.Set(T2IParamTypes.SegmentApplyAfter, "Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preSegmentDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "1801", idMandatory: false);
                    string segmentTail = g.CreateNode("UnitTest_SegmentAfterBase", new JObject()
                    {
                        ["image"] = new JArray(preSegmentDecode, 0)
                    }, id: "1802", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([segmentTail, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertSegmentEditChain(bridge, "1802", "1801");
    }

    [Fact]
    public void EditStage_defaults_to_refiner_when_apply_after_missing()
    {
        T2IParamInput input = BuildEditInput(null);
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotEmpty(bridge.Graph.NodesOfType<VAEDecodeNode>());
        Assert.NotEmpty(bridge.Graph.NodesOfType<SaveImageNode>());
    }

    [Fact]
    public void EditStage_does_not_run_when_base2edit_group_is_not_enabled()
    {
        T2IParamInput input = BuildEditInput(null, enableBase2EditGroup: false);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.Empty(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
    }

    [Fact]
    public void EditStage_runs_and_falls_back_to_global_prompt_when_only_edit_stage1_tag_is_present()
    {
        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: "global <edit[1]>do stage1 edit");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotEmpty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.NotEmpty(WorkflowQuery.Samplers(bridge));

        List<string> prompts = CollectEncoderPrompts(bridge);

        Assert.Contains(prompts, p => p.Contains("global") && !p.Contains("do stage1 edit"));
        Assert.DoesNotContain(prompts, p => p.Contains("do stage1 edit"));
    }

    [Fact]
    public void Edit_prompt_stops_at_registered_custom_prompt_sections()
    {
        WorkflowTestHarness.Base2EditSteps();
        HashSet<string> customPartPrefixes = [.. PromptRegion.CustomPartPrefixes];
        List<string> partPrefixes = [.. PromptRegion.PartPrefixes];
        Dictionary<string, Func<string, T2IPromptHandling.PromptTagContext, string>> promptBasic = new(T2IPromptHandling.PromptTagBasicProcessors);
        Dictionary<string, Func<string, T2IPromptHandling.PromptTagContext, string>> promptLength = new(T2IPromptHandling.PromptTagLengthEstimators);

        try
        {
            if (!PromptRegion.CustomPartPrefixes.Contains("videoclip"))
            {
                PromptRegion.RegisterCustomPrefix("videoclip");
            }
            T2IPromptHandling.PromptTagBasicProcessors["videoclip"] = (_, context) =>
            {
                context.SectionID = 58823;
                return $"<videoclip//cid={context.SectionID}>";
            };
            T2IPromptHandling.PromptTagLengthEstimators["videoclip"] = (_, _) => "<break>";

            T2IParamInput input = BuildEditInput(
                "Base",
                enableBase2EditGroup: true,
                prompt: "sonic the hedgehog\n<edit>Castlevania\n<videoclip>He is annihilate"
            );
            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
            List<string> prompts = CollectEncoderPrompts(bridge);

            Assert.Contains(prompts, prompt => prompt.Trim() == "Castlevania");
            Assert.DoesNotContain(prompts, prompt => prompt.Contains("He is annihilate", StringComparison.Ordinal));
            Assert.DoesNotContain(prompts, prompt => prompt.Contains("<videoclip", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            PromptRegion.CustomPartPrefixes = customPartPrefixes;
            PromptRegion.PartPrefixes = partPrefixes;
            T2IPromptHandling.PromptTagBasicProcessors = promptBasic;
            T2IPromptHandling.PromptTagLengthEstimators = promptLength;
        }
    }

    [Fact]
    public void EditStage_falls_back_to_global_prompt_when_no_edit_tags_exist()
    {
        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: "global prompt only");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotEmpty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.NotEmpty(WorkflowQuery.Samplers(bridge));

        List<string> prompts = CollectEncoderPrompts(bridge);

        Assert.Contains(prompts, p => p.Contains("global prompt only"));
    }

    [Fact]
    public void B2EPrompt_base_reference_uses_base_prompt_text()
    {
        T2IParamInput input = BuildEditInput(
            "Base",
            enableBase2EditGroup: true,
            prompt: "global prompt <base>base prompt <refiner>refiner prompt <edit[0]><b2eprompt[base]>"
        );
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<string> prompts = CollectEncoderPrompts(bridge);

        Assert.Contains(prompts, p => p.Contains("base prompt"));
        Assert.DoesNotContain(prompts, p => p.Contains("global prompt"));
        Assert.DoesNotContain(prompts, p => p.Contains("refiner prompt"));
        Assert.DoesNotContain(prompts, p => p.Contains("<b2eprompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void B2EPrompt_named_stage_reference_falls_back_to_global_when_stage_missing()
    {
        T2IParamInput input = BuildEditInput(
            "Base",
            enableBase2EditGroup: true,
            prompt: "global prompt <edit[0]><b2eprompt[base]>"
        );
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<string> prompts = CollectEncoderPrompts(bridge);

        Assert.Contains(prompts, p => p.Contains("global prompt"));
        Assert.DoesNotContain(prompts, p => p.Contains("<b2eprompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void B2EPrompt_numeric_reference_uses_final_processed_prompt_and_falls_back_when_missing()
    {
        T2IParamInput input = BuildEditInput(
            "Base",
            enableBase2EditGroup: true,
            prompt: "global prompt <edit[0]><b2eprompt[1]> <b2eprompt[5]> <edit[1]>stage1 <random:resolved>"
        );

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 20,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> prompts = CollectEncoderPrompts(bridge);

        Assert.Contains(prompts, p => p.Contains("stage1 resolved"));
        Assert.Contains(prompts, p => p.Contains("global prompt"));
        Assert.DoesNotContain(prompts, p => p.Contains("<random", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(prompts, p => p.Contains("<b2eprompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Stage_specific_edit_sections_apply_only_to_target_stage_and_global_applies_to_all()
    {
        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: "global <edit>GLOBAL <base>ignore <edit[1]>STAGE1");

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 20,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<string> prompts = CollectEncoderPrompts(bridge);

        Assert.Contains(prompts, p => p.Contains("GLOBAL") && !p.Contains("STAGE1"));
        Assert.Contains(prompts, p => p.Contains("GLOBAL") && p.Contains("STAGE1"));
    }

    [Fact]
    public void Edit_stage_with_only_tags_falls_back_to_global_prompt_for_stage0()
    {
        T2IParamInput input = BuildEditInput(
            "Base",
            enableBase2EditGroup: true,
            prompt: "global prompt <edit[0]><setvar[tmp,false]:only-tag>"
        );

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> prompts = CollectEncoderPrompts(bridge);

        Assert.Contains(prompts, p => p.Contains("global prompt", StringComparison.Ordinal));
        Assert.DoesNotContain(prompts, p => p.Contains("only-tag", StringComparison.Ordinal));
    }

    [Fact]
    public void Edit_stage_with_only_tags_falls_back_to_previous_edit_stage_then_global()
    {
        T2IParamInput input = BuildEditInput(
            "Base",
            enableBase2EditGroup: true,
            prompt: "global prompt <edit[0]>stage0 text <edit[1]><setvar[tmp,false]:only-tag>"
        );

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 20,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        List<string> samplerPrompts = [];
        foreach (ComfyNode sampler in samplers)
        {
            KSamplerAdvancedNode ks = Assert.IsType<KSamplerAdvancedNode>(sampler);
            ComfyNode positiveNode = ks.Positive.Connection?.Node
                ?? throw new InvalidOperationException($"Sampler {ks.Id} has no positive connection.");

            SwarmClipTextEncodeAdvancedNode encoder;
            if (positiveNode is ReferenceLatentNode refLatent)
            {
                encoder = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(
                    Assert.IsType<ReferenceLatentNode>(positiveNode).Inputs
                        .Single(i => i.Name == "conditioning").Connection?.Node);
            }
            else
            {
                encoder = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(positiveNode);
            }

            if (encoder.Prompt.LiteralValue is string prompt)
            {
                samplerPrompts.Add(prompt);
            }
        }

        Assert.Equal(2, samplerPrompts.Count);
        Assert.All(samplerPrompts, p => Assert.Contains("stage0 text", p, StringComparison.Ordinal));
        Assert.DoesNotContain(samplerPrompts, p => p.Contains("only-tag", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_apply_after_edit_alias_and_stage_label_are_equivalent()
    {
        static JObject MakeStage(string applyAfter) => new()
        {
            ["applyAfter"] = applyAfter,
            ["keepPreEditImage"] = false,
            ["control"] = 1.0,
            ["model"] = ModelPrep.UseRefiner,
            ["vae"] = "None",
            ["steps"] = 20,
            ["cfgScale"] = 7.0,
            ["sampler"] = "euler",
            ["scheduler"] = "normal"
        };

        static void AssertStageChain(JObject workflow)
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
            IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
            Assert.Equal(2, samplers.Count);

            KSamplerAdvancedNode stage0Sampler = Assert.IsType<KSamplerAdvancedNode>(
                samplers.Single(s => s is KSamplerAdvancedNode ks && ks.LatentImage.Connection?.Node.Id == "10"));
            KSamplerAdvancedNode stage1Sampler = Assert.IsType<KSamplerAdvancedNode>(
                samplers.Single(s => s is KSamplerAdvancedNode ks && ks.LatentImage.Connection?.Node.Id == stage0Sampler.Id));

            Assert.NotEqual(stage0Sampler.Id, stage1Sampler.Id);
        }

        T2IParamInput aliasInput = BuildEditInput(
            "Base",
            enableBase2EditGroup: true,
            prompt: "global <edit[0]>stage0 text <edit[1]>stage1 text"
        );
        aliasInput.Set(Base2EditExtension.EditStages, new JArray(MakeStage("edit0")).ToString());

        T2IParamInput stageLabelInput = BuildEditInput(
            "Base",
            enableBase2EditGroup: true,
            prompt: "global <edit[0]>stage0 text <edit[1]>stage1 text"
        );
        stageLabelInput.Set(Base2EditExtension.EditStages, new JArray(MakeStage("Edit Stage 0")).ToString());

        JObject aliasWorkflow = WorkflowTestHarness.GenerateWithSteps(aliasInput, BaseSteps());
        JObject stageLabelWorkflow = WorkflowTestHarness.GenerateWithSteps(stageLabelInput, BaseSteps());

        AssertStageChain(aliasWorkflow);
        AssertStageChain(stageLabelWorkflow);
    }

    [Fact]
    public void Published_edit_stage_refs_are_written_as_json_payloads()
    {
        T2IParamInput input = BuildEditInput(
            "Base",
            enableBase2EditGroup: true,
            prompt: "global <edit[0]>stage0 text <edit[1]>stage1 text"
        );
        input.Set(Base2EditExtension.EditStages, new JArray(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 20,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        ).ToString());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BaseSteps());

        Assert.NotNull(workflow);
        Assert.True(generator.NodeHelpers.TryGetValue("b2e.published.edit.0", out string stage0Payload));
        Assert.True(generator.NodeHelpers.TryGetValue("b2e.published.edit.1", out string stage1Payload));

        JObject stage0 = JObject.Parse(stage0Payload);
        JObject stage1 = JObject.Parse(stage1Payload);
        Assert.Equal(WGNodeData.DT_VAE, $"{stage0["vae"]?["dataType"]}");
        Assert.Equal(WGNodeData.DT_VAE, $"{stage1["vae"]?["dataType"]}");
        Assert.True(stage0["media"]?["path"] is JArray stage0Path && stage0Path.Count == 2);
        Assert.True(stage1["media"]?["path"] is JArray stage1Path && stage1Path.Count == 2);
        Assert.False(JToken.DeepEquals(stage0["media"]?["path"], stage1["media"]?["path"]));
    }

    [Fact]
    public void Stage_fallback_prompt_from_setvar_false_does_not_include_leading_lt()
    {
        const string portrait = "a portrait of a character in a scenic environment by Tom Everhart";
        string prompt = "<setvar[mplength, false]:30-50>\n"
            + "<setvar[style, false]:Mixed media in the style of Jim Dine, iconic personal motifs like hearts and robes, "
            + "expressive mixed-media assemblage, raw and heavily textured gestural marks, expressive light, bold color "
            + "palette, autobiographical and emotionally raw.>\n\n"
            + $"{portrait}\n\n"
            + "<edit[0]>aaa";

        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: prompt);
        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Refiner",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.4,
                ["model"] = ModelPrep.UseRefiner,
                ["steps"] = 10,
                ["cfgScale"] = 6.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> prompts = CollectEncoderPrompts(bridge);

        Assert.Contains(prompts, p => p.Trim() == "aaa");
        Assert.Contains(prompts, p => p.Contains(portrait, StringComparison.Ordinal) && !p.TrimStart().StartsWith('<'));
        Assert.DoesNotContain(prompts, p => p.Contains(portrait, StringComparison.Ordinal) && p.TrimStart().StartsWith('<'));
    }

    private static void AssertPreEditSaveWiredToBaseLatent(WorkflowBridge bridge, SaveImageNode save)
    {
        VAEDecodeNode preEditDecode = Assert.IsType<VAEDecodeNode>(save.Images.Connection?.Node);
        Assert.Equal("10", preEditDecode.Samples.Connection?.Node.Id);

        ComfyNode sampler = RequireSingleSampler(bridge);
        KSamplerAdvancedNode ks = Assert.IsType<KSamplerAdvancedNode>(sampler);
        IReadOnlyList<VAEDecodeNode> postEditDecodes = WorkflowQuery.FindVaeDecodesBySamples(bridge, ks.Outputs[0]);
        Assert.NotEmpty(postEditDecodes);
        Assert.Contains(postEditDecodes, n => n.Id != preEditDecode.Id);
    }

    private static void AssertPreEditSaveWiredToBaseLatent(WorkflowBridge bridge, SwarmSaveImageWSNode save)
    {
        VAEDecodeNode preEditDecode = Assert.IsType<VAEDecodeNode>(save.Images.Connection?.Node);
        Assert.Equal("10", preEditDecode.Samples.Connection?.Node.Id);

        ComfyNode sampler = RequireSingleSampler(bridge);
        KSamplerAdvancedNode ks = Assert.IsType<KSamplerAdvancedNode>(sampler);
        IReadOnlyList<VAEDecodeNode> postEditDecodes = WorkflowQuery.FindVaeDecodesBySamples(bridge, ks.Outputs[0]);
        Assert.NotEmpty(postEditDecodes);
        Assert.Contains(postEditDecodes, n => n.Id != preEditDecode.Id);
    }

    [Fact]
    public void KeepPreEditImage_adds_save_image_node()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SaveImageNode save = Assert.Single(bridge.Graph.NodesOfType<SaveImageNode>());
        AssertPreEditSaveWiredToBaseLatent(bridge, save);
    }

    [Fact]
    public void KeepPreEditImage_with_comfy_saveimage_ws_uses_SwarmSaveImageWS()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat([new WorkflowGenerator.WorkflowGenStep(g => g.Features.Add("comfy_saveimage_ws"), -999)])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SaveImageNode>());
        SwarmSaveImageWSNode save = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveImageWSNode>());
        AssertPreEditSaveWiredToBaseLatent(bridge, save);
    }

    [Fact]
    public void No_pre_edit_flag_means_no_save_node()
    {
        T2IParamInput input = BuildEditInput("Base");

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        Assert.Empty(bridge.Graph.NodesOfType<SaveImageNode>());
    }

    [Fact]
    public void Refiner_edit_retargets_existing_save_node_to_post_edit_output()
    {
        T2IParamInput input = BuildEditInput("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "801", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([preDecode, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                    g.CreateNode(SaveImageNode.ClassType, new JObject()
                    {
                        ["images"] = g.CurrentMedia.Path
                    }, id: "900", idMandatory: false);
                }, 2)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SaveImageNode save = WorkflowAssertions.RequireNodeById<SaveImageNode>(bridge, "900");
        KSamplerAdvancedNode sampler = Assert.IsType<KSamplerAdvancedNode>(RequireSingleSampler(bridge));
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, sampler.Outputs[0]);
        Assert.Equal(postEditDecode.Id, save.Images.Connection?.Node.Id);
    }

    [Fact]
    public void KeepPreEditImage_preserves_preedit_save_and_retargets_existing_save_to_final_output()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preDecode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "801", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([preDecode, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                    g.CreateNode(SaveImageNode.ClassType, new JObject()
                    {
                        ["images"] = g.CurrentMedia.Path
                    }, id: "900", idMandatory: false);
                }, 2)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SaveImageNode existingSave = WorkflowAssertions.RequireNodeById<SaveImageNode>(bridge, "900");
        KSamplerAdvancedNode sampler = Assert.IsType<KSamplerAdvancedNode>(RequireSingleSampler(bridge));
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, sampler.Outputs[0]);
        Assert.Equal(postEditDecode.Id, existingSave.Images.Connection?.Node.Id);

        IReadOnlyList<SaveImageNode> saves = bridge.Graph.NodesOfType<SaveImageNode>();
        Assert.True(saves.Count >= 2, "Expected dedicated pre-edit save plus existing final save.");
        SaveImageNode preEditSave = saves.Single(s => s.Id != "900");
        VAEDecodeNode preEditDecode = Assert.IsType<VAEDecodeNode>(preEditSave.Images.Connection?.Node);
        Assert.Equal("10", preEditDecode.Samples.Connection?.Node.Id);
    }

    [Fact]
    public void Edit_only_image_input_encodes_to_latent_before_edit()
    {
        T2IParamInput input = BuildEditInput("Base");
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_EditOnly()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<VAEEncodeNode> encodes = bridge.Graph.NodesOfType<VAEEncodeNode>();
        Assert.NotEmpty(encodes);
        Assert.Contains(encodes, n => n.Pixels.Connection?.Node.Id == "11");

        ReferenceLatentNode refLatent = WorkflowAssertions.RequireNodeOfType<ReferenceLatentNode>(bridge);
        Assert.NotNull(refLatent.Latent.Connection);
        Assert.Contains(encodes, n => n.Id == refLatent.Latent.Connection.Node.Id);
    }

    [Fact]
    public void Image_only_input_final_edit_decodes_output()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_EditOnly()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = Assert.IsType<KSamplerAdvancedNode>(RequireSingleSampler(bridge));
        WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, sampler.Outputs[0]);
    }

    [Fact]
    public void Edit_upscale_default_does_not_inherit_refiner_upscale()
    {
        // Regression: when ApplyEditAfter=Refiner and RefinerUpscale!=1, leaving
        // EditUpscale at its default of 1 (which IgnoreIf strips from input) used to
        // fall through to refinerDefaults.Upscale, adding an unwanted ImageScale before
        // the implicit edit stage. Documented contract: "Setting to '1' disables the upscale."
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(T2IParamTypes.RefinerUpscale, 1.5);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<ImageScaleNode>());
    }

    [Fact]
    public void Edit_upscale_pixel_adds_imagescale_before_vaeencode()
    {
        T2IParamInput input = BuildEditInput("Base");
        input.Set(Base2EditExtension.EditUpscale, 2.0);
        input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode imageScale = WorkflowAssertions.RequireNodeOfType<ImageScaleNode>(bridge);
        VAEEncodeNode vaeEncode = WorkflowAssertions.RequireNodeOfType<VAEEncodeNode>(bridge);
        Assert.Equal(imageScale.Id, vaeEncode.Pixels.Connection?.Node.Id);

        KSamplerAdvancedNode sampler = Assert.IsType<KSamplerAdvancedNode>(RequireSingleSampler(bridge));
        Assert.Equal(vaeEncode.Id, sampler.LatentImage.Connection?.Node.Id);
    }

    private static (int Width, int Height) GetImageScaleResolution(ImageScaleNode scale)
    {
        int w = Convert.ToInt32(scale.Width.LiteralValue);
        int h = Convert.ToInt32(scale.Height.LiteralValue);
        return (w, h);
    }

    [Fact]
    public void Chained_edit_stages_apply_upscale_from_parent_stage_resolution()
    {
        T2IParamInput input = BuildEditInput("Base");
        input.Set(T2IParamTypes.Width, 1152);
        input.Set(T2IParamTypes.Height, 896);
        input.Set(Base2EditExtension.EditUpscale, 1.25);
        input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
        input.Set(Base2EditExtension.EditStages, new JArray(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 20,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        ).ToString());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));
        IReadOnlyList<ImageScaleNode> scales = bridge.Graph.NodesOfType<ImageScaleNode>();
        Assert.Equal(2, scales.Count);

        List<(int Width, int Height)> widthsHeights = scales
            .Select(GetImageScaleResolution)
            .OrderBy(v => v.Width)
            .ToList();

        Assert.Contains((1440, 1120), widthsHeights);
        Assert.Contains((1792, 1392), widthsHeights);
    }

    [Fact]
    public void Chained_final_step_edit_stages_apply_upscale_from_parent_stage_resolution()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(T2IParamTypes.Width, 1152);
        input.Set(T2IParamTypes.Height, 896);
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditUpscale, 1.25);
        input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
        input.Set(Base2EditExtension.EditStages, new JArray(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            },
            new JObject
            {
                ["applyAfter"] = "Edit Stage 1",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            }
        ).ToString());

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
            {
                WorkflowTestHarness.MinimalGraphSeedStep(),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string decode = g.CreateNode(VAEDecodeNode.ClassType, new JObject()
                    {
                        ["samples"] = g.CurrentMedia.Path,
                        ["vae"] = g.CurrentVae.Path
                    }, id: "1801", idMandatory: false);
                    string scaled = g.CreateNode(ImageScaleNode.ClassType, new JObject()
                    {
                        ["image"] = new JArray(decode, 0),
                        ["width"] = 1728,
                        ["height"] = 1344,
                        ["upscale_method"] = "lanczos",
                        ["crop"] = "disabled"
                    }, id: "1802", idMandatory: false);
                    g.CurrentMedia = new WGNodeData([scaled, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));
        IReadOnlyList<ImageScaleNode> scales = bridge.Graph.NodesOfType<ImageScaleNode>();

        List<(int Width, int Height)> widthsHeights = scales
            .Select(GetImageScaleResolution)
            .Where(v => v.Width >= 1728 && v.Height >= 1344 && !(v.Width == 1728 && v.Height == 1344))
            .OrderBy(v => v.Width)
            .ToList();

        Assert.Contains((2160, 1680), widthsHeights);
        Assert.Contains((2688, 2096), widthsHeights);
        Assert.Contains((3360, 2608), widthsHeights);
    }

    [Fact]
    public void Edit_upscale_latent_adds_latentupscaleby()
    {
        T2IParamInput input = BuildEditInput("Base");
        input.Set(Base2EditExtension.EditUpscale, 2.0);
        input.Set(Base2EditExtension.EditUpscaleMethod, "latent-bilinear");

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LatentUpscaleByNode latentUpscale = WorkflowAssertions.RequireNodeOfType<LatentUpscaleByNode>(bridge);
        Assert.Equal("10", latentUpscale.Samples.Connection?.Node.Id);

        KSamplerAdvancedNode sampler = Assert.IsType<KSamplerAdvancedNode>(RequireSingleSampler(bridge));
        Assert.Equal(latentUpscale.Id, sampler.LatentImage.Connection?.Node.Id);
    }

    [Fact]
    public void Edit_upscale_model_adds_model_loader_and_model_upscale_nodes()
    {
        T2IParamInput input = BuildEditInput("Base");
        input.Set(Base2EditExtension.EditUpscale, 2.0);
        input.Set(Base2EditExtension.EditUpscaleMethod, "model-UnitTestUpscaler.safetensors");

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        UpscaleModelLoaderNode loader = WorkflowAssertions.RequireNodeOfType<UpscaleModelLoaderNode>(bridge);
        ImageUpscaleWithModelNode modelUpscale = WorkflowAssertions.RequireNodeOfType<ImageUpscaleWithModelNode>(bridge);
        ImageScaleNode imageScale = WorkflowAssertions.RequireNodeOfType<ImageScaleNode>(bridge);

        Assert.Equal(loader.Id, modelUpscale.UpscaleModel.Connection?.Node.Id);
        Assert.Equal(modelUpscale.Id, imageScale.Image.Connection?.Node.Id);
    }
}
