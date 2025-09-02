using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;

namespace DesignatorBinds
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string HarmonyId = "remi.designatorbinds";
        public static readonly Dictionary<KeyBindingDef, DesignatorBinding> BindingToDesignator = new Dictionary<KeyBindingDef, DesignatorBinding>();
        public static KeyBindingCategoryDef CategoryDef;
        private static readonly HashSet<KeyCode> UsedKeys = new HashSet<KeyCode>();
        

        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                Log.Error($"[DB] Harmony patch failed: {e}");
            }

            // Build is scheduled by DB_Mod ctor to avoid double invocation here.
        }

        public static void BuildOrRebuild()
        {
            try
            {
                // Resolve our keybinding category
                CategoryDef =
                    DefDatabase<KeyBindingCategoryDef>.GetNamedSilentFail("DB_DesignatorBinds")
                    ?? DefDatabase<KeyBindingCategoryDef>.AllDefsListForReading
                        ?.FirstOrDefault(k => string.Equals(k.label, "Designator Binds", StringComparison.OrdinalIgnoreCase));
                if (CategoryDef == null)
                {
                    Log.Warning("[DB] Could not find KeyBindingCategoryDef. Did the XML fail to load?");
                    return;
                }

                BindingToDesignator.Clear();
                UsedKeys.Clear();
                SeedUsedKeysFromPrefs();

                int restored = 0;
                bool settingsChanged = false;
                var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // First, (re)create any bindings the user saved in settings
                var saved = DB_Mod.Settings?.PersistedKeyDefNames?.ToList() ?? new List<string>();
                foreach (var defName in saved)
                {
                    if (string.IsNullOrWhiteSpace(defName)) continue;
                    try
                    {
                        var binding = DesignatorBinding.FromKeyBindingDefName(defName);
                        if (binding == null)
                        {
                            Log.Message($"[DB] Skipping saved entry; could not parse: {defName}");
                            continue;
                        }
                        var def = EnsureKeyForBinding(binding);
                        if (def == null)
                        {
                            Log.Message($"[DB] Failed to ensure KeyBindingDef for: {defName}");
                            continue;
                        }
                        BindingToDesignator[def] = binding;
                        EnsureKeyPrefsEntry(def);
                        // Reapply saved key codes if we have them (match both current and legacy names)
                        try
                        {
                            var sblist = DB_Mod.Settings?.SavedBindings;
                            DB_SavedBinding sb = null;
                            if (sblist != null)
                            {
                                sb = sblist.FirstOrDefault(b => string.Equals(b.DefName, def.defName, StringComparison.OrdinalIgnoreCase))
                                     ?? sblist.FirstOrDefault(b => string.Equals(b.DefName, defName, StringComparison.OrdinalIgnoreCase))
                                     ?? sblist.FirstOrDefault(b => b.DefName != null && b.DefName.StartsWith(def.defName + "_", StringComparison.Ordinal));
                            }
                            if (sb != null)
                            {
                                ApplyKeyCodesToDef(def, (KeyCode)sb.KeyA, (KeyCode)sb.KeyB);
                                if (!string.Equals(sb.DefName, def.defName, StringComparison.Ordinal))
                                {
                                    sb.DefName = def.defName;
                                    settingsChanged = true;
                                }
                            }
                        }
                        catch { }
                        // Normalize persisted name if it differs (e.g., legacy suffixes)
                        try
                        {
                            if (!string.Equals(def.defName, defName, StringComparison.Ordinal))
                            {
                                var set = DB_Mod.Settings?.PersistedKeyDefNames;
                                if (set != null)
                                {
                                    set.RemoveAll(n => string.Equals(n, defName, StringComparison.Ordinal));
                                    if (!set.Contains(def.defName)) set.Add(def.defName);
                                    settingsChanged = true;
                                }
                                // Also migrate any SavedBindings entries with the legacy name
                                var sblist = DB_Mod.Settings?.SavedBindings;
                                if (sblist != null)
                                {
                                    foreach (var sb in sblist.Where(b => string.Equals(b.DefName, defName, StringComparison.Ordinal)).ToList())
                                    {
                                        sb.DefName = def.defName;
                                        settingsChanged = true;
                                    }
                                }
                            }
                        }
                        catch { }
                        processed.Add(def.defName);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[DB] Failed to restore saved binding '{defName}': {ex.Message}");
                    }
                }

                // Also include any existing defs already present in our category (e.g. from XML or previous runtime)
                var defs = DefDatabase<KeyBindingDef>.AllDefsListForReading
                    .Where(k => k?.category == CategoryDef)
                    .ToList();
                foreach (var def in defs)
                {
                    if (def == null || processed.Contains(def.defName)) continue;
                    try
                    {
                        var binding = DesignatorBinding.FromKeyBindingDefName(def.defName);
                        if (binding != null)
                        {
                            BindingToDesignator[def] = binding;
                            EnsureKeyPrefsEntry(def);
                            // Capture current key codes so we preserve user choices
                            try { PersistKeyCodesForDef(def); } catch { }
                            // Make sure it's persisted for next launch
                            if (DB_Mod.Settings != null && !DB_Mod.Settings.PersistedKeyDefNames.Contains(def.defName))
                            {
                                DB_Mod.Settings.PersistedKeyDefNames.Add(def.defName);
                            }
                            restored++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[DB] Failed to restore binding for {def?.defName}: {ex.Message}");
                    }
                }

                try { EnsureAllKeyPrefsEntries(); } catch { }
                if (settingsChanged)
                {
                    try { (DB_Mod.Instance ?? LoadedModManager.GetMod<DB_Mod>())?.WriteSettings(); } catch { }
                }
                Log.Message($"[DB] Restored {restored} global designator keybinds.");
            }
            catch (Exception e)
            {
                Log.Error($"[DB] BuildOrRebuild exception: {e}");
            }
        }

        public static void RegisterBindingForDesignator(Designator d, bool openKeyConfig = false)
        {
            if (d == null) return;
            try
            {
                var binding = DesignatorBinding.FromDesignator(d);
                var kbd = EnsureKeyForBinding(binding, d);
                if (kbd != null)
                {
                    BindingToDesignator[kbd] = binding;
                    EnsureKeyPrefsEntry(kbd);
                    try
                    {
                        if (DB_Mod.Settings != null && !DB_Mod.Settings.PersistedKeyDefNames.Contains(kbd.defName))
                        {
                            DB_Mod.Settings.PersistedKeyDefNames.Add(kbd.defName);
                            (DB_Mod.Instance ?? LoadedModManager.GetMod<DB_Mod>())?.WriteSettings();
                        }
                    }
                    catch { }
                    // Capture and persist current key codes for this binding
                    try { PersistKeyCodesForDef(kbd); } catch { }
                    var showLabel = string.IsNullOrWhiteSpace(binding.Label) ? binding.FallbackLabel : binding.Label;
                    Messages.Message($"Added keybind entry under '{CategoryDef?.label ?? "Designator Binds"}': {showLabel}", MessageTypeDefOf.TaskCompletion, false);
                    if (openKeyConfig)
                    {
                        try
                        {
                            var t = AccessTools.TypeByName("RimWorld.Dialog_KeyBinding")
                                    ?? AccessTools.TypeByName("RimWorld.Dialog_KeyBindings")
                                    ?? AccessTools.TypeByName("Dialog_KeyBinding")
                                    ?? AccessTools.TypeByName("Dialog_KeyBindings");
                            if (t != null)
                            {
                                var inst = Activator.CreateInstance(t);
                                if (inst is Window w) Find.WindowStack.Add(w);
                                else Find.WindowStack.Add((Window)inst);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DB] Failed to register keybind: {ex}");
            }
        }

        public static bool HasBinding(Designator d)
        {
            TryGetBindingAndDef(d, out var _, out var _def);
            return _def != null && _def.category == CategoryDef;
        }

        public static void RemoveBindingForDesignator(Designator d)
        {
            try
            {
                if (!TryGetBindingAndDef(d, out var binding, out var def) || def == null) return;

                // Remove from our runtime mapping
                if (def != null)
                {
                    BindingToDesignator.Remove(def);
                }

                // Remove KeyPrefs entry and default assignments
                RemoveKeyPrefsEntry(def);

                // Remove KeyBindingDef from DefDatabase so it no longer appears anywhere
                TryRemoveKeyBindingDef(def);

                // Remove from persisted settings
                try
                {
                    if (DB_Mod.Settings != null && !string.IsNullOrEmpty(def?.defName))
                    {
                        DB_Mod.Settings.PersistedKeyDefNames.RemoveAll(n => string.Equals(n, def.defName, StringComparison.OrdinalIgnoreCase));
                        DB_Mod.Settings.SavedBindings.RemoveAll(b => string.Equals(b.DefName, def.defName, StringComparison.OrdinalIgnoreCase));
                        (DB_Mod.Instance ?? LoadedModManager.GetMod<DB_Mod>())?.WriteSettings();
                    }
                }
                catch { }

                Messages.Message($"Removed keybind for: {binding?.Label ?? d.LabelCap}", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error($"[DB] Failed to remove keybind: {ex}");
            }
        }

        private static bool TryGetBindingAndDef(Designator d, out DesignatorBinding binding, out KeyBindingDef def)
        {
            binding = DesignatorBinding.FromDesignator(d);
            def = null;
            if (binding == null) return false;
            var defName = binding.ToKeyBindingDefName();
            def = DefDatabase<KeyBindingDef>.GetNamedSilentFail(defName);
            return def != null;
        }

        private static KeyBindingDef EnsureKeyForBinding(DesignatorBinding binding, Designator sampleDesignator = null)
        {
            if (binding == null) return null;
            string label = null;
            try { label = binding.Label; } catch { }
            if (string.IsNullOrWhiteSpace(label))
                label = binding.FallbackLabel;

            var defName = binding.ToKeyBindingDefName();
            var existing = DefDatabase<KeyBindingDef>.GetNamedSilentFail(defName);
            if (existing != null)
            {
                existing.category = CategoryDef;
                // Sanitize defaults within our category to avoid duplicates
                KeyCode inA = KeyCode.None, inB = KeyCode.None;
                try
                {
                    var d = sampleDesignator ?? binding.TryResolveDesignator();
                    if (d?.hotKey != null)
                    {
                        inA = d.hotKey.defaultKeyCodeA;
                        inB = d.hotKey.defaultKeyCodeB;
                    }
                }
                catch { }
                SanitizeWithUsed(inA, inB, out var outA, out var outB);
                existing.defaultKeyCodeA = outA;
                existing.defaultKeyCodeB = outB;
                // Prefer saved keys (if any) for immediate UI reflection
                try
                {
                    var sb = DB_Mod.Settings?.SavedBindings?.FirstOrDefault(b => string.Equals(b.DefName, existing.defName, StringComparison.OrdinalIgnoreCase));
                    if (sb != null)
                    {
                        existing.defaultKeyCodeA = (KeyCode)sb.KeyA;
                        existing.defaultKeyCodeB = (KeyCode)sb.KeyB;
                    }
                }
                catch { }
                return existing;
            }

            var kbd = new KeyBindingDef
            {
                defName = defName,
                label = $"{label}",
                category = CategoryDef,
                defaultKeyCodeA = KeyCode.None,
                defaultKeyCodeB = KeyCode.None
            };

            try
            {
                var d = sampleDesignator ?? binding.TryResolveDesignator();
                if (d?.hotKey != null)
                {
                    var inA = d.hotKey.defaultKeyCodeA;
                    var inB = d.hotKey.defaultKeyCodeB;
                    SanitizeWithUsed(inA, inB, out var outA, out var outB);
                    kbd.defaultKeyCodeA = outA;
                    kbd.defaultKeyCodeB = outB;
                }
            }
            catch { }

            try { kbd.modContentPack = LoadedModManager.RunningMods.FirstOrDefault(m => m.PackageId == "remi.designatorbinds"); } catch { }
            DefDatabase<KeyBindingDef>.Add(kbd);
            return kbd;
        }

        private static void EnsureKeyPrefsEntry(KeyBindingDef def)
        {
            if (def == null) return;
            try
            {
                var keyPrefsType = typeof(KeyPrefs);
                var dataField = keyPrefsType.GetField("data", BindingFlags.NonPublic | BindingFlags.Static);
                object data = dataField?.GetValue(null);
                if (data == null)
                {
                    var dataProp = keyPrefsType.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    data = dataProp?.GetValue(null);
                }
                if (data == null)
                {
                    var getDataLike = keyPrefsType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .FirstOrDefault(m => (m.ReturnType?.Name?.Contains("KeyPrefsData") ?? false) && m.GetParameters().Length == 0);
                    if (getDataLike != null)
                    {
                        data = getDataLike.Invoke(null, null);
                    }
                }
                if (data == null) return;

                var dataType = data.GetType();
                // Intentionally avoid TryAddNewKeyBindingDefaults to prevent duplicate assignments.

                // Fallback: search for any IDictionary-like field storing key bindings by KeyBindingDef
                var dictObj = FindKeyPrefsDictionary(data);
                if (dictObj == null) return;

                var nonGenDict = dictObj as System.Collections.IDictionary;
                if (nonGenDict != null)
                {
                    if (!nonGenDict.Contains(def))
                    {
                        var kbdData = CreateKeyBindingData(dataType.Assembly, def);
                        if (kbdData != null)
                        {
                            nonGenDict.Add(def, kbdData);
                            var codes = ExtractKeyCodesFromKeyBindingData(kbdData);
                            if (codes.a != KeyCode.None) UsedKeys.Add(codes.a);
                            if (codes.b != KeyCode.None) UsedKeys.Add(codes.b);
                        }
                    }
                    return;
                }

                var containsKey = dictObj.GetType().GetMethod("ContainsKey");
                var addMethod = dictObj.GetType().GetMethod("Add");
                bool has = false;
                if (containsKey != null)
                {
                    has = (bool)containsKey.Invoke(dictObj, new object[] { def });
                }
                if (!has && addMethod != null)
                {
                    var kbdData = CreateKeyBindingData(dataType.Assembly, def);
                    if (kbdData != null)
                    {
                        addMethod.Invoke(dictObj, new object[] { def, kbdData });
                        var codes = ExtractKeyCodesFromKeyBindingData(kbdData);
                        if (codes.a != KeyCode.None) UsedKeys.Add(codes.a);
                        if (codes.b != KeyCode.None) UsedKeys.Add(codes.b);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DB] Could not ensure KeyPrefs entry for {def.defName}: {ex.Message}");
            }
        }

        private static object FindKeyPrefsDictionary(object data)
        {
            if (data == null) return null;
            var t = data.GetType();
            // Try known field names first
            var dictField = t.GetField("keyPrefs", BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? t.GetField("prefs", BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? t.GetField("keyPrefsDict", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = dictField?.GetValue(data);
            if (dict != null) return dict;

            // Generic search: any IDictionary whose key type is KeyBindingDef
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object val = null;
                try { val = f.GetValue(data); } catch { }
                if (val == null) continue;
                var valType = val.GetType();
                if (typeof(System.Collections.IDictionary).IsAssignableFrom(valType))
                {
                    // If it’s a generic dict, check key type
                    var genArgs = valType.IsGenericType ? valType.GetGenericArguments() : Type.EmptyTypes;
                    if (genArgs.Length == 2 && typeof(KeyBindingDef).IsAssignableFrom(genArgs[0]))
                        return val;
                    // If non-generic, try Contains with a KeyBindingDef and see if call is valid
                    var contains = valType.GetMethod("Contains", new[] { typeof(object) })
                                   ?? valType.GetMethod("ContainsKey", new[] { typeof(object) })
                                   ?? valType.GetMethod("ContainsKey");
                    if (contains != null) return val;
                }
            }
            return null;
        }

private static object CreateKeyBindingData(Assembly verseAsm, KeyBindingDef def)
{
    try
    {
        var kbdDataType = verseAsm.GetType("Verse.KeyBindingData");
        if (kbdDataType == null) return null;
        var kbdData = Activator.CreateInstance(kbdDataType);

        // Use the KeyBindingDef defaults which we sanitized against UsedKeys
        var resA = def.defaultKeyCodeA;
        var resB = def.defaultKeyCodeB;

                // Try setting via KeyBinding fields
                var keyBindingType = verseAsm.GetType("Verse.KeyBinding");
                if (keyBindingType != null)
                {
                    var keyAField = kbdDataType.GetField("keyBindingA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var keyBField = kbdDataType.GetField("keyBindingB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (keyAField != null && keyBField != null)
                    {
                        var kbA = Activator.CreateInstance(keyBindingType);
                        var kbB = Activator.CreateInstance(keyBindingType);
                        var kcField = keyBindingType.GetField("keyCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var kcProp = keyBindingType.GetProperty("MainKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                     ?? keyBindingType.GetProperty("keyCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        try { kcField?.SetValue(kbA, resA); } catch { }
                        try { kcField?.SetValue(kbB, resB); } catch { }
                        try { kcProp?.SetValue(kbA, resA); } catch { }
                        try { kcProp?.SetValue(kbB, resB); } catch { }
                        keyAField.SetValue(kbdData, kbA);
                        keyBField.SetValue(kbdData, kbB);
                        return kbdData;
                    }
                }

                // Fallback: set raw key codes if present
                var fieldA = kbdDataType.GetField("keyCodeA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? kbdDataType.GetField("keyA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fieldB = kbdDataType.GetField("keyCodeB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? kbdDataType.GetField("keyB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                try { fieldA?.SetValue(kbdData, resA); } catch { }
                try { fieldB?.SetValue(kbdData, resB); } catch { }
        return kbdData;
    }
    catch { return null; }
}

        private static (KeyCode a, KeyCode b) ExtractKeyCodesFromKeyBindingData(object keyBindingData)
        {
            try
            {
                if (keyBindingData == null) return (KeyCode.None, KeyCode.None);
                var t = keyBindingData.GetType();
                // Preferred: embedded KeyBinding objects
                var keyBindingType = t.Assembly.GetType("Verse.KeyBinding");
                var fieldAObj = t.GetField("keyBindingA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(keyBindingData);
                var fieldBObj = t.GetField("keyBindingB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(keyBindingData);
                if (fieldAObj != null && keyBindingType != null)
                {
                    var kcField = keyBindingType.GetField("keyCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var kcProp = keyBindingType.GetProperty("MainKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                 ?? keyBindingType.GetProperty("keyCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    KeyCode a = KeyCode.None, b = KeyCode.None;
                    try { a = (KeyCode)(kcProp?.GetValue(fieldAObj) ?? kcField?.GetValue(fieldAObj) ?? KeyCode.None); } catch { }
                    try { b = (KeyCode)(kcProp?.GetValue(fieldBObj) ?? kcField?.GetValue(fieldBObj) ?? KeyCode.None); } catch { }
                    return (a, b);
                }

                // Fallback: raw key code fields
                var fieldA = t.GetField("keyCodeA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? t.GetField("keyA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fieldB = t.GetField("keyCodeB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? t.GetField("keyB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                KeyCode ra = KeyCode.None, rb = KeyCode.None;
                try { ra = (KeyCode)(fieldA?.GetValue(keyBindingData) ?? KeyCode.None); } catch { }
                try { rb = (KeyCode)(fieldB?.GetValue(keyBindingData) ?? KeyCode.None); } catch { }
                return (ra, rb);
            }
            catch { return (KeyCode.None, KeyCode.None); }
        }

        private static void SeedUsedKeysFromPrefs()
        {
            try
            {
                var keyPrefsType = typeof(KeyPrefs);
                var dataField = keyPrefsType.GetField("data", BindingFlags.NonPublic | BindingFlags.Static);
                object data = dataField?.GetValue(null);
                if (data == null)
                {
                    var dataProp = keyPrefsType.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    data = dataProp?.GetValue(null);
                }
                if (data == null) return;

                // Prefer public method if available
                var dataType = data.GetType();
                var getBound = dataType.GetMethod("GetBoundKeyCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var bindingSlotType = typeof(KeyPrefs).GetNestedType("BindingSlot", BindingFlags.Public | BindingFlags.NonPublic);
                if (getBound != null && bindingSlotType != null)
                {
                    object slotA = Enum.GetNames(bindingSlotType).Any(n => n == "A") ? Enum.Parse(bindingSlotType, "A") : Enum.ToObject(bindingSlotType, 0);
                    object slotB = Enum.GetNames(bindingSlotType).Any(n => n == "B") ? Enum.Parse(bindingSlotType, "B") : Enum.ToObject(bindingSlotType, 1);
                    foreach (var kd in DefDatabase<KeyBindingDef>.AllDefsListForReading.Where(k => k?.category == CategoryDef))
                    {
                        try
                        {
                            var a = (KeyCode)getBound.Invoke(data, new object[] { kd, slotA });
                            var b = (KeyCode)getBound.Invoke(data, new object[] { kd, slotB });
                            if (a != KeyCode.None) UsedKeys.Add(a);
                            if (b != KeyCode.None) UsedKeys.Add(b);
                        }
                        catch { }
                    }
                    return;
                }

                // Fallback: read the internal dictionary and filter to our category
                var dictObj = FindKeyPrefsDictionary(data);
                if (dictObj is System.Collections.IDictionary nonGen)
                {
                    foreach (System.Collections.DictionaryEntry de in nonGen)
                    {
                        if (de.Key is KeyBindingDef kd && kd.category == CategoryDef)
                        {
                            var codes = ExtractKeyCodesFromKeyBindingData(de.Value);
                            if (codes.a != KeyCode.None) UsedKeys.Add(codes.a);
                            if (codes.b != KeyCode.None) UsedKeys.Add(codes.b);
                        }
                    }
                }
                else if (dictObj is System.Collections.IEnumerable kvps)
                {
                    foreach (var item in kvps)
                    {
                        var itType = item.GetType();
                        var propKey = itType.GetProperty("Key");
                        var propVal = itType.GetProperty("Value");
                        var key = propKey?.GetValue(item) as KeyBindingDef;
                        if (key == null || key.category != CategoryDef) continue;
                        var codes = ExtractKeyCodesFromKeyBindingData(propVal?.GetValue(item));
                        if (codes.a != KeyCode.None) UsedKeys.Add(codes.a);
                        if (codes.b != KeyCode.None) UsedKeys.Add(codes.b);
                    }
                }
            }
            catch { }
        }

        private static void EnsureAllKeyPrefsEntries()
        {
            try
            {
                var list = DefDatabase<KeyBindingDef>.AllDefsListForReading
                    .Where(k => k?.category == CategoryDef)
                    .ToList();
                foreach (var def in list)
                {
                    EnsureKeyPrefsEntry(def);
                    try { PersistKeyCodesForDef(def); } catch { }
                }
                // No immediate save; will persist normally when RimWorld saves prefs
            }
            catch { }
        }

        private static bool TryGetKeyPrefsData(out object data, out object dictObj)
        {
            data = null; dictObj = null;
            try
            {
                var keyPrefsType = typeof(KeyPrefs);
                var dataField = keyPrefsType.GetField("data", BindingFlags.NonPublic | BindingFlags.Static);
                data = dataField?.GetValue(null);
                if (data == null)
                {
                    var dataProp = keyPrefsType.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    data = dataProp?.GetValue(null);
                }
                if (data == null) return false;
                dictObj = FindKeyPrefsDictionary(data);
                return dictObj != null;
            }
            catch { return false; }
        }

        private static (KeyCode a, KeyCode b) GetKeyCodesForDef(KeyBindingDef def)
        {
            try
            {
                var keyPrefsType = typeof(KeyPrefs);
                var dataField = keyPrefsType.GetField("data", BindingFlags.NonPublic | BindingFlags.Static);
                object data = dataField?.GetValue(null);
                if (data == null)
                {
                    var dataProp = keyPrefsType.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    data = dataProp?.GetValue(null);
                }
                if (data != null)
                {
                    var dataType = data.GetType();
                    var getBound = dataType.GetMethod("GetBoundKeyCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var bindingSlotType = typeof(KeyPrefs).GetNestedType("BindingSlot", BindingFlags.Public | BindingFlags.NonPublic);
                    if (getBound != null && bindingSlotType != null)
                    {
                        object slotA = Enum.GetNames(bindingSlotType).Any(n => n == "A") ? Enum.Parse(bindingSlotType, "A") : Enum.ToObject(bindingSlotType, 0);
                        object slotB = Enum.GetNames(bindingSlotType).Any(n => n == "B") ? Enum.Parse(bindingSlotType, "B") : Enum.ToObject(bindingSlotType, 1);
                        try
                        {
                            var a = (KeyCode)getBound.Invoke(data, new object[] { def, slotA });
                            var b = (KeyCode)getBound.Invoke(data, new object[] { def, slotB });
                            return (a, b);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            try
            {
                if (!TryGetKeyPrefsData(out var data2, out var dictObj)) return (KeyCode.None, KeyCode.None);
                if (dictObj is System.Collections.IDictionary nonGen)
                {
                    if (nonGen.Contains(def))
                    {
                        var kbdData = nonGen[def];
                        return ExtractKeyCodesFromKeyBindingData(kbdData);
                    }
                    return (KeyCode.None, KeyCode.None);
                }
                // Generic
                var containsKey = dictObj.GetType().GetMethod("ContainsKey");
                var tryGetValue = dictObj.GetType().GetMethod("TryGetValue");
                bool has = false; object value = null;
                if (tryGetValue != null)
                {
                    var args = new object[] { def, null };
                    has = (bool)tryGetValue.Invoke(dictObj, args);
                    if (has) value = args[1];
                }
                else if (containsKey != null)
                {
                    has = (bool)containsKey.Invoke(dictObj, new object[] { def });
                }
                if (has && value != null)
                {
                    return ExtractKeyCodesFromKeyBindingData(value);
                }
            }
            catch { }
            return (KeyCode.None, KeyCode.None);
        }

        private static void ApplyKeyCodesToDef(KeyBindingDef def, KeyCode a, KeyCode b)
        {
            try
            {
                // First, prefer KeyPrefs API if available
                try
                {
                    if (TrySetBindingViaAPI(def, a, b)) return;
                }
                catch { }

                if (!TryGetKeyPrefsData(out var data, out var dictObj)) return;
                object kbdData = null;
                if (dictObj is System.Collections.IDictionary nonGen)
                {
                    if (nonGen.Contains(def)) kbdData = nonGen[def];
                    if (kbdData == null)
                    {
                        kbdData = CreateKeyBindingData(data.GetType().Assembly, def);
                        if (kbdData != null) nonGen[def] = kbdData;
                    }
                }
                else
                {
                    var tryGetValue = dictObj.GetType().GetMethod("TryGetValue");
                    var add = dictObj.GetType().GetMethod("Add");
                    object value = null; bool has = false;
                    if (tryGetValue != null)
                    {
                        var args = new object[] { def, null };
                        has = (bool)tryGetValue.Invoke(dictObj, args);
                        if (has) value = args[1];
                    }
                    if (!has)
                    {
                        value = CreateKeyBindingData(data.GetType().Assembly, def);
                        if (value != null && add != null) add.Invoke(dictObj, new object[] { def, value });
                    }
                    kbdData = value;
                }
                if (kbdData != null)
                {
                    ApplyKeyCodesToKeyBindingData(kbdData, a, b);
                }
            }
            catch { }
        }

        private static void ApplyKeyCodesToKeyBindingData(object kbdData, KeyCode a, KeyCode b)
        {
            try
            {
                if (kbdData == null) return;
                var t = kbdData.GetType();
                var asm = t.Assembly;
                // Preferred path: embedded KeyBinding objects
                var keyBindingType = asm.GetType("Verse.KeyBinding");
                var fieldAObj = t.GetField("keyBindingA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(kbdData);
                var fieldBObj = t.GetField("keyBindingB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(kbdData);
                if (fieldAObj != null && keyBindingType != null)
                {
                    var kcField = keyBindingType.GetField("keyCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var kcProp = keyBindingType.GetProperty("MainKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                 ?? keyBindingType.GetProperty("keyCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    try { kcField?.SetValue(fieldAObj, a); } catch { }
                    try { kcField?.SetValue(fieldBObj, b); } catch { }
                    try { kcProp?.SetValue(fieldAObj, a); } catch { }
                    try { kcProp?.SetValue(fieldBObj, b); } catch { }
                    return;
                }

                // Fallback: raw key codes
                var fieldA = t.GetField("keyCodeA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? t.GetField("keyA", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fieldB = t.GetField("keyCodeB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? t.GetField("keyB", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                try { fieldA?.SetValue(kbdData, a); } catch { }
                try { fieldB?.SetValue(kbdData, b); } catch { }
            }
            catch { }
        }

        internal static void PersistKeyCodesForDef(KeyBindingDef def)
        {
            try
            {
                if (def == null || DB_Mod.Settings == null) return;
                var codes = GetKeyCodesForDef(def);
                var list = DB_Mod.Settings.SavedBindings;
                if (list == null) return;
                list.RemoveAll(b => string.Equals(b.DefName, def.defName, StringComparison.OrdinalIgnoreCase));
                list.Add(new DB_SavedBinding
                {
                    DefName = def.defName,
                    KeyA = (int)codes.a,
                    KeyB = (int)codes.b
                });
                (DB_Mod.Instance ?? LoadedModManager.GetMod<DB_Mod>())?.WriteSettings();
            }
            catch { }
        }

        private static bool TrySetBindingViaAPI(KeyBindingDef def, KeyCode a, KeyCode b)
        {
            try
            {
                var kp = typeof(KeyPrefs);
                var slotType = kp.GetNestedType("BindingSlot", BindingFlags.Public | BindingFlags.NonPublic);
                if (slotType == null) return false;
                object slotA = Enum.GetNames(slotType).Any(n => n == "A") ? Enum.Parse(slotType, "A") : Enum.ToObject(slotType, 0);
                object slotB = Enum.GetNames(slotType).Any(n => n == "B") ? Enum.Parse(slotType, "B") : Enum.ToObject(slotType, 1);

                // Try static methods on KeyPrefs
                foreach (var m in kp.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 3
                        && ps[0].ParameterType.IsAssignableFrom(typeof(KeyBindingDef))
                        && ps[1].ParameterType.IsEnum
                        && ps[1].ParameterType == slotType
                        && ps[2].ParameterType == typeof(KeyCode))
                    {
                        m.Invoke(null, new object[] { def, slotA, a });
                        m.Invoke(null, new object[] { def, slotB, b });
                        return true;
                    }
                }

                // Try instance methods on the KeyPrefs data object
                if (TryGetKeyPrefsData(out var data, out var _))
                {
                    var dt = data.GetType();
                    foreach (var m in dt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 3
                            && ps[0].ParameterType.IsAssignableFrom(typeof(KeyBindingDef))
                            && ps[1].ParameterType.IsEnum
                            && ps[1].ParameterType == slotType
                            && ps[2].ParameterType == typeof(KeyCode))
                        {
                            m.Invoke(data, new object[] { def, slotA, a });
                            m.Invoke(data, new object[] { def, slotB, b });
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static void ClearAllBindings()
        {
            try
            {
                // Ensure category is resolved
                if (CategoryDef == null)
                {
                    try
                    {
                        CategoryDef =
                            DefDatabase<KeyBindingCategoryDef>.GetNamedSilentFail("DB_DesignatorBinds")
                            ?? DefDatabase<KeyBindingCategoryDef>.AllDefsListForReading
                                ?.FirstOrDefault(k => string.Equals(k.label, "Designator Binds", StringComparison.OrdinalIgnoreCase));
                    }
                    catch { }
                }

                var defs = DefDatabase<KeyBindingDef>.AllDefsListForReading
                    .Where(k => k != null && k.category == CategoryDef)
                    .ToList();
                int removed = 0;
                foreach (var def in defs)
                {
                    try
                    {
                        BindingToDesignator.Remove(def);
                        RemoveKeyPrefsEntry(def);
                        TryRemoveKeyBindingDef(def);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[DB] Failed to remove binding {def?.defName}: {ex.Message}");
                    }
                }

                try
                {
                    if (DB_Mod.Settings != null)
                    {
                        DB_Mod.Settings.PersistedKeyDefNames.Clear();
                        DB_Mod.Settings.SavedBindings.Clear();
                        (DB_Mod.Instance ?? LoadedModManager.GetMod<DB_Mod>())?.WriteSettings();
                    }
                }
                catch { }

                Messages.Message($"Cleared {removed} designator binds", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e)
            {
                Log.Error($"[DB] ClearAllBindings exception: {e}");
            }
        }

        private static void SanitizeWithUsed(KeyCode inA, KeyCode inB, out KeyCode outA, out KeyCode outB)
        {
            // Avoid assigning the same default inside our category to minimize conflicts
            outA = inA != KeyCode.None && !UsedKeys.Contains(inA) ? inA : KeyCode.None;
            outB = inB != KeyCode.None && !UsedKeys.Contains(inB) ? inB : KeyCode.None;
            if (outA != KeyCode.None) UsedKeys.Add(outA);
            if (outB != KeyCode.None) UsedKeys.Add(outB);
        }

        private static void RemoveKeyPrefsEntry(KeyBindingDef def)
        {
            if (def == null) return;
            try
            {
                var keyPrefsType = typeof(KeyPrefs);
                var dataField = keyPrefsType.GetField("data", BindingFlags.NonPublic | BindingFlags.Static);
                object data = dataField?.GetValue(null);
                if (data == null)
                {
                    var dataProp = keyPrefsType.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    data = dataProp?.GetValue(null);
                }
                if (data == null) return;

                var dictObj = FindKeyPrefsDictionary(data);
                if (dictObj == null) return;

                if (dictObj is System.Collections.IDictionary nonGen)
                {
                    if (nonGen.Contains(def))
                    {
                        nonGen.Remove(def);
                        return;
                    }
                }

                var containsKey = dictObj.GetType().GetMethod("ContainsKey");
                var removeMethod = dictObj.GetType().GetMethod("Remove", new[] { typeof(KeyBindingDef) })
                                  ?? dictObj.GetType().GetMethod("Remove");
                bool has = false;
                if (containsKey != null)
                {
                    has = (bool)containsKey.Invoke(dictObj, new object[] { def });
                }
                if (has && removeMethod != null)
                {
                    try { removeMethod.Invoke(dictObj, new object[] { def }); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DB] Could not remove KeyPrefs entry for {def.defName}: {ex.Message}");
            }
        }

        // ForceWriteModSettings removed; standard WriteSettings is sufficient

        private static void TryRemoveKeyBindingDef(KeyBindingDef def)
        {
            if (def == null) return;
            try
            {
                var dbType = typeof(DefDatabase<KeyBindingDef>);
                // Attempt to remove from any backing collections
                foreach (var f in dbType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        var val = f.GetValue(null);
                        if (val == null) continue;
                        if (val is System.Collections.IList list)
                        {
                            // match element type
                            var elemType = f.FieldType.IsGenericType ? f.FieldType.GetGenericArguments().FirstOrDefault() : null;
                            if (elemType == typeof(KeyBindingDef) || elemType == null)
                            {
                                if (list.Contains(def)) list.Remove(def);
                            }
                        }
                        else if (val is System.Collections.IDictionary dict)
                        {
                            // try remove by defName
                            if (!string.IsNullOrEmpty(def.defName) && dict.Contains(def.defName)) dict.Remove(def.defName);
                            if (dict.Contains(def)) dict.Remove(def);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DB] Could not remove KeyBindingDef from DefDatabase: {ex.Message}");
            }
        }
    }
}
