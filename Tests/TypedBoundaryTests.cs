using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class TypedBoundaryTests
{
    [Fact]
    public void WorkflowBridge_CanWrap_HarnessGeneratedWorkflow()
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(
            input,
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps()));

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotEmpty(bridge.Graph.Nodes);
    }
}
