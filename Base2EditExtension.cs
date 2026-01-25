using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Accounts;
using Newtonsoft.Json.Linq;

namespace Base2Edit;

public class Base2EditExtension : Extension
{
    public const int SectionID_Edit = 100;
    private const int PreEditImageSaveId = 50200;
    private const int EditSeedOffset = 2;

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
            Default: "20",
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
            Default: "7",
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
    }

    private static bool ShouldRunEditStage(WorkflowGenerator g, string expectedApplyAfter)
    {
        if (!g.UserInput.TryGet(ApplyEditAfter, out string applyAfter))
        {
            return false;
        }

        string rawPrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        if (!EditPromptParser.HasEditSection(rawPrompt))
        {
            return false;
        }

        return applyAfter == expectedApplyAfter;
    }

    private static void RunEditStage(WorkflowGenerator g, bool isFinalStep)
    {
        var prompts = ExtractEditPrompts(g);
        var modelState = PrepareEditModelAndVae(g);
        EnsureImageAvailable(g, modelState.PreEditVae);
        SavePreEditImageIfNeeded(g);
        ReencodeIfNeeded(g, modelState);
        var editParams = GetEditParameters(g);
        var conditioning = CreateEditConditioning(g, modelState.Clip, prompts, editParams);
        ExecuteEditSampler(g, modelState.Model, conditioning, editParams);
        FinalizeEditOutput(g, modelState.Vae, isFinalStep);
        var applyAfter = g.UserInput.Get(ApplyEditAfter, "Refiner");

        Logs.Debug($"Base2Edit: Applied edit stage after {applyAfter}");
    }

    private record EditPrompts(string Positive, string Negative);

    private static EditPrompts ExtractEditPrompts(WorkflowGenerator g)
    {
        string rawPrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string rawNegPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        return new EditPrompts(
            EditPromptParser.Extract(rawPrompt),
            EditPromptParser.Extract(rawNegPrompt)
        );
    }

    private record EditModelState(
        JArray Model,
        JArray Clip,
        JArray Vae,
        JArray PreEditVae,
        bool MustReencode
    );

    private static EditModelState PrepareEditModelAndVae(WorkflowGenerator g)
    {
        JArray preEditVae = g.FinalVae;
        JArray model = g.FinalModel;
        JArray clip = g.FinalClip;
        JArray vae = g.FinalVae;
        bool mustReencode = false;

        if (g.UserInput.TryGet(EditModel, out T2IModel altEditModel) && altEditModel is not null)
        {
            T2IModel currentModel = g.FinalLoadedModel;
            mustReencode = altEditModel.ModelClass?.CompatClass != currentModel.ModelClass?.CompatClass;

            g.FinalLoadedModel = altEditModel;
            g.FinalLoadedModelList = [altEditModel];
            (g.FinalLoadedModel, model, clip, vae) = g.CreateStandardModelLoader(altEditModel, "Edit", sectionId: SectionID_Edit);
            g.FinalModel = model;
            g.FinalClip = clip;
            g.FinalVae = vae;
        }

        if (g.UserInput.TryGet(EditVAE, out T2IModel altEditVae) && altEditVae is not null)
        {
            if (altEditVae.Name != "Automatic")
            {
                mustReencode = true;
                vae = g.CreateVAELoader(altEditVae.ToString(g.ModelFolderFormat));
                g.FinalVae = vae;
            }
        }

        return new EditModelState(model, clip, vae, preEditVae, mustReencode);
    }

    private static void EnsureImageAvailable(WorkflowGenerator g, JArray preEditVae)
    {
        if (g.FinalImageOut is null)
        {
            string decodeNode = g.CreateVAEDecode(preEditVae, g.FinalSamples);
            g.FinalImageOut = [decodeNode, 0];
        }
    }

    private static void SavePreEditImageIfNeeded(WorkflowGenerator g)
    {
        bool shouldSave = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(KeepPreEditImage, false);

        if (shouldSave)
        {
            g.CreateImageSaveNode(g.FinalImageOut, g.GetStableDynamicID(PreEditImageSaveId, 0));
            Logs.Debug("Base2Edit: Saved pre-edit image");
        }
    }

    private static void ReencodeIfNeeded(WorkflowGenerator g, EditModelState modelState)
    {
        if (modelState.MustReencode || g.FinalSamples is null)
        {
            string encodeNode = g.CreateVAEEncode(modelState.Vae, g.FinalImageOut);
            g.FinalSamples = [encodeNode, 0];
        }
    }

    private record EditParameters(
        int Width,
        int Height,
        int Steps,
        double Cfg,
        double Control,
        double Guidance,
        long Seed,
        string Sampler,
        string Scheduler
    );

    private static EditParameters GetEditParameters(WorkflowGenerator g)
    {
        return new EditParameters(
            Width: g.UserInput.GetImageWidth(),
            Height: g.UserInput.GetImageHeight(),
            Steps: g.UserInput.Get(EditSteps, 20),
            Cfg: g.UserInput.Get(EditCFGScale, 7.0),
            Control: g.UserInput.Get(EditControl, 1.0),
            Guidance: g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1),
            Seed: g.UserInput.Get(T2IParamTypes.Seed) + EditSeedOffset,
            Sampler: g.UserInput.Get(EditSampler, "euler"),
            Scheduler: g.UserInput.Get(EditScheduler, "normal"));
    }

    private record EditConditioning(JArray Positive, JArray Negative);

    private static EditConditioning CreateEditConditioning(
        WorkflowGenerator g,
        JArray clip,
        EditPrompts prompts,
        EditParameters editParams)
    {
        string posCondNode = CreateConditioningNode(g, clip, prompts.Positive, editParams);
        string refLatentNode = g.CreateNode("ReferenceLatent", new JObject()
        {
            ["conditioning"] = new JArray { posCondNode, 0 },
            ["latent"] = g.FinalSamples
        });
        string negCondNode = CreateConditioningNode(g, clip, prompts.Negative, editParams);

        return new EditConditioning([refLatentNode, 0], [negCondNode, 0]);
    }

    private static string CreateConditioningNode(
        WorkflowGenerator g,
        JArray clip,
        string prompt,
        EditParameters editParams)
    {
        return g.CreateNode("SwarmClipTextEncodeAdvanced", new JObject()
        {
            ["clip"] = clip,
            ["steps"] = editParams.Steps,
            ["prompt"] = prompt,
            ["width"] = editParams.Width,
            ["height"] = editParams.Height,
            ["target_width"] = editParams.Width,
            ["target_height"] = editParams.Height,
            ["guidance"] = editParams.Guidance
        });
    }

    private static void ExecuteEditSampler(
        WorkflowGenerator g,
        JArray model,
        EditConditioning conditioning,
        EditParameters editParams)
    {
        int startStep = (int)Math.Round(editParams.Steps * (1 - editParams.Control));
        string samplerNode = g.CreateKSampler(
            model,
            conditioning.Positive,
            conditioning.Negative,
            g.FinalSamples,
            editParams.Cfg,
            editParams.Steps,
            startStep,
            10000,
            editParams.Seed,
            returnWithLeftoverNoise: false,
            addNoise: true,
            explicitSampler: editParams.Sampler,
            explicitScheduler: editParams.Scheduler,
            sectionId: SectionID_Edit
        );

        g.FinalSamples = [samplerNode, 0];
    }

    private static void FinalizeEditOutput(WorkflowGenerator g, JArray vae, bool isFinalStep)
    {
        if (isFinalStep)
        {
            g.FinalImageOut = [g.CreateVAEDecode(vae, g.FinalSamples), 0];
        }
        else
        {
            g.FinalImageOut = null;
        }
    }
}
