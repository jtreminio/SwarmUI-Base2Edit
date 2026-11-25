using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;

namespace Base2Edit;

/// <summary>Keeps edit nodes separate from the host's automatic and stable numeric ID ranges.</summary>
internal static class EditNodeIds
{
    /// <summary>Adds a typed node without advancing the host counter past reserved LoRA or save IDs.</summary>
    public static T AddEditNode<T>(this WorkflowBridge bridge, T node) where T : ComfyNode
    {
        int index = bridge.Workflow.Count;
        string id;
        do
        {
            id = $"b2e_{index++}";
        }
        while (bridge.Workflow.ContainsKey(id));
        return bridge.AddNode(node, id);
    }

    /// <summary>Skips an immediate stable-ID collision without jumping past unrelated high IDs.</summary>
    public static void AdvancePastOccupiedIds(WorkflowGenerator generator)
    {
        while (generator.HasNode($"{generator.LastID}"))
        {
            generator.LastID++;
        }
    }
}
