using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

/// <summary>
/// Regression for the pre-edit "keep image" save-node id collision fixed in PR #1.
///
/// The pre-edit SaveImage node is minted with an explicit id from
/// <see cref="WorkflowGenerator.GetStableDynamicID"/> (base <c>1000 + PreEditImageSaveId</c>), which
/// claims a free id by scanning but does NOT advance <c>g.LastID</c>. When the stable-id range is
/// already densely filled and <c>LastID</c> happens to point at the id the scan returns, the very next
/// re-encode <c>VAEEncode</c> (created with <c>id=null</c> → <c>LastID++</c>) reuses that same id and
/// silently overwrites the save node. User-visible symptom: the stage's pre-edit image is missing and
/// the output image count is one short. The real-world trigger is two chained edit stages where a
/// non-final stage forces a re-encode (e.g. SDXL → Flux); this test recreates the exact precondition
/// deterministically with a single cross-compat edit stage that forces a re-encode.
///
/// The fix is <c>BridgeSync.SyncLastId(g)</c> immediately after the save, advancing <c>LastID</c> past
/// the stable id so the re-encode <c>VAEEncode</c> cannot land on it. Without the fix the pre-edit
/// SaveImage node is replaced by a VAEEncode and the assertions below fail.
/// </summary>
[Collection("Base2EditTests")]
public class PreEditSaveIdCollisionTests
{
    // GetStableDynamicID(PreEditImageSaveId, stageIndex=0) scans upward from this id.
    private const int PreEditSaveIdScanStart = 1000 + 50200;

    [Fact]
    public void Pre_edit_save_node_is_not_overwritten_by_reencode_vaeencode()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        T2IModelCompatClass baseCompat = new() { ID = "sdxl", ShortCode = "SDXL" };
        T2IModelCompatClass editCompat = new() { ID = "sd15", ShortCode = "SD15" };
        T2IModelClass baseClass = new() { ID = "sdxl-base", Name = "SDXL Base", CompatClass = baseCompat, StandardWidth = 1024, StandardHeight = 1024 };
        T2IModelClass editClass = new() { ID = "sd15-base", Name = "SD 1.5 Base", CompatClass = editCompat, StandardWidth = 512, StandardHeight = 512 };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors") { ModelClass = baseClass };
        T2IModel editModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors") { ModelClass = editClass };
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.Prompt, "base <edit>edit it");
        // Cross-compat edit model (sdxl -> sd15) forces ReencodeIfNeeded to mint a re-encode VAEEncode.
        input.Set(Base2EditExtension.EditModel, "UnitTest_Edit.safetensors");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        // The save node only exists when the pre-edit image is kept.
        input.Set(Base2EditExtension.KeepPreEditImage, true);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        // Park the stable-id range densely filled with LastID at the next free stable id, exactly as a
        // production run leaves it just before the edit stage saves its pre-edit image and re-encodes.
        WorkflowGenerator.WorkflowGenStep fillStableRange = new(g =>
        {
            for (int id = PreEditSaveIdScanStart; id <= PreEditSaveIdScanStart + 9; id++)
            {
                g.Workflow[$"{id}"] = new JObject { ["class_type"] = "UnitTest_Filler", ["inputs"] = new JObject() };
            }
            g.LastID = PreEditSaveIdScanStart + 10;
        }, -10);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat([fillStableRange])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // A re-encode VAEEncode must have been minted (otherwise the test isn't exercising the collision).
        Assert.NotEmpty(bridge.Graph.NodesOfType<VAEEncodeNode>());

        // The pre-edit SaveImage node must survive that re-encode (not be overwritten by a VAEEncode
        // reusing its id). Without the SyncLastId fix this collection is empty — the save is clobbered
        // and the output is one image short.
        Assert.NotEmpty(bridge.Graph.NodesOfType<SaveImageNode>());
    }
}
