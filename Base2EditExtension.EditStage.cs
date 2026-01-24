using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public partial class Base2EditExtension
{
    private static bool IsEditOnlyWorkflow(WorkflowGenerator g)
    {
        return g.UserInput.TryGet(T2IParamTypes.InitImage, out _)
            && g.UserInput.Get(T2IParamTypes.Steps) == 0
            && g.UserInput.Get(ApplyEditAfter, "Refiner") == "Base";
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

    private record Base2EditRunPlan(bool WantPreEdit, bool MustReencode)
    {
        public bool NeedsPreEditDecode => WantPreEdit || MustReencode;
    }

    private static void RunEditStage(WorkflowGenerator g, bool isFinalStep)
    {
        g.NodeHelpers[Base2EditRanKey] = "true";
        var prompts = ExtractEditPrompts(g);
        var modelState = PrepareEditModelAndVae(g);
        bool wantPreEdit = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(KeepPreEditImage, false);
        Base2EditRunPlan plan = new(WantPreEdit: wantPreEdit, MustReencode: modelState.MustReencode);
        if (plan.NeedsPreEditDecode)
        {
            EnsureImageAvailable(g, modelState.PreEditVae);
        }
        if (plan.WantPreEdit)
        {
            SavePreEditImageIfNeeded(g);
        }
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
            // Store the pre-edit image output so we can later emit it in-order
            // before the final post-edit image is saved.
            g.NodeHelpers[PreEditImageOutKey] = $"{g.FinalImageOut[0]},{g.FinalImageOut[1]}";
            Logs.Debug("Base2Edit: Tracked pre-edit image");
        }
    }

    private static void ReencodeIfNeeded(WorkflowGenerator g, EditModelState modelState)
    {
        if (modelState.MustReencode || g.FinalSamples is null)
        {
            if (g.FinalImageOut is null)
            {
                EnsureImageAvailable(g, modelState.PreEditVae);
            }
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

