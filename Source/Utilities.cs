using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace DesignatorBinds
{
    public static class Utilities
    {
        private static readonly BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static IEnumerable<Designator> ResolveDesignators(DesignationCategoryDef cat)
        {
            if (cat == null) yield break;
            object rawList = null;

            var propResolved = cat.GetType().GetProperty("ResolvedDesignators", InstAll);
            if (propResolved != null) rawList = propResolved.GetValue(cat);
            if (rawList == null)
            {
                var fieldResolved = cat.GetType().GetField("resolvedDesignators", InstAll);
                if (fieldResolved != null) rawList = fieldResolved.GetValue(cat);
            }
            if (rawList == null)
            {
                var propAllResolved = cat.GetType().GetProperty("AllResolvedDesignators", InstAll);
                if (propAllResolved != null) rawList = propAllResolved.GetValue(cat);
            }

            if (rawList is IEnumerable enumerable)
            {
                foreach (var obj in enumerable)
                    if (obj is Designator d) yield return d;
            }
        }

        public static bool IsDesignatorUsable(Designator d)
        {
            if (d == null) return false;
            try
            {
                // Allow selection even if not currently visible in UI (e.g., Architect closed)
                return d.Disabled == false;
            }
            catch { return true; }
        }

        public static string SanitizeDefName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unnamed";
            var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            var str = new string(chars);
            while (str.Contains("__")) str = str.Replace("__", "_");
            return str.Trim('_');
        }
    }
}
