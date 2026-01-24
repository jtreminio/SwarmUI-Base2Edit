using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Accounts;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Base2Edit;

public partial class Base2EditExtension : Extension
{
    public const int SectionID_Edit = 100;
    // SwarmUI's ComfyUI websocket handler marks outputs from node IDs >= 50000 as "intermediate" (not real),
    // and those can be displayed after "real" outputs.
    // If keeping the pre-edit image, we can display it before the edited by lowering the ID
    private const int PreEditImageSaveId = 22000;
    private const int EditSeedOffset = 2;
    private const string PreEditImageOutKey = "base2edit_pre_image_out";
    private const string Base2EditRanKey = "base2edit_ran";
    public static T2IParamGroup Base2EditGroup;
    public static T2IRegisteredParam<bool> KeepPreEditImage;
    public static T2IRegisteredParam<string> ApplyEditAfter;
    public static T2IRegisteredParam<T2IModel> EditModel;
    public static T2IRegisteredParam<T2IModel> EditVAE;
    public static T2IRegisteredParam<int> EditSteps;
    public static T2IRegisteredParam<double> EditCFGScale;
    public static T2IRegisteredParam<string> EditSampler;
    public static T2IRegisteredParam<string> EditScheduler;
    public static T2IRegisteredParam<double> EditControl;

    public override void OnPreInit()
    {
        PromptRegion.RegisterCustomPrefix("edit");

        T2IPromptHandling.PromptTagBasicProcessors["edit"] = (data, context) =>
        {
            context.SectionID = SectionID_Edit;
            return $"<edit//cid={SectionID_Edit}>";
        };
        T2IPromptHandling.PromptTagLengthEstimators["edit"] = (data, context) => "<break>";

        ScriptFiles.Add("Assets/base2edit.js");
    }

    public override void OnInit()
    {
        Logs.Info("Base2Edit Extension initializing...");
        ComfyUISelfStartBackend.CustomNodePaths.Add(Path.Join(FilePath, "B2ENodes"));
        RegisterParameters();
        RegisterWorkflowSteps();
    }

    private void RegisterParameters()
    {
        Base2EditGroup = new(
            Name: "Base2Edit",
            Description: "Applies an edit stage to your generated image using the <edit> prompt section.\n"
                + "The edit stage can be injected after the base stage or after the refiner stage.",
            Toggles: true,
            Open: false,
            OrderPriority: 8
        );

        KeepPreEditImage = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Keep Pre-Edit Image",
            Description: "When enabled, saves the image immediately before the edit stage begins.\n"
                + "This lets you compare the original and edited versions.",
            Default: "false",
            Group: Base2EditGroup,
            OrderPriority: 1,
            FeatureFlag: "comfyui"
        ));

        ApplyEditAfter = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Apply Edit After",
            Description: "Where to inject the edit stage:\n"
                + "'Base' applies edit after the base generation, before upscale/refiner.\n"
                + "'Refiner' applies edit after all generation stages (base, upscale, refiner), editing the finalized image.",
            Default: "Refiner",
            GetValues: (_) => ["Base", "Refiner"],
            Group: Base2EditGroup,
            OrderPriority: 2,
            FeatureFlag: "comfyui"
        ));

        EditControl = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "Edit Control",
            Description: "Controls how much of the edit sampling is applied.\n"
                + "At 1.0, full edit steps are run.\n"
                + "At 0.5, only 50% of edit steps are run from the midpoint.\n"
                + "Lower values preserve more of the original image.",
            Default: "1",
            Min: 0.1,
            Max: 1,
            Step: 0.05,
            ViewType: ParamViewType.SLIDER,
            Group: Base2EditGroup,
            OrderPriority: 3,
            FeatureFlag: "comfyui"
        ));

        EditModel = T2IParamTypes.Register<T2IModel>(new T2IParamType(
            Name: "Edit Model",
            Description: "The model to use for the edit stage.\n"
                + "'(Use Current)' uses whatever model is active at the injection point.",
            Default: "(Use Current)",
            IgnoreIf: "(Use Current)",
            GetValues: (Session s) =>
            {
                List<T2IModel> baseList = [.. Program.MainSDModels.ListModelsFor(s).OrderBy(m => m.Name)];
                List<string> bases = T2IParamTypes.CleanModelList(baseList.Select(m => m.Name));
                return ["(Use Current)", .. bases];
            },
            Subtype: "Stable-Diffusion",
            Group: Base2EditGroup,
            OrderPriority: 10,
            FeatureFlag: "comfyui",
            ChangeWeight: 9,
            DoNotPreview: true
        ));

        EditVAE = T2IParamTypes.Register<T2IModel>(new T2IParamType(
            Name: "Edit VAE",
            Description: "VAE to use for the edit stage.\n"
                + "'Automatic' uses the current VAE.\n"
                + "'None' disables VAE override.",
            Default: "None",
            IgnoreIf: "None",
            GetValues: (Session s) =>
            {
                var vaeNames = Program.T2IModelSets["VAE"].ListModelsFor(s).Select(m => m.Name);
                return ["Automatic", "None", .. T2IParamTypes.CleanModelList(vaeNames)];
            },
            Subtype: "VAE",
            Group: Base2EditGroup,
            OrderPriority: 11,
            FeatureFlag: "comfyui",
            ChangeWeight: 7,
            DoNotPreview: true
        ));

        EditSteps = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Edit Steps",
            Description: "Number of steps for the edit stage.",
            Default: "8",
            Min: 1,
            Max: 200,
            ViewMax: 100,
            Step: 1,
            ViewType: ParamViewType.SLIDER,
            Group: Base2EditGroup,
            OrderPriority: 12,
            FeatureFlag: "comfyui"
        ));

        EditCFGScale = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "Edit CFG Scale",
            Description: "CFG Scale for the edit stage.",
            Default: "1",
            Min: 0,
            Max: 100,
            ViewMax: 20,
            Step: 0.5,
            ViewType: ParamViewType.SLIDER,
            Group: Base2EditGroup,
            OrderPriority: 13,
            FeatureFlag: "comfyui",
            ChangeWeight: -3
        ));

        EditSampler = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Edit Sampler",
            Description: "Sampler to use for the edit stage.",
            Default: "euler",
            GetValues: (_) => ComfyUIBackendExtension.Samplers,
            Group: Base2EditGroup,
            OrderPriority: 14,
            FeatureFlag: "comfyui"
        ));

        EditScheduler = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Edit Scheduler",
            Description: "Scheduler to use for the edit stage.",
            Default: "normal",
            GetValues: (_) => ComfyUIBackendExtension.Schedulers,
            Group: Base2EditGroup,
            OrderPriority: 15,
            FeatureFlag: "comfyui"
        ));
    }

    private void RegisterWorkflowSteps()
    {
        WorkflowGenerator.AddStep(g => {
            if (ShouldRunEditStage(g, "Base"))
            {
                RunEditStage(g, isFinalStep: false);
            }
        }, -4.2);

        WorkflowGenerator.AddStep(g => {
            if (ShouldRunEditStage(g, "Refiner"))
            {
                RunEditStage(g, isFinalStep: true);
            }
        }, 7);

        // Final post-processing: rewire saves for deterministic ordering (when needed),
        // then remove any dangling decode nodes created earlier in the build.
        // Run very late to avoid removing nodes another extension might connect later.
        WorkflowGenerator.AddStep(g =>
        {
            FinalizeBase2EditWorkflow(g);
        }, 99999);
    }
}
