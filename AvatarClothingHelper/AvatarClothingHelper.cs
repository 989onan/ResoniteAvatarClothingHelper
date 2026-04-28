using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static OfficialAssets.Graphics;

namespace AvatarClothingHelper
{
    public class AvatarClothingHelper : ResoniteMod
    {
        public static ModConfiguration Config;
        private static readonly string blendshapeSyncSlotName = "Blendshape Sync";

        [AutoRegisterConfigKey]
        private static ModConfigurationKey<bool> EnableInspectorButtons = new ModConfigurationKey<bool>("EnableInspectorButtons", "Enable Setup Blendshape Source Buttons appearing on Inspectors constructed by you.", () => true);

        [AutoRegisterConfigKey]
        private static ModConfigurationKey<bool> GenerateSlotPerBlendshape = new ModConfigurationKey<bool>("GenerateSlotPerBlendshape", "Generate each Blendshape ValueCopy and MultiDriver on a nested slot.", () => true);

        [AutoRegisterConfigKey]
        private static ModConfigurationKey<bool> EnableLogging = new ModConfigurationKey<bool>("EnableLogging", "Should this log?", () => true);

        public override string Author => "Banane9";
        public override string Link => "https://github.com/Banane9/ResoniteAvatarClothingHelper";
        public override string Name => "AvatarClothingHelper";
        public override string Version => "2.0.0";

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony($"{Author}.{Name}");
            Config = GetConfiguration();
            Config.Save(true);
            harmony.PatchAll();
        }

        class DialogSettings
        {
            public bool UseMeshBoneListInstead { get; set; }
            public bool UseFindAndReplace { get; set; }
            public bool ApplyOnDestinationInstead { get; set; }
            public bool UseRegex { get; set; }
            public bool IgnoreCase { get; set; } = true;
            public bool UseTrim { get; set; } = true;
            public string FindPattern { get; set; }
            public string ReplacePattern { get; set; }
        }

        private static void driveSecondaryBlendshapes(Slot parent, SkinnedMeshRenderer primaryRenderer = null, bool replaceLog = false)
        {
            if (replaceLog) Msg($"DOING BLENDSHAPE DRIVERS!");
            var skinnedRenderers = parent.GetComponentsInChildren<SkinnedMeshRenderer>(renderer => renderer.MeshBlendshapeCount > 0).ToArray();

            primaryRenderer = primaryRenderer ?? skinnedRenderers.OrderByDescending(renderer => renderer.MeshBlendshapeCount).First();

            if (primaryRenderer.Slot.FindChild(blendshapeSyncSlotName) != null)
                return;

            foreach (var skinnedRenderer in skinnedRenderers.Where(renderer => renderer.BlendShapeWeights.Count < renderer.MeshBlendshapeCount))
                skinnedRenderer.BlendShapeWeights.AddRange(Enumerable.Repeat(0f, skinnedRenderer.MeshBlendshapeCount - skinnedRenderer.BlendShapeWeights.Count));

            var blendshapeGroups = skinnedRenderers
                .SelectMany(renderer => 

                    Enumerable.Range(0, renderer.MeshBlendshapeCount)
                    .Select(i =>
                    {
                        string name = SanitizeForComparison(renderer.BlendShapeName(i), GetSettings(primaryRenderer));
                        if (renderer == primaryRenderer)
                        {
                            name = SanitizeForComparison(PerformFindAndReplaceIfNeeded(renderer.BlendShapeName(i), GetSettings(primaryRenderer), replaceLog), GetSettings(primaryRenderer));
                        }
                        return new Blendshape(name, renderer.BlendShapeWeights.GetElement(i), renderer == primaryRenderer);
                    }))
                .GroupBy(blendshape => blendshape.Name)
                .Where(group => group.Count() > 1 && group.Any(blendshape => blendshape.Primary));

            if (!blendshapeGroups.Any())
                return;

            var root = primaryRenderer.Slot.AddSlot(blendshapeSyncSlotName);

            foreach (var group in blendshapeGroups)
            {
                var slot = Config.GetValue(GenerateSlotPerBlendshape) ? root.AddSlot(group.Key) : root;
                var primaryBlendshape = group.First(blendshape => blendshape.Primary);

                var multiDriver = slot.AttachComponent<ValueMultiDriver<float>>();
                multiDriver.Value.DriveFrom(primaryBlendshape.Field);

                foreach (var blendshape in group.Where(blendshape => !blendshape.Primary))
                    multiDriver.Drives.Add().Target = blendshape.Field;
            }
        }

        private static Slot getObjectRoot(Slot slot)
        {
            var implicitRoot = slot.GetComponentInParents<IObjectRoot>(null, true, false);
            var objectRoot = slot.GetObjectRoot();

            if (implicitRoot == null)
                return objectRoot;

            if (objectRoot == slot || implicitRoot.Slot.HierachyDepth > objectRoot.HierachyDepth)
                return implicitRoot.Slot;

            return objectRoot;
        }

        static readonly ConditionalWeakTable<SkinnedMeshRenderer, DialogSettings> SettingsMap = [];

        static DialogSettings GetSettings(SkinnedMeshRenderer instance)
        {
            if (SettingsMap.TryGetValue(instance, out DialogSettings settings))
                return settings;

            DialogSettings newSettings = new DialogSettings();
            SettingsMap.Add(instance, newSettings);

            return newSettings;
        }

        static string LogIfDifferent(string original, string newValue, bool shouldLog)
        {
            if (shouldLog)
                Msg(
                    original != newValue
                        ? $"Name '{original}' was replaced with '{newValue}'"
                        : "No replacement was done"
                );
            return newValue;
        }

        static string PerformFindAndReplaceIfNeeded(string name, DialogSettings settings = null, bool shouldLog = false)
        {
            if (settings == null) return name;
            if (!settings.UseFindAndReplace) return name;

            string find = settings.FindPattern ?? "";
            string replace = settings.ReplacePattern ?? "";

            if (shouldLog) Msg($"Performing find & replace on '{name}'");

            if (!settings.UseRegex)
            {
                if (find == "")
                {
                    if (shouldLog) Msg($"Find is empty, using string.Format with '{replace}'");
                    return LogIfDifferent(name, string.Format(replace, name), shouldLog);
                }

                if (shouldLog) Msg($"Simple find and replace with '{find}' and '{replace}'");
                return LogIfDifferent(name, name.Replace(find, replace), shouldLog);
            }

            if (shouldLog) Msg($"RegEx replace with '{find}' and '{replace}'");
            return LogIfDifferent(name, Regex.Replace(name, find, replace), shouldLog);
        }

        static string SanitizeForComparison(string name, DialogSettings settings)
        {
            if (settings == null) return name;
            if (settings.IgnoreCase) name = name.ToLowerInvariant();
            if (settings.UseTrim) name = name.Trim();
            return name;
        }

        [HarmonyPatch(typeof(ModelImporter))]
        private static class ModelImporterPatch
        {
            [HarmonyPostfix]
            [HarmonyPatch(nameof(ModelImporter.ImportModel))]
            private static void ImportModelPostfix(Slot targetSlot, ref IEnumerator<Context> __result)
            {
                __result = new EnumerableInjector<Context>(__result)
                {
                    Postfix = () => driveSecondaryBlendshapes(targetSlot, null, Config.GetValue(EnableLogging))
                }.GetEnumerator();
            }
        }

        [HarmonyPatch(typeof(SkinnedMeshRenderer))]
        private static class SkinnedMeshRendererPatch
        {
            [HarmonyPostfix]
            [HarmonyPatch(nameof(SkinnedMeshRenderer.BuildInspectorUI))]
            private static void BuildInspectorUIPostfix(SkinnedMeshRenderer __instance, UIBuilder ui)
            {
                if (!Config.GetValue(EnableInspectorButtons) || __instance.Slot.FindChild(blendshapeSyncSlotName) != null)
                    return;

                var button = ui.Button("Setup as Primary Blendshape Source", (colorX?)color.Pink);
                var button2 = ui.Button("Setup best Blendshape Source", (colorX?)color.Pink);

                var root = getObjectRoot(__instance.Slot);

                button.LocalPressed += (sender, data) =>
                {
                    driveSecondaryBlendshapes(root, __instance, Config.GetValue(EnableLogging));
                };

                button2.LocalPressed += (sender, data) =>
                {
                    driveSecondaryBlendshapes(root, null, Config.GetValue(EnableLogging));
                };




                DialogSettings settings = GetSettings(__instance);
                Slot infoHolder = ui.Empty("Info Holder");
                ui.Checkbox("Use find and replace", settings.UseFindAndReplace).State.OnValueChange += field => settings.UseFindAndReplace = field.Value;
                //ui.Checkbox("Off - Apply find and replace on names from clipboard or armature / On - Apply on names from this mesh", settings.ApplyOnDestinationInstead).State.OnValueChange += field => settings.ApplyOnDestinationInstead = field.Value;
                ui.Checkbox("Use RegEx", settings.UseRegex).State.OnValueChange += field => settings.UseRegex = field.Value;

                {
                    ValueField<string> findField = infoHolder.AttachComponent<ValueField<string>>();

                    const string key = "Value";
                    SyncMemberEditorBuilder.Build(
                        findField.GetSyncMember(key),
                        "Find",
                        findField.GetSyncMemberFieldInfo(key),
                        ui
                    );

                    findField.Value.Changed += field => settings.FindPattern = (field as Sync<string>)?.Value;
                }

                {
                    ValueField<string> replaceField = infoHolder.AttachComponent<ValueField<string>>();

                    const string key = "Value";
                    SyncMemberEditorBuilder.Build(
                        replaceField.GetSyncMember(key),
                        "Replace",
                        replaceField.GetSyncMemberFieldInfo(key),
                        ui
                    );

                    replaceField.Value.Changed += field => settings.ReplacePattern = (field as Sync<string>)?.Value;
                }

                ui.Text("See format help on github.com/TheJebForge/BoneReferenceHelper");
                ui.Checkbox("Ignore case", settings.IgnoreCase).State.OnValueChange += field => settings.IgnoreCase = field.Value;
                ui.Checkbox("Ignore leading and trailing whitespace", settings.UseTrim).State.OnValueChange += field => settings.UseTrim = field.Value;
            }
        }
    }
}