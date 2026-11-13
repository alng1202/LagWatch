using System;
using System.Collections.Generic;
using System.Drawing;
using Netfix.Model;

namespace LagWatch
{
    /// <summary>
    /// The three plans. Order matters: each tier includes everything below it.
    ///   Free — everyday end user: live latency monitor and Network Doctor's full checkup with the safe, no-admin fixes.
    ///   Plus — paid: network-repair fixes (admin), Fix all, custom scans, predictions, exports.
    ///   Pro  — paid: full network metrics (OSI layer details, route map, outage center, advanced diagnostics),
    ///          background watch, network reports, and workspace optimization.
    /// </summary>
    internal enum SubscriptionTier
    {
        Free,
        Plus,
        Pro
    }

    /// <summary>Every paid feature, so each gate is checked in one place.</summary>
    internal enum Feature
    {
        // Free
        LiveMonitor, GameDetection, SafeOptimize, Troubleshooter, Checkup, SpeedTest,
        // Plus
        Predictions, HeadsUp, ExportHistory, PathCheck, CustomScans, FixAll, NetworkRepairFixes,
        // Pro
        AdvancedDiagnostics, OutageCenter, RouteMap, NetworkDetails, BackgroundWatch, NetworkReport,
        WorkspaceOptimization, AdaptiveControlPreview,
    }

    internal static class SubscriptionPolicy
    {
        public static SubscriptionTier RequiredTier(Feature feature)
        {
            switch (feature)
            {
                case Feature.Predictions:
                case Feature.HeadsUp:
                case Feature.ExportHistory:
                case Feature.PathCheck:
                case Feature.CustomScans:
                case Feature.FixAll:
                case Feature.NetworkRepairFixes:
                    return SubscriptionTier.Plus;
                case Feature.AdvancedDiagnostics:
                case Feature.OutageCenter:
                case Feature.RouteMap:
                case Feature.NetworkDetails:
                case Feature.BackgroundWatch:
                case Feature.NetworkReport:
                case Feature.WorkspaceOptimization:
                case Feature.AdaptiveControlPreview:
                    return SubscriptionTier.Pro;
                default:
                    return SubscriptionTier.Free;
            }
        }

        public static bool Allows(SubscriptionTier tier, Feature feature) { return tier >= RequiredTier(feature); }

        // Kept for the existing LagWatch call sites and self-tests.
        public static bool CanUsePredictions(SubscriptionTier tier) { return Allows(tier, Feature.Predictions); }
        public static bool CanExportHistory(SubscriptionTier tier) { return Allows(tier, Feature.ExportHistory); }
        public static bool CanUseAdvancedDiagnostics(SubscriptionTier tier) { return Allows(tier, Feature.AdvancedDiagnostics); }
        public static bool CanPreviewAdaptiveControl(SubscriptionTier tier) { return Allows(tier, Feature.AdaptiveControlPreview); }

        /// <summary>
        /// Which plan a Network Doctor one-click fix needs. Free gets every fix that is safe for anyone to click: it needs
        /// no administrator and only toggles or refreshes something the user could flip in Settings. Plus gets the repairs
        /// that rewrite network/security configuration (UAC). Pro gets the workspace "your picks for calls" switcher.
        /// Problems are always detected and explained on every tier; only the one-click repair is gated.
        /// </summary>
        public static SubscriptionTier RequiredTier(Fix fix)
        {
            switch (fix.Kind)
            {
                case FixKind.Open:
                case FixKind.QuitApp:
                case FixKind.TurnWiFiOn:
                case FixKind.RestartWiFi:
                case FixKind.FlushDNS:
                case FixKind.UnmuteSpeakers:
                case FixKind.TurnUpMicrophone:
                case FixKind.UseSpeaker:
                case FixKind.UsePCMicrophone:
                case FixKind.TurnBluetoothOn:
                    return SubscriptionTier.Free;
                case FixKind.UseCallPicks:
                    return SubscriptionTier.Pro;
                default:
                    return SubscriptionTier.Plus;
            }
        }

        public static bool CanRunFix(SubscriptionTier tier, Fix fix) { return tier >= RequiredTier(fix); }

        public static string Summary(SubscriptionTier tier)
        {
            switch (tier)
            {
                case SubscriptionTier.Plus:
                    return "Spotty-internet heads-ups, provider outage alerts, network repairs, Fix all, custom scans, predictions and exports unlocked";
                case SubscriptionTier.Pro:
                    return "Full network metrics, route/outage tools, background watch, network reports and workspace optimization unlocked";
                default:
                    return "Live monitoring, game detection, Safe Optimize and Network Doctor checkup with safe fixes";
            }
        }

        public static string ShortSummary(SubscriptionTier tier)
        {
            switch (tier)
            {
                case SubscriptionTier.Plus: return "Network repair";
                case SubscriptionTier.Pro: return "Full metrics + workspace";
                default: return "Monitor + checkup";
            }
        }

        public static string Banner(SubscriptionTier tier)
        {
            switch (tier)
            {
                case SubscriptionTier.Plus: return "PLUS · NETWORK REPAIR";
                case SubscriptionTier.Pro: return "PRO · FULL METRICS + WORKSPACE";
                default: return "FREE · MONITOR + CHECKUP";
            }
        }

        public static Color IdentityColor(SubscriptionTier tier)
        {
            switch (tier)
            {
                case SubscriptionTier.Plus: return Color.FromArgb(167, 139, 250);
                case SubscriptionTier.Pro: return Color.FromArgb(251, 191, 36);
                default: return Theme.Accent;
            }
        }

        /// <summary>The plan comparison shown when a locked feature is clicked.</summary>
        public static IEnumerable<string> PlanLines(SubscriptionTier tier)
        {
            switch (tier)
            {
                case SubscriptionTier.Free:
                    yield return "Live latency, jitter and loss monitor with game auto-detection";
                    yield return "Alerts when your internet gets spotty or goes down (with the likely cause), and when it's back";
                    yield return "Safe Optimize and the guided troubleshooter";
                    yield return "Network Doctor: full device + network checkup, speed test";
                    yield return "Safe one-click fixes (Wi-Fi, DNS refresh, sound, Bluetooth, quit hogs)";
                    break;
                case SubscriptionTier.Plus:
                    yield return "Heads-up before the times your internet usually gets spotty, and outage alerts for your provider";
                    yield return "Network repairs: rejoin network, reset adapter, fix DNS/IP settings, clear VPN leftovers";
                    yield return "Fix all, custom scans, path check";
                    yield return "Historical latency predictions and CSV export";
                    break;
                default:
                    yield return "Network details: every OSI layer finding, live";
                    yield return "Advanced diagnostics, route map and ISP outage center";
                    yield return "Background watch: rechecks and alerts when the connection changes";
                    yield return "Workspace optimization: call devices, PC hogs, visual effects";
                    yield return "Network report (for your internet provider) and adaptive-control preview";
                    break;
            }
        }
    }
}
