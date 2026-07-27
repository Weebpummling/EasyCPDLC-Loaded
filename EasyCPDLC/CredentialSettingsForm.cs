using System;
using System.Drawing;
using System.Windows.Forms;

namespace EasyCPDLC
{
    internal sealed class CredentialSettingsForm : Form
    {
        private readonly TextBox cidBox = new();
        private readonly TextBox hoppieBox = new();
        private readonly TextBox simbriefBox = new();
        private readonly TextBox eloadBox = new();
        private readonly TextBox sayIntentionsBox = new();
        private readonly TextBox aocAddressBox = new();

        internal CredentialSettingsForm()
        {
            Text = "EasyCPDLC credentials";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(520, 454);
            BackColor = Color.FromArgb(9, 17, 23);
            ForeColor = Color.FromArgb(218, 238, 242);
            Font = new Font("Segoe UI", 9.2f, FontStyle.Regular);

            Label heading = NewLabel("Shared connection credentials", 20, 16, 470, 28, 14f, FontStyle.Bold);
            Label explanation = NewLabel(
                "These values are shared by the CDU, GNS430, Hoppie, SimBrief, and eLoadControl workflows.",
                20, 48, 480, 38, 8.8f, FontStyle.Regular);
            Controls.Add(heading);
            Controls.Add(explanation);

            AddField("VATSIM CID", cidBox, 20, 96, false, 12);
            AddField("Hoppie logon code", hoppieBox, 20, 146, true, 64);
            AddField("SimBrief username / pilot ID", simbriefBox, 20, 196, false, 64);
            AddField("eLoadControl API key", eloadBox, 20, 246, true, 128);
            AddField("SayIntentions API key", sayIntentionsBox, 20, 296, true, 128);
            // Not a secret - it is the operator's public ACARS address - so it is shown
            // in clear. Blank switches FMC position reporting off entirely.
            AddField("Company ACARS address", aocAddressBox, 20, 346, false, 8);

            cidBox.Text = MainForm.SavedCID > 0 ? MainForm.SavedCID.ToString() : string.Empty;
            hoppieBox.Text = MainForm.SavedHoppieCode;
            simbriefBox.Text = MainForm.SimbriefID;
            eloadBox.Text = MainForm.SavedELoadControlApiKey;
            sayIntentionsBox.Text = MainForm.SavedSayIntentionsApiKey;
            aocAddressBox.Text = MainForm.SavedAocAddress;

            CheckBox reveal = new()
            {
                Text = "Show protected codes",
                Location = new Point(20, 400),
                Size = new Size(190, 28),
                ForeColor = ForeColor,
                BackColor = BackColor
            };
            reveal.CheckedChanged += (_, __) =>
            {
                hoppieBox.UseSystemPasswordChar = !reveal.Checked;
                eloadBox.UseSystemPasswordChar = !reveal.Checked;
                sayIntentionsBox.UseSystemPasswordChar = !reveal.Checked;
            };
            Controls.Add(reveal);

            Button cancel = NewButton("Cancel", 322, 402, 82);
            cancel.DialogResult = DialogResult.Cancel;
            Button save = NewButton("Save", 414, 402, 82);
            save.Click += SaveClicked;
            Controls.Add(cancel);
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private void AddField(string label, TextBox box, int x, int y, bool secret, int maxLength)
        {
            Controls.Add(NewLabel(label, x, y, 210, 23, 9f, FontStyle.Bold));
            box.Location = new Point(240, y - 2);
            box.Size = new Size(256, 28);
            box.MaxLength = maxLength;
            box.UseSystemPasswordChar = secret;
            box.BackColor = Color.FromArgb(3, 10, 15);
            box.ForeColor = Color.White;
            box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(box);
        }

        private Label NewLabel(string text, int x, int y, int width, int height, float size, FontStyle style)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                ForeColor = ForeColor,
                BackColor = BackColor,
                Font = new Font("Segoe UI", size, style),
                AutoEllipsis = true
            };
        }

        private Button NewButton(string text, int x, int y, int width)
        {
            return new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(18, 43, 54),
                ForeColor = Color.White
            };
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            if (!TryValidate(cidBox.Text, hoppieBox.Text, out int cid, out string error))
            {
                MessageBox.Show(this, error, "Check credentials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                MainForm.SavedCID = cid;
                MainForm.SavedHoppieCode = hoppieBox.Text.Trim();
                MainForm.SimbriefID = simbriefBox.Text.Trim();
                MainForm.SavedELoadControlApiKey = eloadBox.Text.Trim();
                MainForm.SavedSayIntentionsApiKey = sayIntentionsBox.Text.Trim();
                MainForm.SavedAocAddress = aocAddressBox.Text.Trim();
                Properties.Settings.Default.Save();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Credentials were not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal static bool TryValidate(string cidText, string hoppieCode, out int cid, out string error)
        {
            error = string.Empty;
            cid = 0;
            string cidValue = (cidText ?? string.Empty).Trim();
            if (cidValue.Length > 0 && (!int.TryParse(cidValue, out cid) || cid <= 0))
            {
                error = "Enter a valid numeric VATSIM CID, or leave it blank.";
                return false;
            }
            if ((hoppieCode ?? string.Empty).Trim().Length > 64)
            {
                error = "The Hoppie logon code is too long.";
                return false;
            }
            return true;
        }
    }
}
