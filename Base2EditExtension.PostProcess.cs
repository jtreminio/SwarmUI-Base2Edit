using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace Base2Edit;

public partial class Base2EditExtension
{
    private static JArray ParseNodeRef(string value)
    {
        // Stored as "nodeId,outIndex"
        string[] parts = value.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out int outIndex))
        {
            throw new Exception($"Invalid node reference '{value}'");
        }
        return [parts[0], outIndex];
    }

    private static void FinalizeBase2EditWorkflow(WorkflowGenerator g)
    {
        if (!g.NodeHelpers.ContainsKey(Base2EditRanKey))
        {
            return;
        }

        if (IsEditOnlyWorkflow(g))
        {
            return;
        }

        ApplyOrderedEditSavesIfPresent(g);
        g.UsedInputs = null;
        g.RemoveClassIfUnused("Base2EditSavePreThenPassWS");
        g.RemoveClassIfUnused("VAEDecode");
        g.RemoveClassIfUnused("VAEDecodeTiled");
    }

    private static string FindFinalSwarmSaveImageNodeId(WorkflowGenerator g)
    {
        if (g.FinalImageOut is not null && g.FinalImageOut.Count == 2)
        {
            string targetNode = $"{g.FinalImageOut[0]}";
            int targetIndex = g.FinalImageOut[1].Value<int>();
            string best = null;
            int bestNum = int.MaxValue;
            g.RunOnNodesOfClass("SwarmSaveImageWS", (id, data) =>
            {
                JObject inputs = data["inputs"] as JObject;
                if (inputs?["images"] is JArray images
                    && images.Count == 2
                    && $"{images[0]}" == targetNode
                    && images[1].Value<int>() == targetIndex)
                {
                    if (!int.TryParse(id, out int idNum))
                    {
                        best ??= id;
                        return;
                    }
                    if (idNum < bestNum)
                    {
                        bestNum = idNum;
                        best = id;
                    }
                }
            });
            if (best is not null)
            {
                return best;
            }
        }

        // Fallbacks for known standard IDs
        if (g.Workflow.ContainsKey("9") && $"{g.Workflow["9"]?["class_type"]}" == "SwarmSaveImageWS")
        {
            return "9";
        }
        if (g.Workflow.ContainsKey("30") && $"{g.Workflow["30"]?["class_type"]}" == "SwarmSaveImageWS")
        {
            return "30";
        }

        // Last resort: if there is only one SwarmSaveImageWS, use it
        JProperty[] saves = g.NodesOfClass("SwarmSaveImageWS");
        if (saves.Length == 1)
        {
            return saves[0].Name;
        }

        return null;
    }

    private static void ApplyOrderedEditSavesIfPresent(WorkflowGenerator g)
    {
        if (!g.NodeHelpers.TryGetValue(PreEditImageOutKey, out string preEditOutStr))
        {
            return;
        }

        if (WorkflowGenerator.RestrictCustomNodes)
        {
            return;
        }

        string finalSaveId = FindFinalSwarmSaveImageNodeId(g);
        if (finalSaveId is null)
        {
            return;
        }
        JObject finalSaveNode = g.Workflow[finalSaveId] as JObject;
        if ($"{finalSaveNode?["class_type"]}" != "SwarmSaveImageWS")
        {
            return;
        }

        JArray preEditOut = ParseNodeRef(preEditOutStr);
        if (g.FinalImageOut is null)
        {
            return;
        }
        string helperId = g.GetStableDynamicID(PreEditImageSaveId, 1);
        string helperNode = g.CreateNode("Base2EditSavePreThenPassWS", new JObject()
        {
            ["pre_images"] = preEditOut,
            ["post_images"] = g.FinalImageOut,
            ["bit_depth"] = g.UserInput.Get(T2IParamTypes.BitDepth, "8bit")
        }, helperId);

        void SetInput(string nodeId, string key, JToken value)
        {
            JObject node = g.Workflow[nodeId] as JObject;
            JObject inputs = node["inputs"] as JObject;
            inputs[key] = value;
        }

        SetInput(finalSaveId, "images", new JArray() { helperNode, 0 });
    }
}

