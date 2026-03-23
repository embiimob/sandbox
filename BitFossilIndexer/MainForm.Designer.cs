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

        // Chain-filter panel
        private Panel pnlChainFilter = null!;
        private Label lblChainsLabel = null!;
        private CheckBox chkBtcTestnet = null!;
        private CheckBox chkBtcMainnet = null!;
        private CheckBox chkMzc = null!;
        private CheckBox chkDog = null!;
        private CheckBox chkLtc = null!;
        private Button btnPause = null!;
        private Label lblTotalFolders = null!;

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
            Size = new Size(1050, 760);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ClrBackground;
            Icon = SystemIcons.Application;

            // ── Header panel ────────────────────────────────────────────────
            pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 72,
                BackColor = ClrPanel,
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
                Text = "Loads transaction roots into p2fk.io — blockchain detected from ADD file version byte",
                Font = new Font("Segoe UI", 9),
                ForeColor = ClrMuted,
                AutoSize = true,
                Location = new Point(20, 46)
            };

            pnlHeader.Controls.AddRange([lblTitle, lblSubtitle]);

            // ── Controls panel (root path + start/stop/clear) ────────────────
            pnlControls = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = ClrPanel,
            };
            pnlControls.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(35, 35, 60), 1);
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
                Width = 530,
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
                Location = new Point(640, 12),
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
                Location = new Point(686, 12),
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
                Location = new Point(796, 12),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Cursor = Cursors.Hand
            };
            btnClear.FlatAppearance.BorderColor = ClrMuted;
            btnClear.Click += btnClear_Click;

            pnlControls.Controls.AddRange([lblRootLabel, txtRoot, btnBrowse, btnStart, btnClear]);
            pnlControls.Resize += (s, e) =>
            {
                int right = pnlControls.ClientSize.Width - 12;
                btnClear.Left = right - btnClear.Width;
                btnStart.Left = btnClear.Left - btnStart.Width - 8;
                btnBrowse.Left = btnStart.Left - btnBrowse.Width - 8;
                txtRoot.Width = btnBrowse.Left - txtRoot.Left - 8;
            };

            // ── Chain-filter panel ───────────────────────────────────────────
            pnlChainFilter = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = ClrPanel,
            };
            pnlChainFilter.Paint += (s, e) =>
            {
                // Cyan separator at bottom
                using var pen = new Pen(ClrAccent, 1);
                e.Graphics.DrawLine(pen, 0, pnlChainFilter.Height - 1,
                    pnlChainFilter.Width, pnlChainFilter.Height - 1);
            };

            lblChainsLabel = new Label
            {
                Text = "Chains:",
                Font = new Font("Segoe UI", 9),
                ForeColor = ClrMuted,
                AutoSize = true,
                Location = new Point(12, 16)
            };

            chkBtcTestnet = MakeChainCheckBox("BTC testnet", ChainColours["BTC"]);
            chkBtcMainnet = MakeChainCheckBox("BTC mainnet", ChainColours["BTC"]);
            chkMzc        = MakeChainCheckBox("MZC",         ChainColours["MZC"]);
            chkDog        = MakeChainCheckBox("DOG",         ChainColours["DOG"]);
            chkLtc        = MakeChainCheckBox("LTC",         ChainColours["LTC"]);

            btnPause = new Button
            {
                Text = "⏸  Pause",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.FromArgb(40, 40, 70),
                ForeColor = ClrWhite,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(95, 26),
                Location = new Point(800, 12),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Enabled = false,
                Cursor = Cursors.Hand
            };
            btnPause.FlatAppearance.BorderColor = ClrMuted;
            btnPause.Click += btnPause_Click;

            lblTotalFolders = new Label
            {
                Text = "—",
                Font = new Font("Consolas", 9),
                ForeColor = ClrMuted,
                AutoSize = false,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(905, 15),
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };

            pnlChainFilter.Controls.AddRange([
                lblChainsLabel,
                chkBtcTestnet, chkBtcMainnet, chkMzc, chkDog, chkLtc,
                btnPause, lblTotalFolders
            ]);

            // Layout checkboxes and right-anchored controls
            pnlChainFilter.Resize += (s, e) => LayoutChainFilter();
            // Initial layout (fires after form loads)
            Load += (s, e) => LayoutChainFilter();

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
                Font = new Font("Cascadia Code", 9.5f, FontStyle.Regular, GraphicsUnit.Point, 0),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = false,
                DetectUrls = true
            };
            if (rtbLog.Font.Name != "Cascadia Code")
                rtbLog.Font = new Font("Consolas", 9.5f);

            pnlLog.Controls.Add(rtbLog);

            // ── Assemble (Bottom → Fill → Top controls in reverse stacking order) ──
            Controls.Add(pnlLog);           // Fill (middle)
            Controls.Add(pnlChainFilter);   // Top – below pnlControls
            Controls.Add(pnlControls);      // Top – below pnlHeader
            Controls.Add(pnlHeader);        // Top – very top (last DockStyle.Top added)
            Controls.Add(pnlFooter);        // Bottom

            ResumeLayout(false);
        }

        // ── Helper: creates a styled chain-filter CheckBox ────────────────────
        private static CheckBox MakeChainCheckBox(string name, Color fore)
        {
            var cb = new CheckBox
            {
                Text = $"{name} (0)",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = fore,
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat,
                Checked = true,
                AutoSize = true,
                Cursor = Cursors.Hand,
                Location = new Point(0, 0)  // positioned by LayoutChainFilter
            };
            return cb;
        }

        // ── Chain-filter layout (called on Load and Resize) ───────────────────
        private void LayoutChainFilter()
        {
            if (pnlChainFilter == null) return;

            int panelW = pnlChainFilter.ClientSize.Width;
            int cy = (pnlChainFilter.Height - 26) / 2;     // vertical centre for controls

            // Right-anchored controls
            lblTotalFolders.Left = panelW - 12 - lblTotalFolders.Width;
            btnPause.Top = cy;
            btnPause.Left = lblTotalFolders.Left - btnPause.Width - 8;

            // Distribute checkboxes between the "Chains:" label and btnPause
            int startX = lblChainsLabel.Right + 12;
            int endX   = btnPause.Left - 8;
            int avail  = endX - startX;

            CheckBox[] boxes = [chkBtcTestnet, chkBtcMainnet, chkMzc, chkDog, chkLtc];
            int gap = boxes.Length > 1 ? avail / boxes.Length : 0;
            int x = startX;
            foreach (var cb in boxes)
            {
                cb.Top  = cy;
                cb.Left = x;
                x += gap;
            }

            // Vertically centre the labels
            lblChainsLabel.Top  = (pnlChainFilter.Height - lblChainsLabel.Height) / 2;
            lblTotalFolders.Top = (pnlChainFilter.Height - lblTotalFolders.Height) / 2;
        }

        private void ApplyStyling() { }   // styling is applied inline above
    }
}
