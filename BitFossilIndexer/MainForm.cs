using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BitFossilIndexer
{
    public partial class MainForm : Form
    {
        private const string DefaultRootPath = @"C:\bitfossil\ApertusMain\root";

        private CancellationTokenSource? _cts;
        private bool _running;

        // ── colours ──────────────────────────────────────────────────────────
        private static readonly Color ClrBackground = Color.FromArgb(12, 12, 24);
        private static readonly Color ClrPanel = Color.FromArgb(20, 20, 40);
        private static readonly Color ClrAccent = Color.FromArgb(0, 200, 255);
        private static readonly Color ClrGreen = Color.FromArgb(0, 230, 100);
        private static readonly Color ClrRed = Color.FromArgb(255, 80, 80);
        private static readonly Color ClrYellow = Color.FromArgb(255, 220, 60);
        private static readonly Color ClrMuted = Color.FromArgb(120, 130, 150);
        private static readonly Color ClrWhite = Color.FromArgb(220, 230, 245);

        // Chain badge colours
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

        // ── runtime helpers ───────────────────────────────────────────────────

        private void AppendLog(string text, Color colour, bool bold = false)
        {
            if (InvokeRequired) { Invoke(() => AppendLog(text, colour, bold)); return; }

            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = colour;
            rtbLog.SelectionFont = bold
                ? new Font(rtbLog.Font, FontStyle.Bold)
                : rtbLog.Font;
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

        // ── button handlers ───────────────────────────────────────────────────

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

            _cts = new CancellationTokenSource();
            _running = true;
            btnStart.Text = "⏹  Stop";
            btnStart.BackColor = ClrRed;
            btnClear.Enabled = false;
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
                btnStart.Text = "▶  Start";
                btnStart.BackColor = ClrGreen;
                btnClear.Enabled = true;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void btnClear_Click(object sender, EventArgs e) => rtbLog.Clear();

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select BitFossil root folder",
                SelectedPath = txtRoot.Text
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                txtRoot.Text = dlg.SelectedPath;
        }

        // ── core indexing logic ───────────────────────────────────────────────

        private async Task RunIndexingAsync(string rootPath, CancellationToken ct)
        {
            string[] folders = Directory.GetDirectories(rootPath);

            if (folders.Length == 0)
            {
                AppendLine("No transaction folders found in the root directory.", ClrYellow);
                SetStatus("Nothing to process.", ClrYellow);
                return;
            }

            AppendLine($"🔍  Found {folders.Length} transaction folder(s) in:", ClrAccent, bold: true);
            AppendLine($"    {rootPath}", ClrMuted);
            AppendLine(new string('─', 70), ClrMuted);
            SetStatus($"Processing 0 / {folders.Length}…");
            UpdateProgress(0, folders.Length);

            int done = 0;
            int succeeded = 0;
            int failed = 0;

            foreach (string folder in folders)
            {
                ct.ThrowIfCancellationRequested();

                string txId = Path.GetFileName(folder);

                AppendLog($"\n[{done + 1}/{folders.Length}] ", ClrMuted);
                AppendLog("TX: ", ClrMuted);
                AppendLine(txId, ClrAccent, bold: true);

                TransactionResult result;
                bool wasFallback;
                try
                {
                    (result, wasFallback) = await TransactionProcessor.ProcessAsync(txId, folder, ct);
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

                // Chain badge
                string chain = result.Target.Blockchain;
                Color chainColour = ChainColours.TryGetValue(chain, out Color c) ? c : ClrWhite;

                AppendLog("        Chain: ", ClrMuted);
                AppendLog($"[ {result.Target.Label} ]", chainColour, bold: true);
                AppendLog("  URL: ", ClrMuted);
                AppendLine(result.Target.BuildUrl(txId), Color.FromArgb(100, 170, 255));

                if (result.Success)
                {
                    succeeded++;
                    AppendLog("        ✔ ", ClrGreen, bold: true);
                    AppendLine("Response:", ClrGreen);
                    // Pretty-print: indent each response line
                    foreach (string line in result.ResponseBody
                                                 .Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
                    if (!string.IsNullOrWhiteSpace(result.ResponseBody) &&
                        result.ResponseBody != err)
                        AppendLine("          " + result.ResponseBody.Trim(), ClrMuted);
                }

                done++;
                UpdateProgress(done, folders.Length);
                SetStatus($"Processing {done} / {folders.Length}…");

                // Rate-limit: honour the "no faster than 1 call per 2 seconds" requirement.
                // Fallback paths already inserted per-call delays internally; only add the
                // standard inter-transaction delay when the last call was a direct (known)
                // API call to avoid stacking delays.
                if (done < folders.Length && !wasFallback)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            // ── Summary ──────────────────────────────────────────────────────
            AppendLine("\n" + new string('═', 70), ClrAccent);
            AppendLine($"  Done!  ✔ {succeeded} succeeded   ✘ {failed} failed   total {done}", ClrAccent, bold: true);
            AppendLine(new string('═', 70), ClrAccent);
            SetStatus($"Finished. {succeeded} OK, {failed} failed.", ClrGreen);
        }
    }
}
