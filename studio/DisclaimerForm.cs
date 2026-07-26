// =============================================================================
// DisclaimerForm - shown on EVERY Studio launch before the main window.
// The user must explicitly accept that MCPTerminal grants the connected MCP
// client full control of this system, with no warranty and no liability.
// =============================================================================
using System;
using System.Drawing;
using System.Windows.Forms;

namespace MCPTerminal.Studio;

public sealed class DisclaimerForm : Form
{
    public DisclaimerForm()
    {
        Text = "MCPTerminal - Security Warning";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 400);
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.White;
        TopMost = true;

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
        Controls.Add(title);

        // Exit is the safe default for Enter and Esc alike.
        AcceptButton = exit;
        CancelButton = exit;
    }

    // True only when the user explicitly accepted.
    public static bool ShowAndConfirm()
    {
        using var f = new DisclaimerForm();
        return f.ShowDialog() == DialogResult.OK;
    }
}
