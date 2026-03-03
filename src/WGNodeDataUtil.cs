using SwarmUI.Builtin_ComfyUIBackend;

namespace Base2Edit;

public static class WGNodeDataUtil
{
    public static WGNodeData TryGetCurrentLatent(WorkflowGenerator g)
    {
        if (g.CurrentMedia?.IsLatentData == true)
        {
            return g.CurrentMedia;
        }

        return g.CurrentMedia?.AsLatentImage(g.CurrentVae) ?? null;
    }

    public static WGNodeData TryGetCurrentImage(WorkflowGenerator g)
    {
        if (g.CurrentMedia?.IsRawMedia == true)
        {
            return g.CurrentMedia;
        }

        return g.CurrentMedia?.AsRawImage(g.CurrentVae) ?? null;
    }
}