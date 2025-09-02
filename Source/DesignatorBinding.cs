using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace DesignatorBinds
{
    public class DesignatorBinding
    {
        public string TypeName;
        public string ParamDefName; // Optional: BuildableDef/TerrainDef/ThingDef defName or similar
        public string Label;

        public string FallbackLabel =>
            (TypeName ?? "Designator").Split('.').Last().Replace("Designator_", "").Replace('_', ' ')
            + (string.IsNullOrEmpty(ParamDefName) ? string.Empty : $" ({ParamDefName})");

        public static DesignatorBinding FromDesignator(Designator d)
        {
            if (d == null) return null;
            var b = new DesignatorBinding
            {
                TypeName = d.GetType().FullName,
                Label = SafeGetLabel(d)
            };

            // Try to capture a backing Def identity if present (e.g. Designator_Build(entDef))
            b.ParamDefName = TryGetDesignatorDefName(d);
            return b;
        }

        public static DesignatorBinding FromKeyBindingDefName(string keyDefName)
        {
            // Expected pattern: DB_ + Sanitized(TypeName + "|" + ParamDefNameOptional)
            if (string.IsNullOrEmpty(keyDefName)) return null;
            if (!keyDefName.StartsWith("DB_")) return null;
            // De-sanitizing is ambiguous; instead, we match against known types by re-sanitizing
            // Iterate candidate types and match their sanitized key form
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .Where(t => typeof(Designator).IsAssignableFrom(t) && !t.IsAbstract)
                .ToList();

            // Also gather all potential Defs for parameter matching
            var allBuildables = DefDatabase<BuildableDef>.AllDefsListForReading?.ToList() ?? new List<BuildableDef>();
            var allDefsByName = new Dictionary<string, Def>(StringComparer.OrdinalIgnoreCase);
            foreach (var bd in allBuildables)
                if (bd != null && !string.IsNullOrEmpty(bd.defName) && !allDefsByName.ContainsKey(bd.defName))
                    allDefsByName.Add(bd.defName, bd);

            // Try pure type match first
            foreach (var t in allTypes)
            {
                var key = Utilities.SanitizeDefName(t.FullName);
                if ($"DB_{key}" == keyDefName)
                {
                    return new DesignatorBinding { TypeName = t.FullName, ParamDefName = null, Label = null };
                }
            }

            // Try type + param match: we don't know the original delimiter after sanitization,
            // so rebuild the same pattern used by ToKeyBindingDefName.
            foreach (var t in allTypes)
            {
                foreach (var def in allDefsByName.Values)
                {
                    var candidate = Utilities.SanitizeDefName(t.FullName + "|" + def.defName);
                    if ($"DB_{candidate}" == keyDefName)
                    {
                        return new DesignatorBinding { TypeName = t.FullName, ParamDefName = def.defName, Label = null };
                    }
                }
            }

            // Fallback: if name starts with the sanitized type key plus an underscore, treat as type-only
            // This recovers entries accidentally saved with extra suffixes (e.g., hotKey defNames)
            foreach (var t in allTypes)
            {
                var key = Utilities.SanitizeDefName(t.FullName);
                if ($"DB_{key}_".Length < keyDefName.Length && keyDefName.StartsWith($"DB_{key}_", StringComparison.Ordinal))
                {
                    return new DesignatorBinding { TypeName = t.FullName, ParamDefName = null, Label = null };
                }
            }

            return null;
        }

        public string ToKeyBindingDefName()
        {
            var core = string.IsNullOrEmpty(ParamDefName) ? TypeName : (TypeName + "|" + ParamDefName);
            return "DB_" + Utilities.SanitizeDefName(core);
        }

        public Designator TryResolveDesignator()
        {
            var type = ResolveType(TypeName);
            if (type == null) return FallbackSearch();

            // If a specific Def parameter is required, try to construct with it
            if (!string.IsNullOrEmpty(ParamDefName))
            {
                BuildableDef bd = null;
                try { bd = DefDatabase<BuildableDef>.GetNamedSilentFail(ParamDefName); } catch { }

                if (bd != null)
                {
                    try
                    {
                        var ctor = type.GetConstructor(new[] { typeof(BuildableDef) });
                        if (ctor != null)
                            return (Designator)ctor.Invoke(new object[] { bd });
                    }
                    catch { }
                }
            }

            // Try parameterless
            try
            {
                var inst = Activator.CreateInstance(type) as Designator;
                if (inst != null) return inst;
            }
            catch { }

            // Fallback: search resolved designators in categories for a matching one
            return FallbackSearch();
        }

        private Designator FallbackSearch()
        {
            try
            {
                foreach (var cat in DefDatabase<DesignationCategoryDef>.AllDefsListForReading)
                {
                    foreach (var d in Utilities.ResolveDesignators(cat))
                    {
                        if (d == null) continue;
                        if (d.GetType().FullName != TypeName) continue;
                        if (string.IsNullOrEmpty(ParamDefName)) return d;
                        var defName = TryGetDesignatorDefName(d);
                        if (string.Equals(defName, ParamDefName, StringComparison.OrdinalIgnoreCase))
                            return d;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string SafeGetLabel(Designator d)
        {
            try { return d?.LabelCap; } catch { }
            try { return d?.Label; } catch { }
            return null;
        }

        private static Type ResolveType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static string TryGetDesignatorDefName(Designator d)
        {
            if (d == null) return null;
            var t = d.GetType();
            // Prefer common field names: only accept BuildableDef (ThingDef, TerrainDef, etc.)
            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var f in fields)
            {
                try
                {
                    var val = f.GetValue(d);
                    if (val is BuildableDef bd && !string.IsNullOrEmpty(bd.defName)) return bd.defName;
                }
                catch { }
            }
            // Try properties as a fallback
            var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var p in props)
            {
                try
                {
                    var val = p.GetValue(d);
                    if (val is BuildableDef bd && !string.IsNullOrEmpty(bd.defName)) return bd.defName;
                }
                catch { }
            }
            return null;
        }
    }
}
