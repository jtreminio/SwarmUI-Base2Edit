using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Base2Edit;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

/// <summary>
/// Minimal harness to run only the Base2Edit workflow steps (not the full SwarmUI workflow pipeline),
/// and to assert on the generated ComfyUI workflow JSON (a node graph JObject).
/// </summary>
internal static class WorkflowTestHarness
{
    private static readonly object LockObj = new();
    private static bool _initialized;
    private static List<WorkflowGenerator.WorkflowGenStep> _base2EditSteps = [];

    private static void EnsureInitialized()
    {
        lock (LockObj)
        {
            if (_initialized)
            {
                return;
            }

            // Snapshot, init, then detect which steps were added by the extension.
            List<WorkflowGenerator.WorkflowGenStep> before = [.. WorkflowGenerator.Steps];

            if (T2IParamTypes.Width is null)
            {
                T2IParamTypes.RegisterDefaults();
            }

            var ext = new Base2EditExtension();
            ext.OnPreInit();
            ext.OnInit();

            List<WorkflowGenerator.WorkflowGenStep> after = [.. WorkflowGenerator.Steps];
            _base2EditSteps = after.Where(step => !before.Contains(step)).ToList();

            // Restore full steps list; tests will override Steps per-run.
            WorkflowGenerator.Steps = before;

            if (_base2EditSteps.Count == 0)
            {
                throw new InvalidOperationException("Base2Edit did not register any WorkflowGenerator steps during init.");
            }

            _initialized = true;
        }
    }

    public static IReadOnlyList<WorkflowGenerator.WorkflowGenStep> Base2EditSteps()
    {
        EnsureInitialized();
        return _base2EditSteps;
    }

    public static JObject GenerateWithSteps(T2IParamInput input, IEnumerable<WorkflowGenerator.WorkflowGenStep> steps)
    {
        EnsureInitialized();

        // Snapshot global state and restore after this generation.
        List<WorkflowGenerator.WorkflowGenStep> priorSteps = [.. WorkflowGenerator.Steps];

        try
        {
            WorkflowGenerator.Steps = [.. steps.OrderBy(s => s.Priority)];

            var gen = new WorkflowGenerator
            {
                UserInput = input,
                Features = [], // keep KSampler output stable (avoid SwarmKSampler path)
                ModelFolderFormat = "/"
            };

            return gen.Generate();
        }
        finally
        {
            WorkflowGenerator.Steps = priorSteps;
        }
    }

    public static (JObject Workflow, WorkflowGenerator Generator) GenerateWithStepsAndState(
        T2IParamInput input,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps)
    {
        EnsureInitialized();

        List<WorkflowGenerator.WorkflowGenStep> priorSteps = [.. WorkflowGenerator.Steps];
        try
        {
            WorkflowGenerator.Steps = [.. steps.OrderBy(s => s.Priority)];

            var gen = new WorkflowGenerator
            {
                UserInput = input,
                Features = [],
                ModelFolderFormat = "/"
            };

            JObject workflow = gen.Generate();
            return (workflow, gen);
        }
        finally
        {
            WorkflowGenerator.Steps = priorSteps;
        }
    }

    /// <summary>
    /// Step that seeds a minimal, valid-enough workflow graph and generator state for Base2Edit to operate.
    /// It avoids model loading / Program.* dependencies by leaving Model null.
    /// </summary>
    public static WorkflowGenerator.WorkflowGenStep MinimalGraphSeedStep() =>
        new(g =>
        {
            // Generate() initializes g.Workflow, but we set up reserved node IDs to emulate
            // the standard generator's state shape.
            _ = g.CreateNode("UnitTest_Model", new JObject(), id: "4", idMandatory: false);
            _ = g.CreateNode("UnitTest_Latent", new JObject(), id: "10", idMandatory: false);

            g.FinalModel = ["4", 0];
            g.FinalClip = ["4", 1];
            g.FinalVae = ["4", 2];
            g.FinalSamples = ["10", 0];
            g.FinalImageOut = null;
            g.FinalLoadedModel = null;
            g.FinalLoadedModelList = [];
        }, -1000);

    public static JObject RequireNodeOfType(JObject workflow, string classType)
    {
        WorkflowNode node = WorkflowUtils.RequireNodeOfType(workflow, classType);
        Assert.True(node.Node is not null, $"Expected node with class_type '{classType}' was not found.");
        return node.Node;
    }

    public static List<JObject> NodesOfType(JObject workflow, string classType) =>
        WorkflowUtils.NodesOfType(workflow, classType)
            .Select(node => node.Node)
            .Where(node => node is not null)
            .ToList();
}

