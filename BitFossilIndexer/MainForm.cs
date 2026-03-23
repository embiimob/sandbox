using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BitFossilIndexer
{
    public partial class MainForm : Form
    {
        private const string DefaultRootPath = @"C:\bitfossil\ApertusMain\root";

        // ── run-state ──────────────────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private bool _running;
        private bool _paused;

        // Per-chain found-root counters (updated live)
        private readonly Dictionary<ApiTarget, int> _chainCounts = new();

        // ── colour palette ────────────────────────────────────────────────────
        private static readonly Color ClrBackground = Color.FromArgb(12, 12, 24);
        private static readonly Color ClrPanel      = Color.FromArgb(20, 20, 40);
        private static readonly Color ClrAccent     = Color.FromArgb(0, 200, 255);
        private static readonly Color ClrGreen      = Color.FromArgb(0, 230, 100);
        private static readonly Color ClrRed        = Color.FromArgb(255, 80, 80);
        private static readonly Color ClrYellow     = Color.FromArgb(255, 220, 60);
        private static readonly Color ClrMuted      = Color.FromArgb(120, 130, 150);
        private static readonly Color ClrWhite      = Color.FromArgb(220, 230, 245);

        // Chain badge colours (also used for checkbox ForeColor)
        private static readonly Dictionary<string, Color> ChainColours = new()
        {
            ["BTC"] = Color.FromArgb(247, 147, 26),
            ["LTC"] = Color.FromArgb(180, 180, 200),
            ["DOG"] = Color.FromArgb(203, 163, 28),
            ["MZC"] = Color.FromArgb(80, 200, 120),
        };

        public MainForm()
        {
            InitializeComponent();
            ApplyStyling();
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private void AppendLog(string text, Color colour, bool bold = false)
        {
            if (InvokeRequired) { Invoke(() => AppendLog(text, colour, bold)); return; }
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = colour;
            rtbLog.SelectionFont = bold ? new Font(rtbLog.Font, FontStyle.Bold) : rtbLog.Font;
            rtbLog.AppendText(text);
            rtbLog.SelectionColor = rtbLog.ForeColor;
            rtbLog.SelectionFont = rtbLog.Font;
            rtbLog.ScrollToCaret();
        }

        private void AppendLine(string text, Color colour, bool bold = false)
            => AppendLog(text + Environment.NewLine, colour, bold);

        private void SetStatus(string text, Color? colour = null)
        {
            if (InvokeRequired) { Invoke(() => SetStatus(text, colour)); return; }
            lblStatus.Text = text;
            lblStatus.ForeColor = colour ?? ClrMuted;
        }

        private void UpdateProgress(int done, int total)
        {
            if (InvokeRequired) { Invoke(() => UpdateProgress(done, total)); return; }
            progressBar.Maximum = total > 0 ? total : 1;
            progressBar.Value = Math.Min(done, progressBar.Maximum);
            lblProgress.Text = $"{done} / {total}";
        }

        private void UpdateTotalFolders(int total)
        {
            if (InvokeRequired) { Invoke(() => UpdateTotalFolders(total)); return; }
            lblTotalFolders.Text = $"📂 {total} folders";
            lblTotalFolders.ForeColor = ClrAccent;
        }

        // ── chain-count helpers ───────────────────────────────────────────────

        private void IncrementChainCount(ApiTarget target)
        {
            if (InvokeRequired) { Invoke(() => IncrementChainCount(target)); return; }
            _chainCounts[target] = _chainCounts.GetValueOrDefault(target) + 1;
            RefreshChainCheckboxText(target);
        }

        private void RefreshChainCheckboxText(ApiTarget target)
        {
            int count = _chainCounts.GetValueOrDefault(target);
            CheckBox? cb = target switch
            {
                { Blockchain: "BTC", Mainnet: false } => chkBtcTestnet,
                { Blockchain: "BTC", Mainnet: true  } => chkBtcMainnet,
                { Blockchain: "MZC" }                  => chkMzc,
                { Blockchain: "DOG" }                  => chkDog,
                { Blockchain: "LTC" }                  => chkLtc,
                _                                      => null
            };
            if (cb != null) cb.Text = $"{target.Label} ({count})";
        }

        private void ResetChainCountLabels()
        {
            _chainCounts.Clear();
            chkBtcTestnet.Text = "BTC testnet (0)";
            chkBtcMainnet.Text = "BTC mainnet (0)";
            chkMzc.Text        = "MZC (0)";
            chkDog.Text        = "DOG (0)";
            chkLtc.Text        = "LTC (0)";
        }

        // ── enabled-chains helper ─────────────────────────────────────────────

        private HashSet<ApiTarget> GetEnabledChains()
        {
            var set = new HashSet<ApiTarget>();
            if (chkBtcTestnet.Checked) set.Add(new ApiTarget("BTC", false));
            if (chkBtcMainnet.Checked) set.Add(new ApiTarget("BTC", true));
            if (chkMzc.Checked)        set.Add(new ApiTarget("MZC", true));
            if (chkDog.Checked)        set.Add(new ApiTarget("DOG", true));
            if (chkLtc.Checked)        set.Add(new ApiTarget("LTC", true));
            return set;
        }

        // ── pause helpers ─────────────────────────────────────────────────────

        /// <summary>Suspends the indexing loop while paused, waking every 150 ms
        /// to check whether the user has resumed or cancelled.</summary>
        private async Task WaitIfPausedAsync(CancellationToken ct)
        {
            while (_paused)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(150, ct);
            }
        }

        // ── button event handlers ─────────────────────────────────────────────

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (_running)
            {
                _cts?.Cancel();
                return;
            }

            string rootPath = txtRoot.Text.Trim();
            if (!Directory.Exists(rootPath))
            {
                MessageBox.Show($"Root folder not found:\n{rootPath}", "BitFossil Indexer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var enabledAtStart = GetEnabledChains();
            if (enabledAtStart.Count == 0)
            {
                MessageBox.Show("Please enable at least one blockchain.", "BitFossil Indexer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _cts     = new CancellationTokenSource();
            _running = true;
            _paused  = false;

            ResetChainCountLabels();

            btnStart.Text      = "⏹  Stop";
            btnStart.BackColor = ClrRed;
            btnPause.Enabled   = true;
            btnPause.Text      = "⏸  Pause";
            btnPause.BackColor = Color.FromArgb(40, 40, 70);
            btnClear.Enabled   = false;

            // Disable chain checkboxes while running
            foreach (var cb in new[] { chkBtcTestnet, chkBtcMainnet, chkMzc, chkDog, chkLtc })
                cb.Enabled = false;

            rtbLog.Clear();

            try
            {
                await RunIndexingAsync(rootPath, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendLine("\n⚠  Indexing cancelled by user.", ClrYellow, bold: true);
                SetStatus("Cancelled.", ClrYellow);
            }
            catch (Exception ex)
            {
                AppendLine($"\n❌  Unexpected error: {ex.Message}", ClrRed, bold: true);
                SetStatus("Error.", ClrRed);
            }
            finally
            {
                _running = false;
                _paused  = false;

                btnStart.Text      = "▶  Start";
                btnStart.BackColor = ClrGreen;
                btnPause.Enabled   = false;
                btnPause.Text      = "⏸  Pause";
                btnPause.BackColor = Color.FromArgb(40, 40, 70);
                btnClear.Enabled   = true;

                foreach (var cb in new[] { chkBtcTestnet, chkBtcMainnet, chkMzc, chkDog, chkLtc })
                    cb.Enabled = true;

                _cts?.Dispose();
                _cts = null;
            }
        }

        private void btnPause_Click(object sender, EventArgs e)
        {
            if (!_running) return;

            if (!_paused)
            {
                _paused            = true;
                btnPause.Text      = "▶  Resume";
                btnPause.BackColor = ClrGreen;
                btnPause.ForeColor = Color.Black;
                SetStatus("⏸  Paused — press Resume to continue.", ClrYellow);
            }
            else
            {
                _paused            = false;
                btnPause.Text      = "⏸  Pause";
                btnPause.BackColor = Color.FromArgb(40, 40, 70);
                btnPause.ForeColor = ClrWhite;
                SetStatus("Resumed…", ClrMuted);
            }
        }

        private void btnClear_Click(object sender, EventArgs e) => rtbLog.Clear();

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description  = "Select BitFossil root folder",
                SelectedPath = txtRoot.Text
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                txtRoot.Text = dlg.SelectedPath;
        }

        // ── core indexing loop ────────────────────────────────────────────────

        private async Task RunIndexingAsync(string rootPath, CancellationToken ct)
        {
            string[] folders = Directory.GetDirectories(rootPath);

            if (folders.Length == 0)
            {
                AppendLine("No transaction folders found in the root directory.", ClrYellow);
                SetStatus("Nothing to process.", ClrYellow);
                return;
            }

            UpdateTotalFolders(folders.Length);

            AppendLine($"🔍  Found {folders.Length} transaction folder(s) in:", ClrAccent, bold: true);
            AppendLine($"    {rootPath}", ClrMuted);
            AppendLine(new string('─', 72), ClrMuted);
            SetStatus($"Processing 0 / {folders.Length}…");
            UpdateProgress(0, folders.Length);

            int done      = 0;
            int succeeded = 0;
            int failed    = 0;
            int skipped   = 0;

            foreach (string folder in folders)
            {
                // ── pause point ──────────────────────────────────────────────
                await WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();

                string txId = Path.GetFileName(folder);

                AppendLog($"\n[{done + 1}/{folders.Length}] ", ClrMuted);
                AppendLog("TX: ", ClrMuted);
                AppendLine(txId, ClrAccent, bold: true);

                // Snapshot enabled chains for this iteration. Checkboxes are locked
                // while running, so this snapshot is always consistent with the UI state.
                var enabledChains = GetEnabledChains();

                ProcessOutcome outcome;
                try
                {
                    outcome = await TransactionProcessor.ProcessAsync(txId, folder, enabledChains, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppendLine($"        ⚠  Exception: {ex.Message}", ClrRed);
                    done++;
                    failed++;
                    UpdateProgress(done, folders.Length);
                    SetStatus($"Processing {done} / {folders.Length}…");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                // ── skipped ──────────────────────────────────────────────────
                if (outcome.Skipped)
                {
                    skipped++;
                    AppendLog("        ⊘ ", ClrMuted, bold: true);
                    AppendLine($"Skipped — {outcome.SkipReason}", ClrMuted);
                    done++;
                    UpdateProgress(done, folders.Length);
                    SetStatus($"Processing {done} / {folders.Length}…");
                    // No API call was made, so no rate-limit delay needed.
                    continue;
                }

                // ── API result ───────────────────────────────────────────────
                TransactionResult result = outcome.ApiResult!;
                string chain = result.Target.Blockchain;
                Color chainColour = ChainColours.TryGetValue(chain, out Color c) ? c : ClrWhite;

                AppendLog("        Chain: ", ClrMuted);
                AppendLog($"[ {result.Target.Label} ]", chainColour, bold: true);
                AppendLog("  URL: ", ClrMuted);
                AppendLine(result.Target.BuildUrl(txId), Color.FromArgb(100, 170, 255));

                if (result.Success)
                {
                    succeeded++;
                    IncrementChainCount(result.Target);   // update live counter
                    AppendLog("        ✔ ", ClrGreen, bold: true);
                    AppendLine("Response:", ClrGreen);
                    foreach (string line in result.ResponseBody
                                                  .Split('\n', System.StringSplitOptions.RemoveEmptyEntries))
                        AppendLine("          " + line.TrimEnd(), ClrWhite);
                }
                else
                {
                    failed++;
                    AppendLog("        ✘ ", ClrRed, bold: true);
                    string err = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? result.ResponseBody
                        : result.ErrorMessage;
                    AppendLine($"Error: {err}", ClrRed);
                    if (!string.IsNullOrWhiteSpace(result.ResponseBody) && result.ResponseBody != err)
                        AppendLine("          " + result.ResponseBody.Trim(), ClrMuted);
                }

                done++;
                UpdateProgress(done, folders.Length);
                SetStatus($"Processing {done} / {folders.Length}…");

                // Rate-limit: at least 2 s between p2fk.io calls.
                // Fallback paths already inserted per-attempt delays internally.
                if (done < folders.Length && !outcome.LastCallWasFallback)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            // ── Summary ──────────────────────────────────────────────────────
            AppendLine("\n" + new string('═', 72), ClrAccent);
            AppendLine(
                $"  Done!  ✔ {succeeded} succeeded   ✘ {failed} failed   ⊘ {skipped} skipped   total {done}",
                ClrAccent, bold: true);
            AppendLine(new string('═', 72), ClrAccent);
            SetStatus($"Finished. {succeeded} OK, {failed} failed, {skipped} skipped.", ClrGreen);
        }
    }
}

