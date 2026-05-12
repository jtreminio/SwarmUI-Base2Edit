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
    private static JArray RequireConnectionInput(JObject node, params string[] preferredKeys)
    {
        if (node?["inputs"] is not JObject inputs)
        {
            Assert.Fail("Expected node to have an 'inputs' object.");
            return null;
        }

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

    private static void AssertHasAnyInputConnection(JObject node, JArray expectedRef, string because)
    {
        Assert.NotNull(node);
        Assert.NotNull(expectedRef);
        Assert.True(expectedRef.Count == 2, "expectedRef must be a [nodeId, outputIndex] pair.");

        Assert.True(node["inputs"] is JObject, "Expected node to have an 'inputs' object.");
        JObject inputs = (JObject)node["inputs"];

        bool found = inputs.Properties()
            .Select(p => p.Value)
            .OfType<JArray>()
            .Any(arr => JToken.DeepEquals(arr, expectedRef));

        Assert.True(found, because);
    }

    private static string RequireClassType(JObject workflow, string nodeId)
    {
        Assert.True(workflow.TryGetValue(nodeId, out JToken tok), $"Expected workflow node '{nodeId}' to exist.");
        JObject obj = tok as JObject;
        Assert.NotNull(obj);

        Assert.True(obj.TryGetValue("class_type", out JToken ctTok), $"Expected workflow node '{nodeId}' to have class_type.");
        return $"{ctTok}";
    }

    private static ComfyNode RequireSingleSampler(WorkflowBridge bridge) =>
        Assert.Single(WorkflowQuery.Samplers(bridge));

    private static List<string> CollectEncoderPrompts(WorkflowBridge bridge, JObject workflow)
    {
        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.NotEmpty(encoders);

        List<string> prompts = [];
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            if (workflow[enc.Id] is not JObject obj || obj["inputs"] is not JObject inputs)
            {
                continue;
            }
            if (inputs.TryGetValue("prompt", out JToken pTok) && pTok is JValue pVal && !string.IsNullOrWhiteSpace($"{pVal}"))
            {
                prompts.Add($"{pVal}");
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

        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        return input;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps()
    {
        return WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());
    }

    [Fact]
    public void ApplyEditAfter_controls_where_edit_stage_is_injected()
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

        {
            T2IParamInput input = BuildEditInput("Base");
            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

            ComfyNode sampler = Assert.Single(WorkflowQuery.Samplers(bridge));
            ComfyNode afterBase = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_ProbeAfterBase");
            ComfyNode afterFinal = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_ProbeAfterFinal");

            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput((JObject)workflow[afterBase.Id], "latent"));
            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput((JObject)workflow[afterFinal.Id], "latent"));
        }

        {
            T2IParamInput input = BuildEditInput("Refiner");
            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

            ComfyNode sampler = Assert.Single(WorkflowQuery.Samplers(bridge));
            ComfyNode afterBase = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_ProbeAfterBase");
            ComfyNode afterFinal = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_ProbeAfterFinal");

            Assert.Equal(new JArray("10", 0), RequireConnectionInput((JObject)workflow[afterBase.Id], "latent"));
            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput((JObject)workflow[afterFinal.Id], "latent"));
        }
    }

    [Fact]
    public void EditStage_runs_after_base_and_leaves_no_final_image()
    {
        T2IParamInput input = BuildEditInput("Base");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode refLatent = WorkflowAssertions.RequireNodeOfType<ReferenceLatentNode>(bridge);
        ComfyNode sampler = RequireSingleSampler(bridge);

        Assert.Equal(new JArray("10", 0), RequireConnectionInput((JObject)workflow[refLatent.Id], "latent"));
        AssertHasAnyInputConnection((JObject)workflow[sampler.Id], new JArray(refLatent.Id, 0), "Edit sampler must consume ReferenceLatent output (positive conditioning).");

        _ = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
    }

    [Fact]
    public void EditStage_runs_after_refiner_and_outputs_final_image()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode refLatent = WorkflowAssertions.RequireNodeOfType<ReferenceLatentNode>(bridge);
        ComfyNode sampler = RequireSingleSampler(bridge);

        Assert.Equal(new JArray("10", 0), RequireConnectionInput((JObject)workflow[refLatent.Id], "latent"));
        AssertHasAnyInputConnection((JObject)workflow[sampler.Id], new JArray(refLatent.Id, 0), "Edit sampler must consume ReferenceLatent output (positive conditioning).");

        VAEDecodeNode decode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
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
        Assert.Equal(new JArray("10", 0), RequireConnectionInput((JObject)workflow[refLatent.Id], "latent"));
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
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
        ComfyNode consumer = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_ImageConsumer");
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput((JObject)workflow[consumer.Id], "image"));
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
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
        ComfyNode chainConsumer = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_SeedVR2Like_A");
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput((JObject)workflow[chainConsumer.Id], "image"));

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
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
        ComfyNode chainConsumer = WorkflowAssertions.RequireNodeOfType(bridge, "UnitTest_SeedVR2Like_A");
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput((JObject)workflow[chainConsumer.Id], "image"));
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
        JArray chainInput = RequireConnectionInput((JObject)workflow[chainTail.Id], "image");
        Assert.Equal(VAEDecodeNode.ClassType, RequireClassType(workflow, $"{chainInput[0]}"));
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
        {
            if (workflow[s.Id] is not JObject obj || obj["inputs"] is not JObject inputs
                || !inputs.TryGetValue("positive", out JToken posTok) || posTok is not JArray posRef)
            {
                return false;
            }
            return RequireClassType(workflow, $"{posRef[0]}") == SwarmClipTextEncodeAdvancedNode.ClassType;
        });

        JArray latentRef = RequireConnectionInput((JObject)workflow[editSampler.Id], "latent_image", "latent");
        Assert.NotEqual("803", $"{latentRef[0]}");
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

        ComfyNode sampler = RequireSingleSampler(bridge);
        JArray latentRef = RequireConnectionInput((JObject)workflow[sampler.Id], "latent_image", "latent");
        Assert.Equal(VAEEncodeNode.ClassType, RequireClassType(workflow, $"{latentRef[0]}"));

        ComfyNode encode = WorkflowAssertions.RequireNodeById(bridge, $"{latentRef[0]}");
        Assert.Equal(new JArray("1802", 0), RequireConnectionInput((JObject)workflow[encode.Id], "pixels", "image"));

        ComfyNode segmentTailNode = WorkflowAssertions.RequireNodeById(bridge, "1802");
        Assert.Equal(new JArray("1801", 0), RequireConnectionInput((JObject)workflow[segmentTailNode.Id], "image"));
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

        ComfyNode sampler = RequireSingleSampler(bridge);
        JArray latentRef = RequireConnectionInput((JObject)workflow[sampler.Id], "latent_image", "latent");
        Assert.Equal(VAEEncodeNode.ClassType, RequireClassType(workflow, $"{latentRef[0]}"));

        ComfyNode encode = WorkflowAssertions.RequireNodeById(bridge, $"{latentRef[0]}");
        Assert.Equal(new JArray("1802", 0), RequireConnectionInput((JObject)workflow[encode.Id], "pixels", "image"));

        ComfyNode segmentTailNode = WorkflowAssertions.RequireNodeById(bridge, "1802");
        Assert.Equal(new JArray("1801", 0), RequireConnectionInput((JObject)workflow[segmentTailNode.Id], "image"));
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

        List<string> prompts = CollectEncoderPrompts(bridge, workflow);

        Assert.Contains(prompts, p => p.Contains("global") && !p.Contains("do stage1 edit"));
        Assert.DoesNotContain(prompts, p => p.Contains("do stage1 edit"));
    }

    [Fact]
    public void EditStage_runs_when_edit_stage0_tag_is_present()
    {
        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: "global <edit[0]>do stage0 edit");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotEmpty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.NotEmpty(WorkflowQuery.Samplers(bridge));
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
            List<string> prompts = CollectEncoderPrompts(bridge, workflow);

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

        List<string> prompts = CollectEncoderPrompts(bridge, workflow);

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

        List<string> prompts = CollectEncoderPrompts(bridge, workflow);

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

        List<string> prompts = CollectEncoderPrompts(bridge, workflow);

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
        List<string> prompts = CollectEncoderPrompts(bridge, workflow);

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

        List<string> prompts = CollectEncoderPrompts(bridge, workflow);

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
        List<string> prompts = CollectEncoderPrompts(bridge, workflow);

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
            JArray positiveRef = RequireConnectionInput((JObject)workflow[sampler.Id], "positive");
            string positiveNodeId = $"{positiveRef[0]}";
            string positiveClass = RequireClassType(workflow, positiveNodeId);

            JArray conditioningRef = positiveRef;
            if (positiveClass == ReferenceLatentNode.ClassType)
            {
                ComfyNode referenceLatent = WorkflowAssertions.RequireNodeById(bridge, positiveNodeId);
                conditioningRef = RequireConnectionInput((JObject)workflow[referenceLatent.Id], "conditioning");
            }

            ComfyNode encoder = WorkflowAssertions.RequireNodeById(bridge, $"{conditioningRef[0]}");
            Assert.Equal("SwarmClipTextEncodeAdvanced", RequireClassType(workflow, encoder.Id));
            if (workflow[encoder.Id] is JObject encObj
                && encObj["inputs"] is JObject encInputs
                && encInputs.TryGetValue("prompt", out JToken promptTok))
            {
                samplerPrompts.Add($"{promptTok}");
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

            ComfyNode stage0Sampler = samplers.Single(s =>
                JToken.DeepEquals(RequireConnectionInput((JObject)workflow[s.Id], "latent_image", "latent"), new JArray("10", 0)));
            ComfyNode stage1Sampler = samplers.Single(s =>
                JToken.DeepEquals(RequireConnectionInput((JObject)workflow[s.Id], "latent_image", "latent"), new JArray(stage0Sampler.Id, 0)));

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
            + "<setvar[style, false]:Mixed media in the style of Jim Dine, iconic personal motifs like hearts and robes, expressive mixed-media assemblage, raw and heavily textured gestural marks, expressive light, bold color palette, autobiographical and emotionally raw.>\n\n"
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
        List<string> prompts = CollectEncoderPrompts(bridge, workflow);

        Assert.Contains(prompts, p => p.Trim() == "aaa");
        Assert.Contains(prompts, p => p.Contains(portrait, StringComparison.Ordinal) && !p.TrimStart().StartsWith("<", StringComparison.Ordinal));
        Assert.DoesNotContain(prompts, p => p.Contains(portrait, StringComparison.Ordinal) && p.TrimStart().StartsWith("<", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepPreEditImage_adds_save_image_node()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<SaveImageNode> saves = bridge.Graph.NodesOfType<SaveImageNode>();
        Assert.Single(saves);
        SaveImageNode save = saves[0];
        JArray saveImagesRef = RequireConnectionInput((JObject)workflow[save.Id], "images");

        string preEditDecodeId = $"{saveImagesRef[0]}";
        Assert.Equal(VAEDecodeNode.ClassType, RequireClassType(workflow, preEditDecodeId));

        Assert.Equal(new JArray("10", 0), RequireConnectionInput((JObject)workflow[preEditDecodeId], "samples", "latent"));

        ComfyNode sampler = RequireSingleSampler(bridge);
        IReadOnlyList<VAEDecodeNode> postEditDecodes = WorkflowQuery.FindVaeDecodesBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
        Assert.NotEmpty(postEditDecodes);
        Assert.Contains(postEditDecodes, n => n.Id != preEditDecodeId);
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

        IReadOnlyList<SwarmSaveImageWSNode> wsSaves = bridge.Graph.NodesOfType<SwarmSaveImageWSNode>();
        Assert.Single(wsSaves);
        Assert.Empty(bridge.Graph.NodesOfType<SaveImageNode>());

        SwarmSaveImageWSNode save = wsSaves[0];
        JArray saveImagesRef = RequireConnectionInput((JObject)workflow[save.Id], "images");

        string preEditDecodeId = $"{saveImagesRef[0]}";
        Assert.Equal(VAEDecodeNode.ClassType, RequireClassType(workflow, preEditDecodeId));

        Assert.Equal(new JArray("10", 0), RequireConnectionInput((JObject)workflow[preEditDecodeId], "samples", "latent"));

        ComfyNode sampler = RequireSingleSampler(bridge);
        IReadOnlyList<VAEDecodeNode> postEditDecodes = WorkflowQuery.FindVaeDecodesBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
        Assert.NotEmpty(postEditDecodes);
        Assert.Contains(postEditDecodes, n => n.Id != preEditDecodeId);
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

        ComfyNode save = WorkflowAssertions.RequireNodeById(bridge, "900");
        ComfyNode sampler = RequireSingleSampler(bridge);
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput((JObject)workflow[save.Id], "images"));
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

        ComfyNode existingSave = WorkflowAssertions.RequireNodeById(bridge, "900");
        ComfyNode sampler = RequireSingleSampler(bridge);
        VAEDecodeNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput((JObject)workflow[existingSave.Id], "images"));

        IReadOnlyList<SaveImageNode> saves = bridge.Graph.NodesOfType<SaveImageNode>();
        Assert.True(saves.Count >= 2, "Expected dedicated pre-edit save plus existing final save.");
        SaveImageNode preEditSave = saves.Single(s => s.Id != "900");
        string preEditDecodeId = $"{RequireConnectionInput((JObject)workflow[preEditSave.Id], "images")[0]}";
        Assert.Equal(new JArray("10", 0), RequireConnectionInput((JObject)workflow[preEditDecodeId], "samples", "latent"));
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
        Assert.Contains(
            encodes,
            n => JToken.DeepEquals(((JObject)workflow[n.Id])["inputs"]["pixels"], new JArray("11", 0))
        );

        ReferenceLatentNode refLatent = WorkflowAssertions.RequireNodeOfType<ReferenceLatentNode>(bridge);
        JArray refLatentInput = RequireConnectionInput((JObject)workflow[refLatent.Id], "latent");
        Assert.Contains(encodes, n => n.Id == $"{refLatentInput[0]}");
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

        ComfyNode sampler = RequireSingleSampler(bridge);
        WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(sampler.Id, 0)));
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
        Assert.Equal(new JArray(imageScale.Id, 0), RequireConnectionInput((JObject)workflow[vaeEncode.Id], "pixels", "image"));

        ComfyNode sampler = RequireSingleSampler(bridge);
        Assert.Equal(new JArray(vaeEncode.Id, 0), RequireConnectionInput((JObject)workflow[sampler.Id], "latent_image", "latent"));
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        IReadOnlyList<ImageScaleNode> scales = bridge.Graph.NodesOfType<ImageScaleNode>();
        Assert.Equal(2, scales.Count);

        List<(int Width, int Height)> widthsHeights = scales
            .Select(s =>
            {
                JObject inputs = (JObject)workflow[s.Id]["inputs"];
                return (
                    Width: (int)inputs["width"],
                    Height: (int)inputs["height"]
                );
            })
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        IReadOnlyList<ImageScaleNode> scales = bridge.Graph.NodesOfType<ImageScaleNode>();

        List<(int Width, int Height)> widthsHeights = scales
            .Select(s =>
            {
                JObject inputs = (JObject)workflow[s.Id]["inputs"];
                return (
                    Width: (int)inputs["width"],
                    Height: (int)inputs["height"]
                );
            })
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
        Assert.Equal(new JArray("10", 0), RequireConnectionInput((JObject)workflow[latentUpscale.Id], "samples"));

        ComfyNode sampler = RequireSingleSampler(bridge);
        Assert.Equal(new JArray(latentUpscale.Id, 0), RequireConnectionInput((JObject)workflow[sampler.Id], "latent_image", "latent"));
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

        Assert.Equal(new JArray(loader.Id, 0), RequireConnectionInput((JObject)workflow[modelUpscale.Id], "upscale_model"));
        Assert.Equal(new JArray(modelUpscale.Id, 0), RequireConnectionInput((JObject)workflow[imageScale.Id], "image"));
    }
}
