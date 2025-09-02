using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using System.Reflection;
using Verse;
using UnityEngine;

namespace DesignatorBinds
{
    // Build is scheduled from Bootstrap's static ctor; no need to duplicate here.

    [HarmonyPatch(typeof(UIRoot), "UIRootOnGUI")]
    public static class Patch_UIRoot_UIRootOnGUI
    {
        public static void Postfix()
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing) return;
                if (Event.current == null || Event.current.type != EventType.KeyDown) return;
                // Respect local gizmo/designator hotkeys when something is selected
                try
                {
                    var selector = Find.Selector;
                    if (selector != null)
                    {
                        int selCount = 0;
                        try { selCount = selector.NumSelected; } catch { }
                        if (selCount <= 0)
                        {
                            try { selCount = selector.SelectedObjectsListForReading?.Count ?? 0; } catch { }
                        }
                        if (selCount > 0) return;
                    }
                }
                catch { }
                if (Bootstrap.BindingToDesignator.Count == 0) return;

                

                foreach (var kv in Bootstrap.BindingToDesignator)
                {
                    var key = kv.Key;
                    var binding = kv.Value;
                    if (key == null || binding == null) continue;

                    if (key.IsDownEvent)
                    {
                        if (Find.CurrentMap == null) continue;
                        var designator = binding?.TryResolveDesignator();
                        if (designator == null) continue;
                        if (!Utilities.IsDesignatorUsable(designator)) continue;

                        Find.DesignatorManager.Select(designator);
                        Event.current.Use();
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[DB] OnGUI handler error: " + e);
            }
        }
    }

    // Append our option for ALL overrides and both property/method variants
    [HarmonyPatch]
    public static class Patch_RightClickOptions_All
    {
        private const string CreateLabel = "Create keybind";
        private const string RemoveLabel = "Remove keybind";

        public static IEnumerable<MethodBase> TargetMethods()
        {
            // Patch only the base Command entry points, filter to Designators in postfix
            var targets = new HashSet<MethodBase>();
            var baseType = typeof(Command);
            try
            {
                var getter = AccessTools.PropertyGetter(baseType, "RightClickFloatMenuOptions");
                if (getter != null && getter.GetParameters().Length == 0 && typeof(IEnumerable<FloatMenuOption>).IsAssignableFrom(getter.ReturnType))
                    targets.Add(getter);
            }
            catch { }
            try
            {
                var method = AccessTools.Method(baseType, "RightClickFloatMenuOptions", Type.EmptyTypes);
                if (method != null && typeof(IEnumerable<FloatMenuOption>).IsAssignableFrom(method.ReturnType))
                    targets.Add(method);
            }
            catch { }
            return targets;
        }

        public static void Postfix(object __instance, ref IEnumerable<FloatMenuOption> __result)
        {
            try
            {
                if (!(__instance is Designator d)) return;
                var list = (__result as IEnumerable<FloatMenuOption>)?.ToList() ?? new List<FloatMenuOption>();

                var hasBinding = Bootstrap.HasBinding(d);
                var label = hasBinding ? RemoveLabel : CreateLabel;
                list.Add(new FloatMenuOption(label, () =>
                {
                    try
                    {
                        if (hasBinding) Bootstrap.RemoveBindingForDesignator(d);
                        else Bootstrap.RegisterBindingForDesignator(d, openKeyConfig: DB_Mod.Settings.OpenKeyDialogAfterCreate);
                    }
                    catch (Exception ex) { Log.Error("[DB] Keybind action failed: " + ex); }
                }));
                __result = list;
            }
            catch (Exception e)
            {
                Log.Error("[DB] Error adding right-click option: " + e);
            }
        }

        // Duplicate filtering removed to avoid cross-mod reflection hazards
    }

    [HarmonyPatch(typeof(KeyPrefs), nameof(KeyPrefs.Save))]
    public static class Patch_KeyPrefs_Save
    {
        public static void Postfix()
        {
            try
            {
                if (Bootstrap.CategoryDef == null) return;
                var defs = DefDatabase<KeyBindingDef>.AllDefsListForReading
                    .Where(k => k?.category == Bootstrap.CategoryDef)
                    .ToList();
                foreach (var def in defs)
                {
                    try { Bootstrap.PersistKeyCodesForDef(def); } catch { }
                }
            }
            catch { }
        }
    }

    
}
