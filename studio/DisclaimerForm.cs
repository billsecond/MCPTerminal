// =============================================================================
// DisclaimerForm - shown on EVERY Studio launch before the main window.
// The user must explicitly accept that MCPTerminal grants the connected MCP
// client full control of this system, with no warranty and no liability.
// =============================================================================
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace MCPTerminal.Studio;

public sealed class DisclaimerForm : Form
{
    // Remembered choice lives next to the other Studio UI state. Only an
    // explicit Accept-with-the-box-ticked writes it, so the warning is never
    // suppressed by anything the user did not deliberately do.
    static string SettingsFile(string root) => Path.Combine(root, "studio-settings.json");

    public static bool IsSuppressed(string root)
    {
        try
        {
            string p = SettingsFile(root);
            if (!File.Exists(p)) return false;
            var o = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p))
                as System.Text.Json.Nodes.JsonObject;
            return o?["suppressDisclaimer"]?.GetValue<bool>() == true;
        }
        catch { return false; }
    }

    static void SetSuppressed(string root, bool value)
    {
        try
        {
            string p = SettingsFile(root);
            var o = File.Exists(p)
                ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p)) as System.Text.Json.Nodes.JsonObject
                : new System.Text.Json.Nodes.JsonObject();
            o ??= new System.Text.Json.Nodes.JsonObject();
            o["suppressDisclaimer"] = value;
            o["acceptedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.WriteAllText(p, o.ToJsonString());
        }
        catch { }
    }

    CheckBox _dontShow;

    // Both logos ship as embedded resources so the dialog never depends on
    // files sitting next to the exe.
    public static Image LoadLogo(string logicalName)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return Image.FromStream(ms);
        }
        catch { return null; }
    }

    public static Icon LoadAppIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MCPTerminal.icon.ico");
            return s == null ? null : new Icon(s);
        }
        catch { return null; }
    }

    public DisclaimerForm()
    {
        Text = "MCPTerminal - Security Warning";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 470);
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.White;
        TopMost = true;
        ShowInTaskbar = true;
        var ic = LoadAppIcon();
        if (ic != null) Icon = ic;

        // The wordmark is dark navy on transparent, so it sits on a light band
        // rather than vanishing into the dark dialog.
        var brand = new Panel
        {
            Dock = DockStyle.Top,
            Height = 78,
            BackColor = Color.FromArgb(244, 245, 247),
        };
        var logo = LoadLogo("MCPTerminal.logo-wide.png");
        if (logo != null)
        {
            brand.Controls.Add(new PictureBox
            {
                Image = logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 10, 14, 10),
                BackColor = Color.Transparent,
            });
        }

        var title = new Label
        {
            Text = "⚠  WARNING - FULL SYSTEM CONTROL",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(248, 81, 73),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 56,
        };

        var body = new Label
        {
            Text =
                "MCPTerminal exposes real shell terminals on this computer to any " +
                "connected MCP client (such as an AI assistant).\r\n\r\n" +
                "A connected client can run ANY command your user account can run: " +
                "read, modify and delete files, install or remove software, access " +
                "the network, and control this system.\r\n\r\n" +
                "This software is provided \"AS IS\", WITHOUT WARRANTY OF ANY KIND, " +
                "express or implied. The author(s) accept NO responsibility or " +
                "liability for any damage, data loss, security incident, or other " +
                "consequence of using it.\r\n\r\n" +
                "By clicking Accept, you confirm that you understand these risks " +
                "and assume ALL responsibility for what connected clients do on " +
                "this system.",
            Font = new Font("Segoe UI", 10f),
            ForeColor = Color.FromArgb(220, 220, 225),
            AutoSize = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 4, 24, 4),
        };

        // AutoSize so the control is exactly box + label: clicking the text
        // toggles it, which is what anyone expects from a checkbox caption.
        _dontShow = new CheckBox
        {
            Text = "Don't show this warning again on this computer",
            AutoSize = true,
            Location = new Point(24, 7),
            ForeColor = Color.FromArgb(170, 170, 178),
            Font = new Font("Segoe UI", 9f),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft,
            CheckAlign = ContentAlignment.MiddleLeft,
        };
        var suppressRow = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        suppressRow.Controls.Add(_dontShow);

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            ColumnCount = 2,
            Padding = new Padding(16, 12, 16, 16),
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        var exit = new Button
        {
            Text = "EXIT",
            DialogResult = DialogResult.Cancel,
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(196, 30, 30),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
        };
        exit.FlatAppearance.BorderSize = 0;

        var accept = new Button
        {
            Text = "I UNDERSTAND - ACCEPT",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 45, 52),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
        };
        accept.FlatAppearance.BorderSize = 0;

        buttons.Controls.Add(exit, 0, 0);
        buttons.Controls.Add(accept, 1, 0);

        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(suppressRow);
        Controls.Add(title);
        Controls.Add(brand);

        // Exit is the safe default for Enter and Esc alike.
        AcceptButton = exit;
        CancelButton = exit;
    }

    // A warning nobody sees is not a warning. TopMost alone is not enough when
    // another app owns the foreground at launch, so claim it explicitly and
    // flash the taskbar button in case the user is on another monitor.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            BringToFront();
            Activate();
            Focus();
            FlashWindow(Handle, true);
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool FlashWindow(IntPtr hwnd, bool invert);

    // True when the user accepted - or accepted earlier and asked not to be
    // shown it again. Declining never records anything.
    public static bool ShowAndConfirm(string root)
    {
        if (IsSuppressed(root)) return true;
        using var f = new DisclaimerForm();
        bool ok = f.ShowDialog() == DialogResult.OK;
        if (ok && f._dontShow.Checked) SetSuppressed(root, true);
        return ok;
    }
}
