using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace Base2Edit;

public class Runner(WorkflowGenerator g)
{
    public void Run(bool isFinalStep)
    {
        if (!IsExtensionActive())
        {
            return;
        }

        StageRefStore store = new(g);
        new EditStage(g, store).Run(isFinalStep);
    }

    private bool IsExtensionActive()
    {
        T2IParamType type = Base2EditExtension.EditModel?.Type;
        return type is not null && g.UserInput.TryGetRaw(type, out _);
    }
}
