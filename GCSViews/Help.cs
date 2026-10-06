using MissionPlanner.Controls;
using MissionPlanner.Properties;
using MissionPlanner.Utilities;
using System;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    public partial class Help : MyUserControl, IActivate
    {
        public Help()
        {
            InitializeComponent();
        }

        public void Activate()
        {
            try
            {
                CHK_showconsole.Checked = Settings.Instance.GetBoolean("showconsole");
            }
            catch
            {
            }

            if (Program.WindowsStoreApp)
            {
                BUT_betaupdate.Visible = false;
                BUT_updatecheck.Visible = false;
            }
        }

        public void BUT_updatecheck_Click(object sender, EventArgs e)
        {
            try
            {
                if (Program.WindowsStoreApp)
                {
                    return;
                }
                Utilities.Update.CheckForUpdate(true);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(ex.ToString(), Strings.ERROR);
            }
        }

        private void CHK_showconsole_CheckedChanged(object sender, EventArgs e)
        {
            Settings.Instance["showconsole"] = CHK_showconsole.Checked.ToString();
        }

        private void Help_Load(object sender, EventArgs e)
        {
            richTextBox1.Rtf = ThemedHelpRtf(Resources.help_text);
            ThemeManager.ApplyThemeTo(richTextBox1);
        }

        /// <summary>
        /// The help RTF sets no body colour, so it draws in the system text colour (near black), unreadable on the dark
        /// themes, and its links are pure blue. On a dark theme give it the theme's text colour and a light blue for links;
        /// a light theme gets the file as it is.
        /// </summary>
        private static string ThemedHelpRtf(string rtf)
        {
            var back = ThemeManager.BGColor;
            const string oldTable = @"{\colortbl ;\red0\green0\blue255;}";
            if (back.GetBrightness() >= 0.5f || !rtf.Contains(oldTable))
                return rtf;

            var text = ThemeManager.Contrast(ThemeManager.TextColor, back) >= 4.5 ? ThemeManager.TextColor : Color.White;
            var newTable = @"{\colortbl ;\red127\green180\blue255;\red" + text.R + @"\green" + text.G + @"\blue" + text.B + ";}";
            return rtf.Replace(oldTable, newTable).Replace(@"\viewkind4\uc1", @"\viewkind4\uc1\cf2");
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://github.com/LikhitS/SarusOperationPlanner/releases");
        }

        private void BUT_betaupdate_Click(object sender, EventArgs e)
        {
            try
            {
                Utilities.Update.dobeta = true;
                if (Control.ModifierKeys == Keys.Control)
                {
                    Utilities.Update.domaster = true;
                    CustomMessageBox.Show("This will update to MASTER release");
                }

                Utilities.Update.DoUpdate();
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(ex.ToString(), Strings.ERROR);
            }
        }
    }
}