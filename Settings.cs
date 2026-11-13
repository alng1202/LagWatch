using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LagWatch
{
    internal sealed class AppSettings
    {
        public bool LaunchAtStartup { get; set; }
        public bool AutoPollOnLaunch { get; set; }
        public bool CloseToTrayOnClose { get; set; }
        public string ThemeName { get; set; }
        /// <summary>Notifications before, during and after spotty internet, and provider outage alerts.</summary>
        public bool ConnectionAlerts { get; set; }

        public AppSettings()
        {
            LaunchAtStartup = true;
            AutoPollOnLaunch = true;
            CloseToTrayOnClose = true;
            ThemeName = "Dark";
            ConnectionAlerts = true;
        }

        public AppSettings Clone()
        {
            return new AppSettings
            {
                LaunchAtStartup = LaunchAtStartup,
                AutoPollOnLaunch = AutoPollOnLaunch,
                CloseToTrayOnClose = CloseToTrayOnClose,
                ThemeName = ThemeName,
                ConnectionAlerts = ConnectionAlerts
            };
        }
    }

    internal static class SettingsStore
    {
        internal static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LagWatch", "settings.ini");
            }
        }

        internal static AppSettings Load()
        {
            return Load(DefaultPath);
        }

        internal static AppSettings Load(string path)
        {
            AppSettings settings = new AppSettings();
            try
            {
                if (!File.Exists(path)) return settings;
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    bool enabled;
                    if (key.Equals("launch_at_startup", StringComparison.OrdinalIgnoreCase) &&
                        Boolean.TryParse(value, out enabled))
                        settings.LaunchAtStartup = enabled;
                    else if (key.Equals("auto_poll_on_launch", StringComparison.OrdinalIgnoreCase) &&
                        Boolean.TryParse(value, out enabled))
                        settings.AutoPollOnLaunch = enabled;
                    else if (key.Equals("close_to_tray_on_close", StringComparison.OrdinalIgnoreCase) &&
                        Boolean.TryParse(value, out enabled))
                        settings.CloseToTrayOnClose = enabled;
                    else if (key.Equals("connection_alerts", StringComparison.OrdinalIgnoreCase) &&
                        Boolean.TryParse(value, out enabled))
                        settings.ConnectionAlerts = enabled;
                    else if (key.Equals("theme", StringComparison.OrdinalIgnoreCase))
                        settings.ThemeName = Theme.NormalizePreset(value);
                }
            }
            catch
            {
                // Invalid or inaccessible settings fall back to safe defaults.
            }
            settings.ThemeName = Theme.NormalizePreset(settings.ThemeName);
            return settings;
        }

        internal static void Save(AppSettings settings)
        {
            Save(DefaultPath, settings);
        }

        internal static void Save(string path, AppSettings settings)
        {
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string content = "launch_at_startup=" + settings.LaunchAtStartup.ToString().ToLowerInvariant() + Environment.NewLine +
                "auto_poll_on_launch=" + settings.AutoPollOnLaunch.ToString().ToLowerInvariant() + Environment.NewLine +
                "close_to_tray_on_close=" + settings.CloseToTrayOnClose.ToString().ToLowerInvariant() + Environment.NewLine +
                "connection_alerts=" + settings.ConnectionAlerts.ToString().ToLowerInvariant() + Environment.NewLine +
                "theme=" + Theme.NormalizePreset(settings.ThemeName) + Environment.NewLine;
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }

    internal sealed class OptionsForm : Form
    {
        private readonly CheckBox startupCheck;
        private readonly CheckBox autoPollCheck;
        private readonly CheckBox closeToTrayCheck;
        private readonly CheckBox alertsCheck;
        private readonly ComboBox themeBox;

        /// <param name="previewTheme">Called as soon as a theme is picked, so the app shows it live before saving.</param>
        internal OptionsForm(AppSettings settings, Action<string> previewTheme = null)
        {
            Theme.BeginDpiLayout(this);
            Text = "LagWatch Options";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(468, 468);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.UiFont(9f);

            Label title = new Label();
            title.Text = "OPTIONS";
            title.AutoSize = true;
            title.Font = Theme.UiFont(15f, true);
            title.ForeColor = Theme.Text;
            title.Location = new Point(24, 20);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "Choose how LagWatch starts and looks on this PC.";
            subtitle.AutoSize = true;
            subtitle.ForeColor = Theme.TextMuted;
            subtitle.Location = new Point(26, 53);
            Controls.Add(subtitle);

            CardPanel behaviorCard = new CardPanel();
            behaviorCard.Location = new Point(24, 82);
            behaviorCard.Size = new Size(420, 218);
            Controls.Add(behaviorCard);

            Label behaviorTitle = new Label();
            behaviorTitle.Text = "STARTUP & MONITORING";
            behaviorTitle.AutoSize = true;
            behaviorTitle.UseMnemonic = false;
            behaviorTitle.Font = Theme.UiFont(8.5f, true);
            behaviorTitle.ForeColor = Theme.TextFaint;
            behaviorTitle.Location = new Point(16, 14);
            behaviorCard.Controls.Add(behaviorTitle);

            startupCheck = new CheckBox();
            startupCheck.Text = "Launch LagWatch when I sign in to Windows";
            startupCheck.Checked = settings.LaunchAtStartup;
            startupCheck.AutoSize = true;
            startupCheck.ForeColor = Theme.Text;
            startupCheck.Location = new Point(18, 47);
            behaviorCard.Controls.Add(startupCheck);

            autoPollCheck = new CheckBox();
            autoPollCheck.Text = "Automatically poll the local gateway when LagWatch opens";
            autoPollCheck.Checked = settings.AutoPollOnLaunch;
            autoPollCheck.AutoSize = true;
            autoPollCheck.ForeColor = Theme.Text;
            autoPollCheck.Location = new Point(18, 81);
            behaviorCard.Controls.Add(autoPollCheck);

            closeToTrayCheck = new CheckBox();
            closeToTrayCheck.Text = "Keep LagWatch running when I close the window";
            closeToTrayCheck.Checked = settings.CloseToTrayOnClose;
            closeToTrayCheck.AutoSize = true;
            closeToTrayCheck.ForeColor = Theme.Text;
            closeToTrayCheck.Location = new Point(18, 115);
            behaviorCard.Controls.Add(closeToTrayCheck);

            alertsCheck = new CheckBox();
            alertsCheck.Text = "Notify me before, during and after spotty internet";
            alertsCheck.Checked = settings.ConnectionAlerts;
            alertsCheck.AutoSize = true;
            alertsCheck.ForeColor = Theme.Text;
            alertsCheck.Location = new Point(18, 149);
            behaviorCard.Controls.Add(alertsCheck);

            Label behaviorHint = new Label();
            behaviorHint.Text = "Turning off automatic polling leaves LagWatch idle until you press Start.";
            behaviorHint.AutoSize = true;
            behaviorHint.Font = Theme.UiFont(8f);
            behaviorHint.ForeColor = Theme.TextFaint;
            behaviorHint.Text = "Close hides to tray; minimize stays on the taskbar.";
            behaviorHint.Location = new Point(20, 183);
            behaviorCard.Controls.Add(behaviorHint);

            CardPanel appearanceCard = new CardPanel();
            appearanceCard.Location = new Point(24, 312);
            appearanceCard.Size = new Size(420, 80);
            Controls.Add(appearanceCard);

            Label appearanceTitle = new Label();
            appearanceTitle.Text = "THEME";
            appearanceTitle.AutoSize = true;
            appearanceTitle.Font = Theme.UiFont(8.5f, true);
            appearanceTitle.ForeColor = Theme.TextFaint;
            appearanceTitle.Location = new Point(16, 30);
            appearanceCard.Controls.Add(appearanceTitle);

            themeBox = new ComboBox();
            themeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            themeBox.FlatStyle = FlatStyle.Flat;
            themeBox.BackColor = Theme.CardAlt;
            themeBox.ForeColor = Theme.Text;
            themeBox.Font = Theme.UiFont(9f, true);
            themeBox.Items.AddRange(new object[] { "Dark", "Midnight", "Light" });
            themeBox.SelectedItem = Theme.NormalizePreset(settings.ThemeName);
            themeBox.SelectedIndexChanged += delegate
            {
                if (previewTheme != null) previewTheme(Theme.NormalizePreset(themeBox.SelectedItem as string));
            };
            themeBox.Location = new Point(110, 24);
            themeBox.Size = new Size(282, 28);
            appearanceCard.Controls.Add(themeBox);

            AccentButton cancelButton = new AccentButton();
            cancelButton.Text = "CANCEL";
            cancelButton.Size = new Size(96, 36);
            cancelButton.Location = new Point(232, 408);
            cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(cancelButton);

            AccentButton saveButton = new AccentButton();
            saveButton.Text = "SAVE CHANGES";
            saveButton.Size = new Size(108, 36);
            saveButton.Location = new Point(336, 408);
            saveButton.Primary = true;
            saveButton.DialogResult = DialogResult.OK;
            Controls.Add(saveButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
            Theme.EndDpiLayout(this);
        }

        /// <summary>Render previews: picks a theme as if the user chose it in the list.</summary>
        internal void SelectThemeForPreview(string name) { themeBox.SelectedItem = Theme.NormalizePreset(name); }

        internal AppSettings SelectedSettings
        {
            get
            {
                return new AppSettings
                {
                    LaunchAtStartup = startupCheck.Checked,
                    AutoPollOnLaunch = autoPollCheck.Checked,
                    CloseToTrayOnClose = closeToTrayCheck.Checked,
                    ConnectionAlerts = alertsCheck.Checked,
                    ThemeName = Theme.NormalizePreset(themeBox.SelectedItem as string)
                };
            }
        }
    }
}
