using System;
using System.Drawing;
using System.Windows.Forms;

namespace BitFossilIndexer
{
    public partial class MainForm : Form
    {
        // ── controls ──────────────────────────────────────────────────────────
        private Panel pnlHeader = null!;
        private Label lblTitle = null!;
        private Label lblSubtitle = null!;
        private Panel pnlControls = null!;
        private Label lblRootLabel = null!;
        private TextBox txtRoot = null!;
        private Button btnBrowse = null!;
        private Button btnStart = null!;
        private Button btnClear = null!;
        private Panel pnlLog = null!;
        private RichTextBox rtbLog = null!;
        private Panel pnlFooter = null!;
        private Label lblStatus = null!;
        private Label lblProgress = null!;
        private ProgressBar progressBar = null!;

        private void InitializeComponent()
        {
            SuspendLayout();

            // ── Form ────────────────────────────────────────────────────────
            Text = "BitFossil Indexer  ·  p2fk.io";
            Size = new Size(1000, 720);
            MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ClrBackground;
            Icon = SystemIcons.Application;

            // ── Header panel ────────────────────────────────────────────────
            pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 72,
                BackColor = ClrPanel,
                Padding = new Padding(16, 8, 16, 8)
            };

            lblTitle = new Label
            {
                Text = "⛏  BitFossil Indexer",
                Font = new Font("Segoe UI", 20, FontStyle.Bold),
                ForeColor = ClrAccent,
                AutoSize = true,
                Location = new Point(16, 8)
            };

            lblSubtitle = new Label
            {
                Text = "Loads transaction roots into p2fk.io",
                Font = new Font("Segoe UI", 9),
                ForeColor = ClrMuted,
                AutoSize = true,
                Location = new Point(20, 46)
            };

            pnlHeader.Controls.AddRange([lblTitle, lblSubtitle]);

            // ── Controls panel ───────────────────────────────────────────────
            pnlControls = new Panel
            {
                Dock = DockStyle.Top,
                Height = 54,
                BackColor = ClrPanel,
                Padding = new Padding(12, 8, 12, 8)
            };
            // Thin separator at bottom
            pnlControls.Paint += (s, e) =>
            {
                using var pen = new Pen(ClrAccent, 1);
                e.Graphics.DrawLine(pen, 0, pnlControls.Height - 1,
                    pnlControls.Width, pnlControls.Height - 1);
            };

            lblRootLabel = new Label
            {
                Text = "Root folder:",
                Font = new Font("Segoe UI", 9),
                ForeColor = ClrMuted,
                AutoSize = true,
                Location = new Point(12, 16)
            };

            txtRoot = new TextBox
            {
                Text = DefaultRootPath,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(30, 30, 55),
                ForeColor = ClrWhite,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(100, 12),
                Width = 520,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            btnBrowse = new Button
            {
                Text = "📂",
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(40, 40, 70),
                ForeColor = ClrWhite,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(36, 26),
                Location = new Point(630, 12),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Cursor = Cursors.Hand
            };
            btnBrowse.FlatAppearance.BorderColor = ClrMuted;
            btnBrowse.Click += btnBrowse_Click;

            btnStart = new Button
            {
                Text = "▶  Start",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = ClrGreen,
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(100, 26),
                Location = new Point(676, 12),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Cursor = Cursors.Hand
            };
            btnStart.FlatAppearance.BorderSize = 0;
            btnStart.Click += btnStart_Click;

            btnClear = new Button
            {
                Text = "🗑 Clear",
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(40, 40, 70),
                ForeColor = ClrWhite,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(80, 26),
                Location = new Point(786, 12),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Cursor = Cursors.Hand
            };
            btnClear.FlatAppearance.BorderColor = ClrMuted;
            btnClear.Click += btnClear_Click;

            pnlControls.Controls.AddRange([lblRootLabel, txtRoot, btnBrowse, btnStart, btnClear]);
            pnlControls.Resize += (s, e) =>
            {
                // Keep text box stretching and buttons anchored right
                int rightEdge = pnlControls.ClientSize.Width - 12;
                btnClear.Left = rightEdge - btnClear.Width;
                btnStart.Left = btnClear.Left - btnStart.Width - 8;
                btnBrowse.Left = btnStart.Left - btnBrowse.Width - 8;
                txtRoot.Width = btnBrowse.Left - txtRoot.Left - 8;
            };

            // ── Footer panel ─────────────────────────────────────────────────
            pnlFooter = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                BackColor = ClrPanel
            };

            progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Continuous,
                Height = 10,
                Dock = DockStyle.Top,
                BackColor = ClrPanel,
                ForeColor = ClrAccent
            };

            lblProgress = new Label
            {
                Text = "0 / 0",
                Font = new Font("Consolas", 8),
                ForeColor = ClrMuted,
                AutoSize = true,
                Location = new Point(8, 14)
            };

            lblStatus = new Label
            {
                Text = "Ready.",
                Font = new Font("Segoe UI", 9),
                ForeColor = ClrMuted,
                AutoEllipsis = true,
                Location = new Point(70, 12),
                Width = 700,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            pnlFooter.Controls.AddRange([progressBar, lblProgress, lblStatus]);

            // ── Log panel ────────────────────────────────────────────────────
            pnlLog = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ClrBackground,
                Padding = new Padding(6)
            };

            rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(8, 8, 18),
                ForeColor = ClrWhite,
                Font = new Font("Cascadia Code", 9.5f, FontStyle.Regular,
                    GraphicsUnit.Point, 0),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = false,
                DetectUrls = true
            };
            // Fallback font if Cascadia Code is not installed
            if (rtbLog.Font.Name != "Cascadia Code")
                rtbLog.Font = new Font("Consolas", 9.5f);

            pnlLog.Controls.Add(rtbLog);

            // ── Assemble form ─────────────────────────────────────────────────
            Controls.Add(pnlLog);       // Fill
            Controls.Add(pnlControls);  // Top (added after header so it's below)
            Controls.Add(pnlHeader);    // Top (first)
            Controls.Add(pnlFooter);    // Bottom

            ResumeLayout(false);
        }

        // ── Styling helpers that require runtime values ───────────────────────
        private void ApplyStyling()
        {
            // Nothing additional needed – styling applied in InitializeComponent.
        }
    }
}
