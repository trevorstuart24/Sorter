// TestRunForm.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sorter
{
    public class TestRunForm : Form
    {
        private readonly MainForm _main;

        // UI
        private NumericUpDown numMaxCases;
        private CheckBox chkUnlimited;
        private Button btnStart;
        private Button btnStop;
        private Label lblStatus;
        private Label lblStats;
        private PictureBox pbLastCrop;
        private TextBox txtLog;

        // Run state
        private CancellationTokenSource _cts;
        private int _totalCases;
        private int _okCases;
        private int _rejectCases;
        private int _errorCases;

        public TestRunForm(MainForm main)
        {
            _main = main ?? throw new ArgumentNullException(nameof(main));

            Text = "Run – Headstamp Sorter";
            Width = 900;
            Height = 600;
            StartPosition = FormStartPosition.CenterParent;

            InitializeComponent();
            UpdateStatsLabel();
            UpdateStatus("Idle. Configure camera/ROI in main window, then start.");
        }

        private void InitializeComponent()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // Top controls
            var top = new Panel { Dock = DockStyle.Fill };
            root.Controls.Add(top, 0, 0);

            var lblRuns = new Label
            {
                Left = 10,
                Top = 10,
                AutoSize = true,
                Text = "Cases to run:"
            };
            top.Controls.Add(lblRuns);

            numMaxCases = new NumericUpDown
            {
                Left = 100,
                Top = 7,
                Width = 80,
                Minimum = 1,
                Maximum = 1000000,
                Value = 100
            };
            top.Controls.Add(numMaxCases);

            chkUnlimited = new CheckBox
            {
                Left = 190,
                Top = 9,
                AutoSize = true,
                Text = "Run until stopped"
            };
            top.Controls.Add(chkUnlimited);

            btnStart = new Button
            {
                Left = 10,
                Top = 40,
                Width = 100,
                Text = "Start run"
            };
            btnStart.Click += BtnStart_Click;
            top.Controls.Add(btnStart);

            btnStop = new Button
            {
                Left = 120,
                Top = 40,
                Width = 100,
                Text = "Stop",
                Enabled = false
            };
            btnStop.Click += BtnStop_Click;
            top.Controls.Add(btnStop);

            lblStatus = new Label
            {
                Left = 240,
                Top = 15,
                AutoSize = true,
                Text = "Status: Idle"
            };
            top.Controls.Add(lblStatus);

            lblStats = new Label
            {
                Left = 240,
                Top = 40,
                AutoSize = true,
                Text = "Stats: 0 total, 0 ok, 0 rejects, 0 errors"
            };
            top.Controls.Add(lblStats);

            // Main area: preview + log
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 350
            };
            root.Controls.Add(split, 0, 1);

            pbLastCrop = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle
            };
            split.Panel1.Controls.Add(pbLastCrop);

            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true
            };
            split.Panel2.Controls.Add(txtLog);

            FormClosing += TestRunForm_FormClosing;
        }

        // ========== UI event handlers ==========

        private void BtnStart_Click(object sender, EventArgs e)
        {
            if (_cts != null)
                return;

            if (!_main.IsSerialConnected)
            {
                MessageBox.Show(
                    "Serial is not connected.\nConnect in the main window first.",
                    "Run",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Reset stats
            _totalCases = 0;
            _okCases = 0;
            _rejectCases = 0;
            _errorCases = 0;
            UpdateStatsLabel();

            txtLog.Clear();

            btnStart.Enabled = false;
            btnStop.Enabled = true;
            UpdateStatus("Starting run…");

            _cts = new CancellationTokenSource();

            Task.Run(() => RunLoopAsync(_cts.Token));
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            StopRun();
        }

        private void TestRunForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopRun();
        }

        // ========== Run control ==========

        private void StopRun()
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }
        }

        private void RunFinished()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RunFinished));
                return;
            }

            btnStart.Enabled = true;
            btnStop.Enabled = false;

            _cts?.Dispose();
            _cts = null;

            UpdateStatus("Idle.");
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            try
            {
                var camera = _main.GetCameraForm();
                camera.StartCamera(); // idempotent

                int maxCases = chkUnlimitedCheckedSafe()
                    ? int.MaxValue
                    : (int)numMaxCasesValueSafe();

                SafeLog($"Run started. FeedToCaptureDelay={_main.FeedToCaptureDelayMs} ms, BetweenCyclesDelay={_main.BetweenCyclesDelayMs} ms.");

                for (int i = 0; i < maxCases; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int cycleIndex = i + 1;

                    UpdateStatus($"Running… case {cycleIndex}");
                    await RunOneCycleAsync(camera, cycleIndex, token);

                    token.ThrowIfCancellationRequested();

                    int delayMs = _main.BetweenCyclesDelayMs;
                    if (delayMs > 0)
                    {
                        try
                        {
                            SafeLog($"[{cycleIndex}] BetweenCyclesDelay {delayMs} ms…");
                            await Task.Delay(delayMs, token);
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                    }
                }

                SafeLog("Run completed.");
            }
            catch (OperationCanceledException)
            {
                SafeLog("Run cancelled.");
            }
            catch (Exception ex)
            {
                SafeLog("Run loop error: " + ex.Message);
            }
            finally
            {
                RunFinished();
            }
        }

        private async Task RunOneCycleAsync(CameraForm camera, int cycleIndex, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            int localTotal = Interlocked.Increment(ref _totalCases);
            UpdateStatsLabel();

            SafeLog($"[{cycleIndex}] Feed case (xf:0)…");
            try
            {
                _main.SendSerialCommand("xf:0");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] ERROR sending xf:0: {ex.Message}");
                return;
            }

            bool feedDone = false;
            try
            {
                SafeLog($"[{cycleIndex}] Waiting for 'done' after feed (timeout 10000 ms)…");
                feedDone = await _main.WaitForDoneAsync(10000);
                SafeLog($"[{cycleIndex}] WaitForDone after feed returned: {feedDone}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] ERROR waiting for feed done: {ex.Message}");
                return;
            }

            if (!feedDone)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] TIMEOUT or no 'done' after feed. Skipping capture and continuing.");
                return;
            }

            int captureDelay = _main.FeedToCaptureDelayMs;
            if (captureDelay > 0)
            {
                SafeLog($"[{cycleIndex}] Waiting {captureDelay} ms before capture…");
                try
                {
                    await Task.Delay(captureDelay, token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }

            Bitmap crop = null;
            try
            {
                crop = camera.CaptureAndCropForRun();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] ERROR capturing image: {ex.Message}");
                return;
            }

            if (crop == null)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] No image returned from camera.");
                return;
            }

            SafeSetPreview(crop);

            byte[] jpegBytes;
            try
            {
                using (crop)
                using (var ms = new MemoryStream())
                {
                    crop.Save(ms, ImageFormat.Jpeg);
                    jpegBytes = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] ERROR encoding JPEG: {ex.Message}");
                return;
            }

            string rawText;
            int? bin;
            double secs;
            try
            {
                SafeLog($"[{cycleIndex}] Calling LM…");
                var result = await _main.CallLmAsync(jpegBytes);
                rawText = result.text;
                bin = result.bin;
                secs = result.secs;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] ERROR calling LM: {ex.Message}");
                return;
            }

            SafeLog($"[{cycleIndex}] LM time {secs:F3} s; raw: {rawText}");

            if (!bin.HasValue || bin.Value < 0 || bin.Value > 255)
            {
                Interlocked.Increment(ref _rejectCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] No valid bin parsed. Case REJECTED (no sorter move).");
                return;
            }

            int targetBin = bin.Value;
            Interlocked.Increment(ref _okCases);
            UpdateStatsLabel();
            SafeLog($"[{cycleIndex}] Parsed bin: {targetBin}. Sending sortto:{targetBin}…");

            try
            {
                _main.SendSerialCommand("sortto:" + targetBin);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] ERROR sending sortto:{targetBin}: {ex.Message}");
                return;
            }

            bool moveDone = false;
            try
            {
                SafeLog($"[{cycleIndex}] Waiting for 'done' after sortto:{targetBin} (timeout 10000 ms)…");
                moveDone = await _main.WaitForDoneAsync(10000);
                SafeLog($"[{cycleIndex}] WaitForDone after sort returned: {moveDone}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] ERROR waiting for move done: {ex.Message}");
                return;
            }

            if (!moveDone)
            {
                Interlocked.Increment(ref _errorCases);
                UpdateStatsLabel();
                SafeLog($"[{cycleIndex}] TIMEOUT or no 'done' after sortto:{targetBin}.");
                return;
            }

            SafeLog($"[{cycleIndex}] Cycle complete.");
        }

        // ========== Safe UI helpers ==========

        private void UpdateStatus(string text)
        {
            if (lblStatus.InvokeRequired)
            {
                lblStatus.BeginInvoke(new Action<string>(UpdateStatus), text);
                return;
            }

            lblStatus.Text = "Status: " + text;
        }

        private void UpdateStatsLabel()
        {
            if (lblStats == null)
                return;

            if (lblStats.InvokeRequired)
            {
                lblStats.BeginInvoke(new Action(UpdateStatsLabel));
                return;
            }

            lblStats.Text = $"Stats: total={_totalCases}, ok={_okCases}, rejects={_rejectCases}, errors={_errorCases}";
        }

        private void SafeLog(string message)
        {
            if (txtLog == null)
                return;

            if (txtLog.InvokeRequired)
            {
                txtLog.BeginInvoke(new Action<string>(SafeLog), message);
                return;
            }

            txtLog.AppendText(message + Environment.NewLine);
        }

        private void SafeSetPreview(Bitmap bmp)
        {
            if (pbLastCrop == null)
            {
                bmp.Dispose();
                return;
            }

            if (pbLastCrop.InvokeRequired)
            {
                pbLastCrop.BeginInvoke(new Action<Bitmap>(SafeSetPreview), bmp);
                return;
            }

            var old = pbLastCrop.Image as Bitmap;
            pbLastCrop.Image = bmp;
            old?.Dispose();
        }

        private bool chkUnlimitedCheckedSafe()
        {
            if (chkUnlimited.InvokeRequired)
            {
                bool result = false;
                chkUnlimited.Invoke(new Action(() => result = chkUnlimited.Checked));
                return result;
            }
            return chkUnlimited.Checked;
        }

        private decimal numMaxCasesValueSafe()
        {
            if (numMaxCases.InvokeRequired)
            {
                decimal result = 0;
                numMaxCases.Invoke(new Action(() => result = numMaxCases.Value));
                return result;
            }
            return numMaxCases.Value;
        }
    }
}
