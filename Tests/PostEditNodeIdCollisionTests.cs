using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

/// <summary>Base2Edit must not move core allocation into the stable LoRA or save ID ranges.</summary>
[Collection("Base2EditTests")]
public class PostEditNodeIdCollisionTests
{
    [Theory]
    [InlineData("pixel-lanczos", false)]
    [InlineData("latent-bilinear", false)]
    [InlineData("pixel-lanczos", true)]
    public void Seedvr_style_stage_after_edit_preserves_model_nodes(string upscaleMethod, bool keepPreEdit)
    {
        WorkflowTestHarness.Base2EditSteps();
        using SwarmUiTestContext context = new();
        T2IModelHandler models = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loras = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new() { ["Stable-Diffusion"] = models, ["LoRA"] = loras };
        T2IModel model = new(models, "/tmp", "/tmp/base.safetensors", "base.safetensors")
        {
            ModelClass = new T2IModelClass
            {
                ID = "unit-test-image",
                CompatClass = new T2IModelCompatClass { ID = "unit-test-image", LorasTargetTextEnc = false },
                StandardWidth = 1024,
                StandardHeight = 1024
            }
        };
        models.Models[model.Name] = model;
        foreach (string name in new[] { "first.safetensors", "second.safetensors" })
        {
            loras.Models[name] = new(loras, "/tmp", $"/tmp/{name}", name);
        }
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, model);
        input.Set(T2IParamTypes.Prompt, "base <edit>edit it");
        input.Set(T2IParamTypes.Loras, ["first", "second"]);
        input.Set(T2IParamTypes.LoraWeights, ["0.7", "1"]);
        input.Set(T2IParamTypes.Width, 832);
        input.Set(T2IParamTypes.Height, 1216);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Refiner");
        input.Set(Base2EditExtension.EditRefineOnly, true);
        input.Set(Base2EditExtension.EditUpscale, 1.5);
        input.Set(Base2EditExtension.EditUpscaleMethod, upscaleMethod);
        input.Set(Base2EditExtension.KeepPreEditImage, keepPreEdit);

        WorkflowGenerator.WorkflowGenStep baseLoras = new(g =>
        {
            (JArray path, _) = g.LoadLorasForConfinement(-1, g.CurrentModel.Path, g.CurrentTextEnc.Path);
            g.CurrentModel = g.CurrentModel.WithPath(path);
        }, -900);
        string conditioning = null, sampler = null;
        JArray seedvrModel = null;
        Dictionary<string, JToken> modelNodes = [];
        WorkflowGenerator.WorkflowGenStep seedvr = new(g =>
        {
            // Match the core SeedVR ordering: dynamic model/VAE, stable LoRAs, then dynamic media nodes.
            string loader = g.CreateNode("UNETLoader", new JObject { ["unet_name"] = "seedvr2.safetensors" });
            string vae = g.CreateNode("VAELoader", new JObject { ["vae_name"] = "seedvr2_vae.safetensors" });
            (seedvrModel, _) = g.LoadLorasForConfinement(-1, [loader, 0], null);
            foreach (JProperty node in g.Workflow.Properties().Where(p => p.Value["class_type"]?.Value<string>() == "LoraLoaderModelOnly"))
            {
                modelNodes[node.Name] = node.Value.DeepClone();
            }
            string encoded = g.CreateNode("VAEEncodeTiled", new JObject
            {
                ["vae"] = new JArray(vae, 0),
                ["pixels"] = g.CurrentMedia.AsRawImage(g.CurrentVae).Path
            });
            conditioning = g.CreateNode("SeedVR2Conditioning", new JObject { ["model"] = seedvrModel, ["vae_conditioning"] = new JArray(encoded, 0) });
            sampler = g.CreateNode("SwarmKSampler", new JObject
            {
                ["model"] = seedvrModel,
                ["positive"] = new JArray(conditioning, 0),
                ["negative"] = new JArray(conditioning, 1),
                ["latent_image"] = new JArray(encoded, 0)
            });
        }, 40);
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps = WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat([baseLoras])
            .Concat(WorkflowTestHarness.Base2EditSteps())
            .Concat([seedvr]);

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, steps);

        Assert.True(modelNodes.Count >= 4, "Both base and SeedVR LoRA chains must be present before sampling.");
        foreach ((string id, JToken original) in modelNodes)
        {
            Assert.True(JToken.DeepEquals(original, workflow[id]), $"LoRA node {id} was overwritten after the edit stage.");
        }
        WorkflowAssertions.AssertPathEquals(seedvrModel, (JArray)workflow[conditioning]["inputs"]["model"]);
        WorkflowAssertions.AssertPathEquals(seedvrModel, (JArray)workflow[sampler]["inputs"]["model"]);
        Assert.NotEqual(conditioning, seedvrModel[0].Value<string>());
        Assert.True(generator.LastID < 3000, $"Edit allocation advanced core LastID into a reserved range: {generator.LastID}.");
    }
}
