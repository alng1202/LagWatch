using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LagWatch
{
    internal enum VisualEffectsMode
    {
        /// <summary>The visual-effects checkbox is off: nothing is changed.</summary>
        Unchanged,
        BestPerformance,
        BestAppearance,
        /// <summary>Windows' own default ("Let Windows choose what's best for my computer").</summary>
        WindowsDefault
    }

    internal sealed class OptimizationPlan
    {
        public bool BoostGamePriority { get; set; }
        public VisualEffectsMode VisualMode { get; set; }
        public bool Confirmed { get; set; }
    }

    /// <summary>
    /// Applies a visual-effects preset the way System › Advanced › Performance Options does, and records the choice
    /// there too, so that dialog shows what LagWatch set.
    /// </summary>
    internal static class VisualEffects
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint uiParam, IntPtr pvParam, uint flags);

        private const uint SpiSetUiEffects = 0x103F;
        private const uint SpiSetClientAreaAnimation = 0x1043;
        private const uint SpiSetMenuAnimation = 0x1003;
        private const uint SpiSetMenuFade = 0x1013;
        private const uint SpiSetTooltipAnimation = 0x1017;
        private const uint SpiSetTooltipFade = 0x1019;
        private const uint SpiSetSelectionFade = 0x1015;
        private const uint SpiSetDropShadow = 0x1025;
        private const uint SpiSetListBoxSmoothScrolling = 0x1007;
        private const uint SpiSetComboBoxAnimation = 0x1005;
        private const uint SpiSetCursorShadow = 0x101B;
        private const uint SpiSetFontSmoothing = 0x004B;
        private const uint UpdateAndBroadcast = 0x03; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE

        // These take their on/off value directly in pvParam (not a pointer to it).
        private static readonly uint[] Effects =
        {
            SpiSetUiEffects, SpiSetClientAreaAnimation, SpiSetMenuAnimation, SpiSetMenuFade, SpiSetTooltipAnimation,
            SpiSetTooltipFade, SpiSetSelectionFade, SpiSetDropShadow, SpiSetListBoxSmoothScrolling,
            SpiSetComboBoxAnimation, SpiSetCursorShadow,
        };

        public static string Describe(VisualEffectsMode mode)
        {
            switch (mode)
            {
                case VisualEffectsMode.BestPerformance: return "best performance";
                case VisualEffectsMode.BestAppearance: return "best appearance";
                case VisualEffectsMode.WindowsDefault: return "Windows default";
                default: return "unchanged";
            }
        }

        /// <summary>Returns true when every setting was accepted.</summary>
        public static bool Apply(VisualEffectsMode mode)
        {
            if (mode == VisualEffectsMode.Unchanged) return true;
            // Best performance turns animations, fades and shadows off. Best appearance and Windows' default turn them
            // on (the default on any PC that can run games). Font smoothing (ClearType) always stays on: turning it off
            // makes text jagged and doesn't make games faster.
            IntPtr on = mode == VisualEffectsMode.BestPerformance ? IntPtr.Zero : new IntPtr(1);
            bool ok = true;
            foreach (uint action in Effects)
                ok &= SystemParametersInfo(action, 0, on, UpdateAndBroadcast);
            ok &= SystemParametersInfo(SpiSetFontSmoothing, 1, IntPtr.Zero, UpdateAndBroadcast);

            // Performance Options' radio buttons: 0 = Let Windows choose, 1 = Best appearance, 2 = Best performance.
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects"))
                {
                    int setting = mode == VisualEffectsMode.WindowsDefault ? 0 : mode == VisualEffectsMode.BestAppearance ? 1 : 2;
                    if (key != null) key.SetValue("VisualFXSetting", setting, RegistryValueKind.DWord);
                }
            }
            catch { ok = false; }
            return ok;
        }
    }

    internal sealed class OptimizationForm : Form
    {
        private readonly RadioButton performanceRadio;
        private readonly RadioButton minimalRadio;
        private readonly RadioButton visualRadio;
        private readonly RadioButton customRadio;
        private readonly CheckBox priorityCheck;
        private readonly CheckBox visualCheck;
        private readonly ComboBox visualModeBox;
        private bool applyingPreset;

        private static readonly VisualEffectsMode[] VisualChoices =
            { VisualEffectsMode.BestPerformance, VisualEffectsMode.BestAppearance, VisualEffectsMode.WindowsDefault };

        public OptimizationPlan SelectedPlan { get; private set; }

        public OptimizationForm()
        {
            Theme.BeginDpiLayout(this);
            Text = "LagWatch — Optimize";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try
            {
                using (Icon sourceIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    if (sourceIcon != null) Icon = (Icon)sourceIcon.Clone();
            }
            catch { }
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.UiFont(9.5f);
            // The window takes the size of its content, so nothing overlaps at any display scaling.
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24, 20, 24, 20);

            TableLayoutPanel root = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
                Location = new Point(24, 20), Margin = new Padding(0)
            };
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                Text = "OPTIMIZE NETWORK + GAME SETUP", AutoSize = true, Font = Theme.UiFont(12f, true),
                ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 4)
            });
            root.Controls.Add(new Label
            {
                Text = "Pick a profile, check the list, then apply. Nothing changes until you click Apply.",
                AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 0, 0, 16)
            });

            // Profiles: one column, so long names never run into each other.
            CardPanel profiles = Section(root, "PROFILE");
            // No descriptions: the checklist below shows exactly what each profile changes.
            performanceRadio = AddRadio(profiles, "Performance");
            minimalRadio = AddRadio(profiles, "Balanced");
            visualRadio = AddRadio(profiles, "Visual quality");
            customRadio = AddRadio(profiles, "Custom");
            performanceRadio.CheckedChanged += delegate { if (performanceRadio.Checked) ApplyPreset(0); };
            minimalRadio.CheckedChanged += delegate { if (minimalRadio.Checked) ApplyPreset(1); };
            visualRadio.CheckedChanged += delegate { if (visualRadio.Checked) ApplyPreset(2); };

            CardPanel checklist = Section(root, "WHAT WILL CHANGE");
            priorityCheck = AddCheck(checklist, "Give the selected game higher priority (Above normal, until it closes)");
            visualCheck = AddCheck(checklist, "Change Windows visual effects");

            FlowLayoutPanel visualRow = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Margin = new Padding(26, 2, 0, 4), BackColor = Theme.Card
            };
            visualRow.Controls.Add(new Label
            {
                Text = "Preset", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 6, 10, 0)
            });
            visualModeBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.CardAlt, ForeColor = Theme.Text,
                FlatStyle = FlatStyle.Flat, Font = Theme.UiFont(9.5f), Margin = new Padding(0, 2, 0, 0)
            };
            visualModeBox.Items.AddRange(new object[]
            {
                "Best performance · turn off animations, fades and shadows",
                "Best appearance · turn all effects on",
                "Windows default · let Windows choose",
            });
            visualRow.Controls.Add(visualModeBox);
            Stack(checklist).Controls.Add(visualRow);

            Label details = new Label
            {
                Text = "Same switches as Windows' Performance Options: animations, fades, shadows and smooth scrolling. " +
                       "Text smoothing (ClearType) stays on. You can switch back to Windows default here at any time.",
                AutoSize = true, ForeColor = Theme.TextFaint, Font = Theme.UiFont(8.5f), Margin = new Padding(0, 6, 0, 0)
            };
            Stack(checklist).Controls.Add(details);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 0, 0)
            };
            AccentButton apply = new AccentButton { Text = "APPLY", Primary = true, Margin = new Padding(8, 0, 0, 0) };
            AccentButton cancel = new AccentButton { Text = "CANCEL", DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
            apply.Click += delegate
            {
                SelectedPlan = BuildPlan();
                SelectedPlan.Confirmed = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            buttons.Controls.Add(apply);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons);
            AcceptButton = apply;
            CancelButton = cancel;

            foreach (CheckBox check in new[] { priorityCheck, visualCheck })
                check.CheckedChanged += delegate { OnChecklistEdited(); };
            visualModeBox.SelectedIndexChanged += delegate { OnChecklistEdited(); };

            performanceRadio.Checked = true;
            ApplyPreset(0);
            details.MaximumSize = new Size(1, 0); // measured below, after scaling
            Theme.EndDpiLayout(this);

            // Sizes measured from text are set after DPI scaling (the text is already at the display's size).
            SuspendLayout();
            visualModeBox.Width = visualModeBox.Items.Cast<string>()
                .Max(s => TextRenderer.MeasureText(s, visualModeBox.Font).Width) + Theme.Px(this, 36);
            visualModeBox.DropDownWidth = visualModeBox.Width;
            foreach (AccentButton b in new[] { apply, cancel })
                b.Size = new Size(TextRenderer.MeasureText(b.Text, b.Font).Width + Theme.Px(this, 48), Theme.Px(this, 36));
            details.MaximumSize = new Size(Math.Max(visualRow.PreferredSize.Width + visualRow.Margin.Left,
                priorityCheck.PreferredSize.Width), 0);
            // Both sections share the widest one's width.
            int width = new[] { profiles, checklist }.Max(c => c.PreferredSize.Width);
            foreach (CardPanel card in new[] { profiles, checklist }) card.MinimumSize = new Size(width, 0);
            ResumeLayout(true);
        }

        private static CardPanel Section(TableLayoutPanel root, string title)
        {
            root.Controls.Add(new Label
            {
                Text = title, AutoSize = true, Font = Theme.UiFont(8.5f, true), ForeColor = Theme.TextFaint,
                Margin = new Padding(0, 0, 0, 6)
            });
            CardPanel card = new CardPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 12, 16, 12),
                Margin = new Padding(0, 0, 0, 16)
            };
            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Card, Dock = DockStyle.Fill
            };
            card.Controls.Add(stack);
            root.Controls.Add(card);
            return card;
        }

        private static FlowLayoutPanel Stack(CardPanel card) { return (FlowLayoutPanel)card.Controls[0]; }

        private static RadioButton AddRadio(CardPanel card, string name)
        {
            RadioButton radio = new RadioButton
            {
                Text = name, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Card,
                Margin = new Padding(0, 3, 0, 3), UseMnemonic = false
            };
            Stack(card).Controls.Add(radio);
            return radio;
        }

        private static CheckBox AddCheck(CardPanel card, string text)
        {
            CheckBox check = new CheckBox
            {
                Text = text, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Card,
                Margin = new Padding(0, 3, 0, 3), UseMnemonic = false
            };
            Stack(card).Controls.Add(check);
            return check;
        }

        /// <summary>Profiles: 0 Performance, 1 Balanced, 2 Visual quality.</summary>
        private void ApplyPreset(int preset)
        {
            if (visualModeBox == null) return;
            applyingPreset = true;
            priorityCheck.Checked = preset != 2;
            visualCheck.Checked = preset != 1;
            visualModeBox.SelectedIndex = preset == 2 ? 1 : 0;
            visualModeBox.Enabled = visualCheck.Checked;
            applyingPreset = false;
        }

        /// <summary>Editing the list by hand makes it a Custom profile.</summary>
        private void OnChecklistEdited()
        {
            visualModeBox.Enabled = visualCheck.Checked;
            if (!applyingPreset && !customRadio.Checked) customRadio.Checked = true;
        }

        private OptimizationPlan BuildPlan()
        {
            VisualEffectsMode visual = visualCheck.Checked && visualModeBox.SelectedIndex >= 0
                ? VisualChoices[visualModeBox.SelectedIndex] : VisualEffectsMode.Unchanged;
            return new OptimizationPlan
            {
                BoostGamePriority = priorityCheck.Checked, VisualMode = visual
            };
        }
    }
}
