using System;
using System.Reflection;
using CitiesHarmony.API;
using ColossalFramework.UI;
using HarmonyLib;
using ICities;
using TakeAWalk.Util;
using UnityEngine;

namespace TakeAWalk
{
    public class TakeAWalkMod : IUserMod
    {
        private const string HarmonyId = "com.roberto.takeawalk";

        public string Name => "Take a Walk";
        public string Description => Localization.Get("MOD_DESCRIPTION");

        public void OnEnabled()
        {
            HarmonyHelper.EnsureHarmonyInstalled();
            HarmonyHelper.DoOnHarmonyReady(() =>
            {
                try
                {
                    new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
                    Log.Info("Harmony patches applied");
                }
                catch (Exception ex)
                {
                    Log.Error("PatchAll failed: " + ex);
                }
            });
        }

        public void OnDisabled()
        {
            WalkingTourManager.ReleaseAll();   // remove any live transient walking-tour lines
            if (HarmonyHelper.IsHarmonyInstalled)
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                Log.Info("Harmony patches removed");
            }
        }

        public void OnSettingsUI(UIHelperBase helper)
        {
            Settings s = Settings.Instance;

            // ── General ──────────────────────────────────────────────────────────
            var general = helper.AddGroup(Localization.Get("SETTINGS_GROUP_GENERAL"));
            var enable = (UIComponent)general.AddCheckbox(
                Localization.Get("SETTINGS_ENABLE"), s.Enabled,
                v => { s.Enabled = v; Settings.Save(); });
            enable.tooltip = Localization.Get("SETTINGS_ENABLE_TIP");

            // ── Eligibility ──────────────────────────────────────────────────────
            var elig = helper.AddGroup(Localization.Get("SETTINGS_GROUP_ELIGIBILITY"));
            AddSlider(elig, "SETTINGS_POLLUTION_THRESHOLD", 0f, 255f, 5f, s.PollutionThreshold,
                v => v.ToString("F0"), v => { s.PollutionThreshold = (int)v; Settings.Save(); });
            AddSlider(elig, "SETTINGS_WATER_POLLUTION_THRESHOLD", 0f, 255f, 5f, s.WaterPollutionThreshold,
                v => v.ToString("F0"), v => { s.WaterPollutionThreshold = (int)v; Settings.Save(); });
            AddSlider(elig, "SETTINGS_WATER_POLLUTION_PENALTY", 0f, 2f, 0.1f, s.WaterPollutionSensitivity,
                v => v.ToString("F1"), v => { s.WaterPollutionSensitivity = v; Settings.Save(); });
            AddSlider(elig, "SETTINGS_WATER_CHECK_RADIUS", 16f, 192f, 8f, s.WaterCheckRadius,
                v => v.ToString("F0") + " m", v => { s.WaterCheckRadius = v; Settings.Save(); });
            AddSlider(elig, "SETTINGS_NOISE_SENSITIVITY", 0f, 1f, 0.05f, s.NoiseSensitivity,
                v => v.ToString("F2"), v => { s.NoiseSensitivity = v; Settings.Save(); });
            AddSlider(elig, "SETTINGS_LANDMARK_FORGIVENESS", 0f, 2f, 0.1f, s.LandmarkNoiseForgiveness,
                v => v.ToString("F1"), v => { s.LandmarkNoiseForgiveness = v; Settings.Save(); });

            // ── Points of interest ───────────────────────────────────────────────
            var poi = helper.AddGroup(Localization.Get("SETTINGS_GROUP_POI"));
            AddSlider(poi, "SETTINGS_FEATURE_RADIUS", 16f, 128f, 8f, s.FeatureRadius,
                v => v.ToString("F0") + " m", v => { s.FeatureRadius = v; Settings.Save(); });
            AddSlider(poi, "SETTINGS_TREE_WEIGHT", 0f, 5f, 0.5f, s.TreeWeight,
                v => v.ToString("F1"), v => { s.TreeWeight = v; Settings.Save(); });
            AddSlider(poi, "SETTINGS_PROP_WEIGHT", 0f, 5f, 0.5f, s.PropWeight,
                v => v.ToString("F1"), v => { s.PropWeight = v; Settings.Save(); });
            AddSlider(poi, "SETTINGS_LANDMARK_BONUS", 0f, 100f, 5f, s.LandmarkBonus,
                v => v.ToString("F0"), v => { s.LandmarkBonus = v; Settings.Save(); });

            // ── Leisure value (length-scaled) ────────────────────────────────────
            var scoring = helper.AddGroup(Localization.Get("SETTINGS_GROUP_SCORING"));
            AddSlider(scoring, "SETTINGS_SMALL_PARK_RATE", 1f, 60f, 1f, s.SmallParkRate,
                v => v.ToString("F0"), v => { s.SmallParkRate = v; Settings.Save(); });
            AddSlider(scoring, "SETTINGS_LENGTH_FULL", 16f, 320f, 8f, s.LengthForFullValue,
                v => v.ToString("F0") + " m", v => { s.LengthForFullValue = v; Settings.Save(); });
            AddSlider(scoring, "SETTINGS_MAX_MULT", 1f, 5f, 0.5f, s.MaxTotalMultiplier,
                v => v.ToString("F1") + "x", v => { s.MaxTotalMultiplier = v; Settings.Save(); });
            AddSlider(scoring, "SETTINGS_DECORATION_FULL", 10f, 100f, 5f, s.DecorationForFullValue,
                v => v.ToString("F0"), v => { s.DecorationForFullValue = v; Settings.Save(); });
            AddSlider(scoring, "SETTINGS_INJECT_RADIUS", 16f, 128f, 8f, s.InjectRadius,
                v => v.ToString("F0") + " m", v => { s.InjectRadius = v; Settings.Save(); });
            var sight = (UIComponent)scoring.AddCheckbox(
                Localization.Get("SETTINGS_INJECT_SIGHTSEEING"), s.InjectSightseeing,
                v => { s.InjectSightseeing = v; Settings.Save(); });
            sight.tooltip = Localization.Get("SETTINGS_INJECT_SIGHTSEEING_TIP");
            AddSlider(scoring, "SETTINGS_HEALTH_SHARE", 0f, 1f, 0.05f, s.HealthShare,
                v => v.ToString("F2"), v => { s.HealthShare = v; Settings.Save(); });
            // Shown as a 0-50 strength; stored as a 0-0.5 fractional share (1 = 0.01, 50 = 0.5).
            AddSlider(scoring, "SETTINGS_APPEAL_SHARE", 0f, 0.5f, 0.01f, s.AttractivenessShare,
                v => (v * 100f).ToString("F0"), v => { s.AttractivenessShare = v; Settings.Save(); });

            // ── Cims walking the paths (walking tours, Parklife only) ────────────
            var visits = helper.AddGroup(Localization.Get("SETTINGS_GROUP_VISITS"));
            var visitsCheck = (UIComponent)visits.AddCheckbox(
                Localization.Get("SETTINGS_ENABLE_TOURS"), s.EnableWalkingTours,
                v => { s.EnableWalkingTours = v; Settings.Save(); });
            visitsCheck.tooltip = Localization.Get("SETTINGS_ENABLE_TOURS_TIP");
            AddSlider(visits, "SETTINGS_TOUR_MIN_LENGTH", 16f, 320f, 8f, s.TourMinLength,
                v => v.ToString("F0") + " m", v => { s.TourMinLength = v; Settings.Save(); });
            AddSlider(visits, "SETTINGS_MAX_TOURS", 1f, 64f, 1f, s.MaxTours,
                v => v.ToString("F0"), v => { s.MaxTours = (int)v; Settings.Save(); });
            AddSlider(visits, "SETTINGS_TOUR_BUDGET", 50f, 400f, 10f, s.TourBudget,
                v => v.ToString("F0") + "%", v => { s.TourBudget = (int)v; Settings.Save(); });

            // ── Performance ──────────────────────────────────────────────────────
            var perf = helper.AddGroup(Localization.Get("SETTINGS_GROUP_PERFORMANCE"));
            AddSlider(perf, "SETTINGS_SEGMENTS_PER_TICK", 16f, 1024f, 16f, s.SegmentsPerTick,
                v => v.ToString("F0"), v => { s.SegmentsPerTick = (int)v; Settings.Save(); });
            AddSlider(perf, "SETTINGS_MAX_PATH_SEGMENTS", 32f, 1024f, 32f, s.MaxPathSegments,
                v => v.ToString("F0"), v => { s.MaxPathSegments = (int)v; Settings.Save(); });

            // ── Debug ────────────────────────────────────────────────────────────
            var dbg = helper.AddGroup(Localization.Get("SETTINGS_GROUP_DEBUG"));
            var debugCheck = (UIComponent)dbg.AddCheckbox(
                Localization.Get("SETTINGS_DEBUG_LOGGING"), Log.DebugEnabled,
                v => { Log.DebugEnabled = v; });
            debugCheck.tooltip = Localization.Get("SETTINGS_DEBUG_LOGGING_TIP");
        }

        // Adds a localized slider with a live value label to its right.
        // labelKey resolves both "<KEY>" (label) and "<KEY>_TIP" (tooltip).
        private static void AddSlider(
            UIHelperBase group, string labelKey,
            float min, float max, float step, float defaultValue,
            Func<float, string> format, OnValueChanged onChanged)
        {
            UILabel valueLabel = null;

            UIComponent root = null;
            var uiHelper = group as UIHelper;
            if (uiHelper != null)
            {
                var field = typeof(UIHelper).GetField("m_Root", BindingFlags.NonPublic | BindingFlags.Instance);
                root = field?.GetValue(uiHelper) as UIComponent;
            }
            valueLabel = root?.AddUIComponent<UILabel>();

            var slider = (UISlider)group.AddSlider(
                Localization.Get(labelKey), min, max, step, defaultValue,
                v =>
                {
                    onChanged(v);
                    if (valueLabel != null) valueLabel.text = format(v);
                });
            slider.tooltip = Localization.Get(labelKey + "_TIP");

            UILabel nameLabel = slider.parent?.Find<UILabel>("Label");
            if (nameLabel != null)
                nameLabel.width = nameLabel.textScale * nameLabel.font.size * nameLabel.text.Length;

            if (valueLabel != null)
            {
                valueLabel.text = format(defaultValue);
                valueLabel.textScale = 0.85f;
                valueLabel.AlignTo(slider, UIAlignAnchor.TopLeft);
                valueLabel.relativePosition = new Vector3(slider.width + 8f, 0f, 0f);
            }
        }
    }
}
