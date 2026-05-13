using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace Base2Edit.Tests;

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

            List<WorkflowGenerator.WorkflowGenStep> before = [.. WorkflowGenerator.Steps];

            if (T2IParamTypes.Width is null)
            {
                T2IParamTypes.RegisterDefaults();
            }

            UnitTestStubs.EnsureComfySetClipDeviceRegistered();
            UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

            Base2EditExtension ext = new();
            ext.OnPreInit();
            ext.OnInit();

            List<WorkflowGenerator.WorkflowGenStep> after = [.. WorkflowGenerator.Steps];
            _base2EditSteps = after.Where(step => !before.Contains(step)).ToList();

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

        List<WorkflowGenerator.WorkflowGenStep> priorSteps = [.. WorkflowGenerator.Steps];

        try
        {
            WorkflowGenerator.Steps = [.. steps.OrderBy(s => s.Priority)];
            input.ApplyLateSpecialLogic();

            WorkflowGenerator gen = new()
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

            WorkflowGenerator gen = new()
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

    public static WorkflowGenerator.WorkflowGenStep MinimalGraphSeedStep() =>
        new(g =>
        {
            using SyncingWorkflowBridge bridge = BridgeSync.For(g);
            UnknownNode model = bridge.AddStub("UnitTest_Model", "4")
                .WithOutputs(WGNodeData.DT_MODEL, "CLIP", WGNodeData.DT_VAE);
            UnknownNode latent = bridge.AddStub("UnitTest_Latent", "10")
                .WithOutputs("LATENT");

            g.CurrentModel = model.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = model.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);
            g.CurrentVae = model.GetOutput(2).ToWGNodeData(g, WGNodeData.DT_VAE);
            g.CurrentMedia = latent.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_LATENT_IMAGE);
            g.FinalLoadedModel = g.UserInput.Get(T2IParamTypes.Model, null);
            g.FinalLoadedModelList = g.FinalLoadedModel is null ? [] : [g.FinalLoadedModel];
        }, -1000);

    public static WorkflowGenerator.WorkflowGenStep ImageOnlySeedStep() =>
        new(g =>
        {
            string imageNode = g.CreateNode("UnitTest_Image", [], id: "11", idMandatory: false);
            g.CurrentMedia = new WGNodeData([imageNode, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
            g.BasicInputImage = new WGNodeData([imageNode, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        }, -900);

    public static WorkflowGenerator.WorkflowGenStep DecodeSamplesToImageStep() =>
        new(g =>
        {
            if (g.CurrentMedia is null || g.CurrentVae is null)
            {
                return;
            }
            WGNodeData decoded = g.CurrentMedia.DecodeLatents(g.CurrentVae, false);
            g.CurrentMedia = decoded;
            g.BasicInputImage = decoded;
        }, -950);

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseOnlyLatents() =>
        [MinimalGraphSeedStep()];

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseOnlyImage() =>
        [MinimalGraphSeedStep(), DecodeSamplesToImageStep()];

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_EditOnly() =>
        [MinimalGraphSeedStep(), ImageOnlySeedStep()];

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenUpscale() =>
        [
            MinimalGraphSeedStep(),
            DecodeSamplesToImageStep(),
            new(g =>
            {
                string upLatent = g.CreateNode("UnitTest_UpscaleLatent", [], idMandatory: false);
                g.CurrentMedia = new WGNodeData([upLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
            }, -500)
        ];

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenRefiner() =>
        [
            MinimalGraphSeedStep(),
            DecodeSamplesToImageStep(),
            new(g =>
            {
                g.IsRefinerStage = true;
                string refLatent = g.CreateNode("UnitTest_RefinerLatent", [], idMandatory: false);
                g.CurrentMedia = new WGNodeData([refLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
            }, -400),
            DecodeSamplesToImageStep()
        ];

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenUpscaleThenRefiner() =>
        Template_BaseThenUpscale()
            .Concat(
                [
                    new(g =>
                    {
                        g.IsRefinerStage = true;
                        string refLatent = g.CreateNode("UnitTest_RefinerLatent", [], idMandatory: false);
                        g.CurrentMedia = new WGNodeData([refLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                    }, -400),
                    DecodeSamplesToImageStep()
                ]);

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenSegments(int segmentCount = 1) =>
        new[]
            {
                MinimalGraphSeedStep(),
                DecodeSamplesToImageStep()
            }
            .Concat(Enumerable.Range(0, Math.Max(1, segmentCount)).Select(i =>
                new WorkflowGenerator.WorkflowGenStep(g =>
                {
                    string seg = g.CreateNode($"UnitTest_Segment_{i}", [], idMandatory: false);
                    g.FinalMask = [seg, 0];
                }, -300 + i)));

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseThenRefinerThenSegments(int segmentCount = 1) =>
        Template_BaseThenRefiner()
            .Concat(Template_BaseThenSegments(segmentCount).Where(s => s.Priority > -950));

    public static WorkflowGenerator.WorkflowGenStep PostBaseLatentStep() =>
        new(g =>
        {
            string postBaseLatent = g.CreateNode("UnitTest_PostBaseLatent", [], id: "2100", idMandatory: false);
            g.CurrentMedia = new WGNodeData([postBaseLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
        }, 2);
}
