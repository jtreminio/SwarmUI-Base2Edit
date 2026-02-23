using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class WorkflowTests
{
    private static IReadOnlyList<WorkflowNode> NodesOfAnyType(JObject workflow, params string[] classTypes) =>
        (classTypes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => WorkflowUtils.NodesOfType(workflow, t))
            .ToList();

    private static WorkflowNode RequireSingleNodeOfAnyType(JObject workflow, params string[] classTypes)
    {
        IReadOnlyList<WorkflowNode> nodes = NodesOfAnyType(workflow, classTypes);
        Assert.Single(nodes);
        return nodes[0];
    }

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

        // Fall back to "first connection-shaped input" for resilience against upstream key renames.
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

    private static WorkflowNode RequireSingleSampler(JObject workflow) =>
        RequireSingleNodeOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");

    private static List<string> CollectEncoderPrompts(JObject workflow)
    {
        IReadOnlyList<WorkflowNode> encoders = WorkflowUtils.NodesOfType(workflow, "SwarmClipTextEncodeAdvanced");
        Assert.NotEmpty(encoders);

        List<string> prompts = [];
        foreach (WorkflowNode enc in encoders)
        {
            if (enc.Node?["inputs"] is not JObject inputs)
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
        _ = WorkflowTestHarness.Base2EditSteps();
        var input = new T2IParamInput(null);
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
        _ = WorkflowTestHarness.Base2EditSteps();

        static WorkflowGenerator.WorkflowGenStep ProbeStep(string probeType, double priority) =>
            new(g =>
            {
                _ = g.CreateNode(probeType, new JObject
                {
                    ["latent"] = g.FinalSamples
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

        // Case A: apply after Base -> non-final step runs, final step doesn't.
        {
            T2IParamInput input = BuildEditInput("Base");
            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

            WorkflowNode sampler = RequireSingleNodeOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
            WorkflowNode afterBase = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ProbeAfterBase");
            WorkflowNode afterFinal = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ProbeAfterFinal");

            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput(afterBase.Node, "latent"));
            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput(afterFinal.Node, "latent"));
        }

        // Case B: apply after Refiner -> non-final step doesn't run, final step runs.
        {
            T2IParamInput input = BuildEditInput("Refiner");
            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

            WorkflowNode sampler = RequireSingleNodeOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
            WorkflowNode afterBase = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ProbeAfterBase");
            WorkflowNode afterFinal = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ProbeAfterFinal");

            Assert.Equal(new JArray("10", 0), RequireConnectionInput(afterBase.Node, "latent"));
            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput(afterFinal.Node, "latent"));
        }
    }

    [Fact]
    public void EditStage_runs_after_base_and_leaves_no_final_image()
    {
        T2IParamInput input = BuildEditInput("Base");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));
        var refLatent = WorkflowAssertions.RequireNodeOfType(workflow, "ReferenceLatent");
        WorkflowNode sampler = RequireSingleSampler(workflow);

        // Placement: the ReferenceLatent must read from the pre-edit latent (seed step's FinalSamples),
        // and the edit sampler must consume the ReferenceLatent output.
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(refLatent.Node, "latent"));
        AssertHasAnyInputConnection(sampler.Node, new JArray(refLatent.Id, 0), "Edit sampler must consume ReferenceLatent output (positive conditioning).");
    }

    [Fact]
    public void EditStage_runs_after_refiner_and_outputs_final_image()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.Single(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));
        WorkflowNode refLatent = WorkflowAssertions.RequireNodeOfType(workflow, "ReferenceLatent");
        WorkflowNode sampler = RequireSingleSampler(workflow);

        // Placement: ReferenceLatent must still read the pre-edit latent, and the sampler must consume it.
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(refLatent.Node, "latent"));
        AssertHasAnyInputConnection(sampler.Node, new JArray(refLatent.Id, 0), "Edit sampler must consume ReferenceLatent output (positive conditioning).");

        // Final placement: the VAEDecode should decode the post-edit latent (sampler output).
        WorkflowNode decode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(workflow, new JArray(sampler.Id, 0));
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
                    // Simulate an extension that mutates FinalSamples after decode by adding an image
                    // postprocess chain and re-encoding it. Base2Edit should still anchor at sampler/decode.
                    string decoded = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "801", idMandatory: false);
                    string post = g.CreateNode("UnitTest_PostProcessImage", new JObject()
                    {
                        ["image"] = new JArray(decoded, 0)
                    }, id: "802", idMandatory: false);
                    string encoded = g.CreateNode("VAEEncode", new JObject()
                    {
                        ["pixels"] = new JArray(post, 0),
                        ["vae"] = g.FinalVae
                    }, id: "803", idMandatory: false);
                    g.FinalImageOut = new JArray(post, 0);
                    g.FinalSamples = new JArray(encoded, 0);
                }, 2)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, steps);

        WorkflowNode refLatent = WorkflowAssertions.RequireNodeOfType(workflow, "ReferenceLatent");
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(refLatent.Node, "latent"));
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
                    // Pre-existing downstream image chain (simulates SeedVR2-style consumers) wired
                    // before Base2Edit final-stage run.
                    string preDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "801", idMandatory: false);
                    g.FinalImageOut = new JArray(preDecode, 0);
                    g.CreateNode("UnitTest_ImageConsumer", new JObject()
                    {
                        ["image"] = g.FinalImageOut
                    }, id: "802", idMandatory: false);
                }, 2)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, steps);

        WorkflowNode sampler = WorkflowAssertions.RequireNodeOfType(workflow, "KSamplerAdvanced");
        WorkflowNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(workflow, new JArray(sampler.Id, 0));
        WorkflowNode consumer = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ImageConsumer");
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput(consumer.Node, "image"));
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
                    // Simulate SeedVR2 timing/shape: decode current latent, build an image chain from that decode,
                    // and drift FinalImageOut to the chain output before Base2Edit final step executes.
                    string preDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "801", idMandatory: false);
                    string chainA = g.CreateNode("UnitTest_SeedVR2Like_A", new JObject()
                    {
                        ["image"] = new JArray(preDecode, 0)
                    }, id: "802", idMandatory: false);
                    string chainB = g.CreateNode("UnitTest_SeedVR2Like_B", new JObject()
                    {
                        ["image"] = new JArray(chainA, 0)
                    }, id: "803", idMandatory: false);
                    g.FinalImageOut = new JArray(chainB, 0);
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, steps);

        WorkflowNode sampler = WorkflowAssertions.RequireNodeOfType(workflow, "KSamplerAdvanced");
        WorkflowNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(workflow, new JArray(sampler.Id, 0));
        WorkflowNode chainConsumer = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_SeedVR2Like_A");
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput(chainConsumer.Node, "image"));

        // Final output should remain the downstream chain endpoint, not the edit decode.
        WorkflowNode chainTail = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_SeedVR2Like_B");
        Assert.Equal(new JArray(chainTail.Id, 0), generator.FinalImageOut);

        // Cleanup pass should remove the explicit pre-edit decode that got orphaned by rewiring.
        Assert.False(workflow.ContainsKey("801"));
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
                    // Build a downstream image chain from a decode, but do not assign g.FinalImageOut
                    // to the chain tail (mimics extensions that leave FinalImageOut stale/null)
                    string preDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
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

        WorkflowNode tail = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_SeedVR2Like_Tail");
        Assert.Equal(new JArray(tail.Id, 0), generator.FinalImageOut);
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
                    // Mimic a refiner-shaped handoff where a decode already exists before final-stage edit.
                    string refinerLatent = g.CreateNode("UnitTest_RefinerLatent", new JObject(), id: "23", idMandatory: false);
                    g.FinalSamples = new JArray(refinerLatent, 0);
                    string preEditDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "8", idMandatory: false);
                    g.FinalImageOut = new JArray(preEditDecode, 0);
                }, -100),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string chainA = g.CreateNode("UnitTest_SeedVR2Like_A", new JObject()
                    {
                        ["image"] = g.FinalImageOut
                    }, id: "802", idMandatory: false);
                    string chainB = g.CreateNode("UnitTest_SeedVR2Like_B", new JObject()
                    {
                        ["image"] = new JArray(chainA, 0)
                    }, id: "803", idMandatory: false);
                    g.FinalImageOut = new JArray(chainB, 0);
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        Assert.False(workflow.ContainsKey("8"));
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
                    // Create a downstream image branch that is also used to re-encode latent.
                    // Rewiring this branch to post-edit decode would create a cycle.
                    string preDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "801", idMandatory: false);
                    string chainTail = g.CreateNode("UnitTest_SeedVR2Like_Tail", new JObject()
                    {
                        ["image"] = new JArray(preDecode, 0)
                    }, id: "802", idMandatory: false);
                    string encoded = g.CreateNode("VAEEncode", new JObject()
                    {
                        ["pixels"] = new JArray(chainTail, 0),
                        ["vae"] = g.FinalVae
                    }, id: "803", idMandatory: false);
                    g.FinalImageOut = new JArray(chainTail, 0);
                    g.FinalSamples = new JArray(encoded, 0);
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        WorkflowNode chainTail = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_SeedVR2Like_Tail");
        JArray chainInput = RequireConnectionInput(chainTail.Node, "image");
        Assert.Equal("VAEDecode", RequireClassType(workflow, $"{chainInput[0]}"));
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
                    // Simulate refiner-shape drift:
                    // - samples still point at the refiner sampler output
                    // - image has moved into a downstream chain with an existing encode
                    string preDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "801", idMandatory: false);
                    string chainTail = g.CreateNode("UnitTest_SeedVR2Like_Tail", new JObject()
                    {
                        ["image"] = new JArray(preDecode, 0)
                    }, id: "802", idMandatory: false);
                    _ = g.CreateNode("VAEEncode", new JObject()
                    {
                        ["pixels"] = new JArray(chainTail, 0),
                        ["vae"] = g.FinalVae
                    }, id: "803", idMandatory: false);
                    g.FinalImageOut = new JArray(chainTail, 0);
                    // Keep FinalSamples unchanged to mimic "samples/image drift".
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        IReadOnlyList<WorkflowNode> samplers = NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        WorkflowNode editSampler = samplers.Single(s =>
        {
            if (s.Node?["inputs"] is not JObject inputs || !inputs.TryGetValue("positive", out JToken posTok) || posTok is not JArray posRef)
            {
                return false;
            }
            return RequireClassType(workflow, $"{posRef[0]}") == "SwarmClipTextEncodeAdvanced";
        });

        JArray latentRef = RequireConnectionInput(editSampler.Node, "latent_image", "latent");
        Assert.NotEqual("803", $"{latentRef[0]}");
    }

    [Fact]
    public void EditStage_refiner_hook_with_segments_runs_after_segment_image_tail()
    {
        // Repro shape:
        // - Refiner latent exists.
        // - A segment-like image node chain is already attached after refiner.
        // - Edit is also "after refiner".
        // Expected: edit should consume latent encoded from the segment tail image (not pre-segment latent/decode).
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
                    string refinerLatent = g.CreateNode("UnitTest_RefinerLatent", new JObject(), id: "23", idMandatory: false);
                    g.FinalSamples = new JArray(refinerLatent, 0);
                }, -400),
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string preSegmentDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "1801", idMandatory: false);
                    string segmentTail = g.CreateNode("UnitTest_SegmentAfterRefiner", new JObject()
                    {
                        ["image"] = new JArray(preSegmentDecode, 0)
                    }, id: "1802", idMandatory: false);
                    g.FinalImageOut = new JArray(segmentTail, 0);
                }, 5.8)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        WorkflowNode sampler = RequireSingleSampler(workflow);
        JArray latentRef = RequireConnectionInput(sampler.Node, "latent_image", "latent");
        Assert.Equal("VAEEncode", RequireClassType(workflow, $"{latentRef[0]}"));

        WorkflowNode encode = WorkflowAssertions.RequireNodeById(workflow, $"{latentRef[0]}");
        Assert.Equal(new JArray("1802", 0), RequireConnectionInput(encode.Node, "pixels", "image"));

        // Segment chain should remain upstream of edit (ie not rewired to post-edit decode).
        WorkflowNode segmentTailNode = WorkflowAssertions.RequireNodeById(workflow, "1802");
        Assert.Equal(new JArray("1801", 0), RequireConnectionInput(segmentTailNode.Node, "image"));
    }

    [Fact]
    public void EditStage_defaults_to_refiner_when_apply_after_missing()
    {
        // If ApplyEditAfter isn't present, Base2Edit should still run on the final step
        // (the param default is "Refiner") as long as Base2Edit is enabled.
        T2IParamInput input = BuildEditInput(null);
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.NotEmpty(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));
        Assert.NotEmpty(WorkflowUtils.NodesOfType(workflow, "SaveImage"));
    }

    [Fact]
    public void EditStage_does_not_run_when_base2edit_group_is_not_enabled()
    {
        T2IParamInput input = BuildEditInput(null, enableBase2EditGroup: false);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "SwarmKSampler"));
    }

    [Fact]
    public void EditStage_runs_and_falls_back_to_global_prompt_when_only_edit_stage1_tag_is_present()
    {
        // Stage0 has no <edit> / <edit[0]>, so it should fall back to the global prompt
        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: "global <edit[1]>do stage1 edit");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.NotEmpty(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));
        Assert.NotEmpty(NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler"));

        List<string> prompts = CollectEncoderPrompts(workflow);

        Assert.Contains(prompts, p => p.Contains("global") && !p.Contains("do stage1 edit"));
        Assert.DoesNotContain(prompts, p => p.Contains("do stage1 edit"));
    }

    [Fact]
    public void EditStage_runs_when_edit_stage0_tag_is_present()
    {
        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: "global <edit[0]>do stage0 edit");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.NotEmpty(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));
        Assert.NotEmpty(NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler"));
    }

    [Fact]
    public void EditStage_falls_back_to_global_prompt_when_no_edit_tags_exist()
    {
        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: "global prompt only");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.NotEmpty(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));
        Assert.NotEmpty(NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler"));

        List<string> prompts = CollectEncoderPrompts(workflow);

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

        List<string> prompts = CollectEncoderPrompts(workflow);

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

        List<string> prompts = CollectEncoderPrompts(workflow);

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

        var stages = new JArray(
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
        List<string> prompts = CollectEncoderPrompts(workflow);

        Assert.Contains(prompts, p => p.Contains("stage1 resolved"));
        Assert.Contains(prompts, p => p.Contains("global prompt"));
        Assert.DoesNotContain(prompts, p => p.Contains("<random", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(prompts, p => p.Contains("<b2eprompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Stage_specific_edit_sections_apply_only_to_target_stage_and_global_applies_to_all()
    {
        // stage0 + stage1 chained in Base hook
        T2IParamInput input = BuildEditInput("Base", enableBase2EditGroup: true, prompt: "global <edit>GLOBAL <base>ignore <edit[1]>STAGE1");

        // Add stage1
        var stages = new JArray(
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

        List<string> prompts = CollectEncoderPrompts(workflow);

        Assert.Contains(prompts, p => p.Contains("GLOBAL") && !p.Contains("STAGE1"));
        Assert.Contains(prompts, p => p.Contains("GLOBAL") && p.Contains("STAGE1"));
    }

    [Fact]
    public void KeepPreEditImage_adds_save_image_node()
    {
        // Use a "final step" run so we can assert SaveImage is wired to the *pre-edit* decode
        // and the generator's FinalImageOut is wired to the *post-edit* decode.
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        IReadOnlyList<WorkflowNode> saves = WorkflowUtils.NodesOfType(workflow, "SaveImage");
        Assert.Single(saves);
        WorkflowNode save = saves[0];
        JArray saveImagesRef = RequireConnectionInput(save.Node, "images");

        // SaveImage.images should come from a VAEDecode node that decodes the pre-edit latent.
        string preEditDecodeId = $"{saveImagesRef[0]}";
        Assert.Equal("VAEDecode", RequireClassType(workflow, preEditDecodeId));

        WorkflowNode preEditDecodeNode = new(preEditDecodeId, (JObject)workflow[preEditDecodeId]);
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(preEditDecodeNode.Node, "samples", "latent"));

        // And there should be a distinct VAEDecode decoding the post-edit latent (sampler output).
        WorkflowNode sampler = RequireSingleSampler(workflow);
        IReadOnlyList<WorkflowNode> postEditDecodes = WorkflowUtils.FindVaeDecodesBySamples(workflow, new JArray(sampler.Id, 0));
        Assert.NotEmpty(postEditDecodes);
        Assert.Contains(postEditDecodes, n => n.Id != preEditDecodeId);
    }

    [Fact]
    public void KeepPreEditImage_with_comfy_saveimage_ws_uses_SwarmSaveImageWS()
    {
        _ = WorkflowTestHarness.Base2EditSteps();

        var input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat([new WorkflowGenerator.WorkflowGenStep(g => g.Features.Add("comfy_saveimage_ws"), -999)])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        IReadOnlyList<WorkflowNode> wsSaves = WorkflowUtils.NodesOfType(workflow, "SwarmSaveImageWS");
        Assert.Single(wsSaves);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "SaveImage"));

        WorkflowNode save = wsSaves[0];
        JArray saveImagesRef = RequireConnectionInput(save.Node, "images");

        // SwarmSaveImageWS.images should come from a VAEDecode node decoding the pre-edit latent.
        string preEditDecodeId = $"{saveImagesRef[0]}";
        Assert.Equal("VAEDecode", RequireClassType(workflow, preEditDecodeId));

        WorkflowNode preEditDecodeNode = new(preEditDecodeId, (JObject)workflow[preEditDecodeId]);
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(preEditDecodeNode.Node, "samples", "latent"));

        // And there should be a distinct post-edit decode from the sampler output.
        WorkflowNode sampler = RequireSingleSampler(workflow);
        IReadOnlyList<WorkflowNode> postEditDecodes = WorkflowUtils.FindVaeDecodesBySamples(workflow, new JArray(sampler.Id, 0));
        Assert.NotEmpty(postEditDecodes);
        Assert.Contains(postEditDecodes, n => n.Id != preEditDecodeId);
    }

    [Fact]
    public void No_pre_edit_flag_means_no_save_node()
    {
        T2IParamInput input = BuildEditInput("Base");

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "SaveImage"));
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
                    string preDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "801", idMandatory: false);
                    g.FinalImageOut = new JArray(preDecode, 0);
                    g.CreateNode("SaveImage", new JObject()
                    {
                        ["images"] = g.FinalImageOut
                    }, id: "900", idMandatory: false);
                }, 2)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        WorkflowNode save = WorkflowAssertions.RequireNodeById(workflow, "900");
        WorkflowNode sampler = RequireSingleSampler(workflow);
        WorkflowNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(workflow, new JArray(sampler.Id, 0));
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput(save.Node, "images"));
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
                    string preDecode = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["vae"] = g.FinalVae
                    }, id: "801", idMandatory: false);
                    g.FinalImageOut = new JArray(preDecode, 0);
                    g.CreateNode("SaveImage", new JObject()
                    {
                        ["images"] = g.FinalImageOut
                    }, id: "900", idMandatory: false);
                }, 2)
            }
            .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        WorkflowNode existingSave = WorkflowAssertions.RequireNodeById(workflow, "900");
        WorkflowNode sampler = RequireSingleSampler(workflow);
        WorkflowNode postEditDecode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(workflow, new JArray(sampler.Id, 0));
        Assert.Equal(new JArray(postEditDecode.Id, 0), RequireConnectionInput(existingSave.Node, "images"));

        IReadOnlyList<WorkflowNode> saves = WorkflowUtils.NodesOfType(workflow, "SaveImage");
        Assert.True(saves.Count >= 2, "Expected dedicated pre-edit save plus existing final save.");
        WorkflowNode preEditSave = saves.Single(s => s.Id != "900");
        string preEditDecodeId = $"{RequireConnectionInput(preEditSave.Node, "images")[0]}";
        WorkflowNode preEditDecodeNode = new(preEditDecodeId, (JObject)workflow[preEditDecodeId]);
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(preEditDecodeNode.Node, "samples", "latent"));
    }

    [Fact]
    public void Edit_only_image_input_encodes_to_latent_before_edit()
    {
        T2IParamInput input = BuildEditInput("Base");
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_EditOnly()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));

        // Edit-only: when we start with an image and no latents, the edit stage must VAE-encode.
        IReadOnlyList<WorkflowNode> encodes = WorkflowUtils.NodesOfType(workflow, "VAEEncode");
        Assert.Single(encodes);
        WorkflowNode encode = encodes[0];

        WorkflowNode refLatent = WorkflowAssertions.RequireNodeOfType(workflow, "ReferenceLatent");
        Assert.Equal(new JArray(encode.Id, 0), RequireConnectionInput(refLatent.Node, "latent"));
    }

    [Fact]
    public void Image_only_input_final_edit_decodes_output()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_EditOnly()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        WorkflowNode sampler = RequireSingleSampler(workflow);
        WorkflowAssertions.RequireSingleVaeDecodeBySamples(workflow, new JArray(sampler.Id, 0));
    }
}
