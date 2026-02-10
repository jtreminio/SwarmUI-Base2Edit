using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class MetadataModelHashTests
{
    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps() =>
        WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());

    [Fact]
    public void Resolved_edit_model_is_tracked_in_sui_models_metadata()
    {
        using var ctx = new SwarmUiTestContext(disableImageMetadataModelHash: false);
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var vaeHandler = new T2IModelHandler { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };

        static T2IModel makeModel(T2IModelHandler handler, string name, string hash)
        {
            var model = new T2IModel(handler, "/tmp", $"/tmp/{name}", name)
            {
                Metadata = new T2IModelHandler.ModelMetadataStore
                {
                    ModelName = name,
                    Hash = hash
                }
            };
            handler.Models[name] = model;
            return model;
        }

        T2IModel baseModel = makeModel(sdHandler, "UnitTest_Base.safetensors", "0x111");
        T2IModel refinerModel = makeModel(sdHandler, "UnitTest_Refiner.safetensors", "0x222");
        T2IModel editModel = makeModel(sdHandler, "UnitTest_Edit.safetensors", "0x333");

        _ = WorkflowTestHarness.Base2EditSteps();
        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>edit text");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, refinerModel);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(Base2EditExtension.EditModel, editModel.Name);

        _ = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.True(input.TryGet(Base2EditExtension.EditModelResolvedForMetadata, out T2IModel tracked));
        Assert.Equal(editModel.Name, tracked.Name);
        T2IEngine.PostGenerateEvent?.Invoke(new T2IEngine.PostGenerationEventParams(null, input, () => { }));
        Assert.False(input.TryGet(Base2EditExtension.EditModelResolvedForMetadata, out _));
        Assert.True(input.TryGetRaw(Base2EditExtension.EditModel.Type, out object editModelRaw));
        T2IModel mappedEditModel = Assert.IsType<T2IModel>(editModelRaw);
        Assert.Equal(editModel.Name, mappedEditModel.Name);

        JObject fullMetadata = input.GenFullMetadataObject();
        JArray models = fullMetadata["sui_models"] as JArray;
        Assert.NotNull(models);

        JObject trackedModel = models
            .OfType<JObject>()
            .FirstOrDefault(m => $"{m["param"]}" == Base2EditExtension.EditModel.Type.ID);
        Assert.NotNull(trackedModel);
        Assert.Equal(editModel.Name, $"{trackedModel["name"]}");
        Assert.Equal("0x333", $"{trackedModel["hash"]}");
    }
}
