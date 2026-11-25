using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

/// <summary>
/// Regression for the edit-stage model/VAEEncode id collision (payload nonversioned/1.json).
///
/// When an edit stage freshly loads a model and applies a LoRA, the LoRA node is minted via
/// <see cref="WorkflowGenerator.GetStableDynamicID"/>, which claims an id by scanning for the first
/// free one but does NOT advance <c>g.LastID</c>. Once the stable-id range (3000+) is densely filled,
/// that scan returns an id equal to <c>g.LastID</c>; the subsequent re-encode <c>VAEEncode</c>
/// (created with <c>id=null</c> → <c>LastID++</c>) then reuses the same id and silently overwrites the
/// LoRA node. The sampler's <c>model</c> input, still referencing that id, ends up wired to a LATENT,
/// surfacing as "Cannot connect output of type 'LATENT' to input 'model'".
///
/// This test recreates the production precondition deterministically: the 3000–3009 stable-id slots
/// are pre-filled and <c>LastID</c> parked at 3010, then a cross-compat edit stage (forces re-encode)
/// with a global LoRA runs. Without the <c>AdvancePastOccupiedIds</c> guard the generated graph fails strict
/// re-parse / wires a sampler model to a VAEEncode; with it, ids stay distinct.
/// </summary>
[Collection("Base2EditTests")]
public class BranchModelIdCollisionTests
{
    [Fact]
    public void Edit_stage_lora_node_is_not_overwritten_by_reencode_vaeencode()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        T2IModelCompatClass baseCompat = new() { ID = "sdxl", ShortCode = "SDXL" };
        T2IModelCompatClass editCompat = new() { ID = "sd15", ShortCode = "SD15" };
        T2IModelClass baseClass = new() { ID = "sdxl-base", Name = "SDXL Base", CompatClass = baseCompat, StandardWidth = 1024, StandardHeight = 1024 };
        T2IModelClass editClass = new() { ID = "sd15-base", Name = "SD 1.5 Base", CompatClass = editCompat, StandardWidth = 512, StandardHeight = 512 };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors") { ModelClass = baseClass };
        T2IModel editModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors") { ModelClass = editClass };
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        // Global lora (outside <edit>) so it applies to the freshly-loaded cross-compat edit model.
        input.Set(T2IParamTypes.Prompt, "global <lora:UnitTest_Lora:1.0> <edit>edit it");
        input.Set(Base2EditExtension.EditModel, "UnitTest_Edit.safetensors");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        // Park the stable-id range densely filled with LastID == next free stable id, exactly as a
        // multi-stage production run leaves it just before the edit stage loads its model + lora.
        WorkflowGenerator.WorkflowGenStep fillStableRange = new(g =>
        {
            for (int id = 3000; id <= 3009; id++)
            {
                g.Workflow[$"{id}"] = new JObject { ["class_type"] = "UnitTest_Filler", ["inputs"] = new JObject() };
            }
            g.LastID = 3010;
        }, -10);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat([fillStableRange])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        // Generation must not throw, and the resulting graph must re-parse strictly (no model<-LATENT).
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The edit sampler's model input must resolve to a model node, never a VAEEncode (LATENT).
        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.NotEmpty(samplers);
        foreach (KSamplerAdvancedNode sampler in samplers)
        {
            INodeOutput modelConn = sampler.Model.Connection;
            Assert.NotNull(modelConn);
            Assert.False(modelConn.Node is VAEEncodeNode,
                $"Sampler {sampler.Id} model input is wired to a VAEEncode (LATENT) instead of a model node.");
        }

        // And the LoRA node applied to the edit model must survive (not overwritten by a VAEEncode).
        Assert.NotEmpty(bridge.Graph.NodesOfType<LoraLoaderNode>());
    }
}
