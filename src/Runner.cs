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

        if (!isFinalStep)
        {
            store.ResetStore();
        }

        new EditStage(g, store).Run(isFinalStep);

        if (isFinalStep)
        {
            store.DeleteStore();
        }
    }

    // Extension is considered ACTIVE when the root-level (stage0) "Edit Model" param is present.
    // When ACTIVE, stage0 is ALWAYS included. Additional stages are optional and come from JSON.
    // There is no scenario where stage1+ arrives without stage0 (stage0 comes from root-level fields).
    private bool IsExtensionActive()
    {
        T2IParamType type = Base2EditExtension.EditModel?.Type;
        return type is not null && g.UserInput.TryGetRaw(type, out _);
    }
}
