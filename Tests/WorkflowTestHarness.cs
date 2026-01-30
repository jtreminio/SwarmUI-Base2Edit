using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

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

            UnitTestStubs.EnsureComfySetClipDeviceRegistered();
            UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

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
            input.ApplyLateSpecialLogic();

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
            input.ApplyLateSpecialLogic();

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

    /// <summary>
    /// Seed step for "edit-only" style flows: an input image exists but there are no latents yet.
    /// This forces Base2Edit to VAE-encode before sampling.
    /// </summary>
    public static WorkflowGenerator.WorkflowGenStep ImageOnlySeedStep() =>
        new(g =>
        {
            string imageNode = g.CreateNode("UnitTest_Image", new JObject(), id: "11", idMandatory: false);
            g.FinalImageOut = [imageNode, 0];
            g.FinalInputImage = [imageNode, 0];
            g.FinalSamples = null;
        }, -900);

    /// <summary>
    /// Adds a decode node to produce an image output for the current samples+VAE.
    /// Useful for "base-only image" templates (latents already exist).
    /// </summary>
    public static WorkflowGenerator.WorkflowGenStep DecodeSamplesToImageStep() =>
        new(g =>
        {
            if (g.FinalSamples is null || g.FinalVae is null)
            {
                return;
            }
            string decodeNode = g.CreateVAEDecode(g.FinalVae, g.FinalSamples);
            g.FinalImageOut = [decodeNode, 0];
            g.FinalInputImage ??= [decodeNode, 0];
        }, -950);

    /// <summary>Common workflow templates for tests.</summary>
    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseOnlyLatents() =>
        new[] { MinimalGraphSeedStep() };

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseOnlyImage() =>
        new[] { MinimalGraphSeedStep(), DecodeSamplesToImageStep() };

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_EditOnly() =>
        new[] { MinimalGraphSeedStep(), ImageOnlySeedStep() };

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenUpscale() =>
        new WorkflowGenerator.WorkflowGenStep[]
        {
            MinimalGraphSeedStep(),
            DecodeSamplesToImageStep(),
            new(g =>
            {
                // Minimal stub: "upscale" produces a new latent and clears FinalImageOut (as many pipelines do).
                string upLatent = g.CreateNode("UnitTest_UpscaleLatent", new JObject(), idMandatory: false);
                g.FinalSamples = [upLatent, 0];
                g.FinalImageOut = null;
            }, -500)
        };

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenRefiner() =>
        new WorkflowGenerator.WorkflowGenStep[]
        {
            MinimalGraphSeedStep(),
            DecodeSamplesToImageStep(),
            new(g =>
            {
                // Minimal stub: mark refiner stage and advance to a new latent.
                g.IsRefinerStage = true;
                string refLatent = g.CreateNode("UnitTest_RefinerLatent", new JObject(), idMandatory: false);
                g.FinalSamples = [refLatent, 0];
            }, -400),
            DecodeSamplesToImageStep()
        };

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenUpscaleThenRefiner() =>
        Template_BaseThenUpscale()
            .Concat(
                new WorkflowGenerator.WorkflowGenStep[]
                {
                    new(g =>
                    {
                        g.IsRefinerStage = true;
                        string refLatent = g.CreateNode("UnitTest_RefinerLatent", new JObject(), idMandatory: false);
                        g.FinalSamples = [refLatent, 0];
                    }, -400),
                    DecodeSamplesToImageStep()
                });

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenSegments(int segmentCount = 1) =>
        new[]
            {
                MinimalGraphSeedStep(),
                DecodeSamplesToImageStep()
            }
            .Concat(Enumerable.Range(0, Math.Max(1, segmentCount)).Select(i =>
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    // Minimal stub: create "segment" nodes and set FinalMask reference.
                    string seg = g.CreateNode($"UnitTest_Segment_{i}", new JObject(), idMandatory: false);
                    g.FinalMask = [seg, 0];
                }, -300 + i)));

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenRefinerThenSegments(int segmentCount = 1) =>
        Template_BaseThenRefiner()
            .Concat(Template_BaseThenSegments(segmentCount).Where(s => s.Priority > -950));

    public static List<JObject> NodesOfType(JObject workflow, string classType) =>
        WorkflowUtils.NodesOfType(workflow, classType)
            .Select(node => node.Node)
            .Where(node => node is not null)
            .ToList();
}
