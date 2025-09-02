using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DesignatorBinds
{
    public class DB_Settings : ModSettings
    {
        public bool OpenKeyDialogAfterCreate = true;
        public List<string> PersistedKeyDefNames = new List<string>();
        public List<DB_SavedBinding> SavedBindings = new List<DB_SavedBinding>();

        public override void ExposeData()
        {
            Scribe_Values.Look(ref OpenKeyDialogAfterCreate, "OpenKeyDialogAfterCreate", true);
            Scribe_Collections.Look(ref PersistedKeyDefNames, "PersistedKeyDefNames", LookMode.Value);
            Scribe_Collections.Look(ref SavedBindings, "SavedBindings", LookMode.Deep);
            if (PersistedKeyDefNames == null) PersistedKeyDefNames = new List<string>();
            if (SavedBindings == null) SavedBindings = new List<DB_SavedBinding>();
            base.ExposeData();
        }
    }

    public class DB_Mod : Mod
    {
        public static DB_Mod Instance;
        // Ensure settings are available even if accessed before Instance is constructed
        public static DB_Settings Settings
        => Instance?._settings
           ?? LoadedModManager.GetMod<DB_Mod>()?
                 .GetSettings<DB_Settings>()
           ?? new DB_Settings();

        private DB_Settings _settings;

        public DB_Mod(ModContentPack content) : base(content)
        {
            Instance = this;
            _settings = GetSettings<DB_Settings>();
            try { LongEventHandler.ExecuteWhenFinished(Bootstrap.BuildOrRebuild); } catch { }
        }

        public override string SettingsCategory() => "Designator Binds";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Open keyboard config after creating a keybind", ref _settings.OpenKeyDialogAfterCreate);
            listing.GapLine();
            listing.Label("Maintenance");
            if (listing.ButtonText("Clear all designator binds"))
            {
                try { Bootstrap.ClearAllBindings(); } catch (System.Exception e) { Log.Error("[DB] ClearAllBindings failed: " + e); }
            }
            listing.End();
        }
    }

    public class DB_SavedBinding : IExposable
    {
        public string DefName;
        public int KeyA;
        public int KeyB;

        public void ExposeData()
        {
            Scribe_Values.Look(ref DefName, nameof(DefName));
            Scribe_Values.Look(ref KeyA, nameof(KeyA), (int)UnityEngine.KeyCode.None);
            Scribe_Values.Look(ref KeyB, nameof(KeyB), (int)UnityEngine.KeyCode.None);
        }
    }
}
