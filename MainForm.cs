// MainForm.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace Sorter
{
    public class MainForm : Form
    {
        // ====== UI ======
        private ComboBox cbPorts;
        private Button btnConnect;
        private Label lblSerialStatus;

        private GroupBox grpSorter;
        private Button btnHomeSorter;
        private NumericUpDown numSlot;
        private Button btnGoSlot;
        private Button btnCamera;
        private Button btnTestRun;

        private GroupBox grpLLM;
        private TextBox txtLmUrl;
        private TextBox txtModel;
        private NumericUpDown numTemp;
        private NumericUpDown numMaxTokens;
        private TextBox txtSystemPrompt;
        private CheckBox chkUseTemp;
        private CheckBox chkUseMaxTokens;

        private GroupBox grpCartridges;
        private ListBox lstCartridges;
        private Button btnCartAdd;
        private Button btnCartRemove;
        private TextBox txtCartridgeName;
        private DataGridView dgvHeadstamps;
        private Button btnHeadAdd;
        private Button btnHeadRemove;

        private GroupBox grpRight;
        private PictureBox pbRightPreview;
        private TextBox txtClassifierResult;
        private Button btnTestCapture;
        private Button btnTestClassify;
        private Button btnTestForceFeed;

        private SplitContainer _bodySplit;

        // For right-side test classifier
        private byte[] _testPreviewBytes;

        // ====== Serial ======
        private readonly SorterSerialClient _serialClient = new SorterSerialClient();

        // ====== Camera / child forms ======
        private CameraForm _cameraForm;
        private TestRunForm _testRunForm;

        // ====== Configuration / LLM ======
        private const string ConfigPath = "run_config.json";
        private const int HttpTimeoutMs = 30000;
        private RunConfig _runConfig = new RunConfig();
        private readonly QwenClient _qwenClient;

        public int FeedToCaptureDelayMs { get { return _runConfig.FeedToCaptureDelayMs; } }
        public int BetweenCyclesDelayMs { get { return _runConfig.BetweenCyclesDelayMs; } }

        public string LastCameraMoniker
        {
            get { return _runConfig.LastCameraMoniker; }
            set
            {
                if (_runConfig.LastCameraMoniker == value) return;
                _runConfig.LastCameraMoniker = value;
                SaveRunConfigSafe();
            }
        }

        public int LedPwm
        {
            get { return _runConfig.LedPwm; }
            set
            {
                if (_runConfig.LedPwm == value) return;
                _runConfig.LedPwm = value;
                SaveRunConfigSafe();
                SendSerialCommand("cameraledlevel:" + value);
            }
        }

        public bool IsSerialConnected
        {
            get { return _serialClient.IsConnected; }
        }

        public MainForm()
        {
            Text = "Headstamp Sorter (LLM-based)";
            Width = 1300;
            Height = 800;
            MinimumSize = new Size(1200, 760);
            StartPosition = FormStartPosition.CenterScreen;

            LoadRunConfigSafe();
            _qwenClient = new QwenClient(HttpTimeoutMs);

            InitializeComponent();
            WindowState = FormWindowState.Maximized;
            RefreshSerialPorts();
        }

        private void InitializeComponent()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var topBar = new Panel { Dock = DockStyle.Fill };
            root.Controls.Add(topBar, 0, 0);

            var lblPort = new Label
            {
                Text = "Port:",
                Left = 10,
                Top = 20,
                AutoSize = true
            };
            topBar.Controls.Add(lblPort);

            cbPorts = new ComboBox
            {
                Left = 50,
                Top = 15,
                Width = 140,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            topBar.Controls.Add(cbPorts);

            var btnRefreshPorts = new Button
            {
                Text = "↻",
                Left = 195,
                Top = 15,
                Width = 30
            };
            btnRefreshPorts.Click += (s, e) => RefreshSerialPorts();
            topBar.Controls.Add(btnRefreshPorts);

            btnConnect = new Button
            {
                Text = "Connect",
                Left = 235,
                Top = 15,
                Width = 90
            };
            btnConnect.Click += BtnConnect_Click;
            topBar.Controls.Add(btnConnect);

            lblSerialStatus = new Label
            {
                Text = "Disconnected",
                Left = 340,
                Top = 20,
                AutoSize = true,
                ForeColor = Color.DarkRed
            };
            topBar.Controls.Add(lblSerialStatus);

            _bodySplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 520,
                Panel1MinSize = 480
            };
            root.Controls.Add(_bodySplit, 0, 1);

            InitializeLeftPanel(_bodySplit.Panel1);
            InitializeRightPanel(_bodySplit.Panel2);

            Resize += (s, e) => AdjustSplitterForRightWidth();
        }

        private void InitializeLeftPanel(Control parent)
        {
            var leftPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            parent.Controls.Add(leftPanel);

            var topRow = new Panel
            {
                Left = 0,
                Top = 0,
                Width = leftPanel.ClientSize.Width,
                Height = 180,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            leftPanel.Controls.Add(topRow);

            grpSorter = new GroupBox
            {
                Text = "Sorter Control",
                Left = 10,
                Top = 10,
                Width = 400,
                Height = 160,
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            };
            topRow.Controls.Add(grpSorter);

            btnHomeSorter = new Button
            {
                Text = "Home Sorter",
                Left = 20,
                Top = 30,
                Width = 110
            };
            btnHomeSorter.Click += BtnHomeSorter_Click;
            grpSorter.Controls.Add(btnHomeSorter);

            var lblSlot = new Label
            {
                Text = "Slot:",
                Left = 150,
                Top = 35,
                AutoSize = true
            };
            grpSorter.Controls.Add(lblSlot);

            numSlot = new NumericUpDown
            {
                Left = 190,
                Top = 30,
                Width = 60,
                Minimum = 0,
                Maximum = 255,
                Value = 0
            };
            grpSorter.Controls.Add(numSlot);

            btnGoSlot = new Button
            {
                Text = "Move",
                Left = 265,
                Top = 30,
                Width = 70
            };
            btnGoSlot.Click += (s, e) => SendSerialCommand("sortto:" + (int)numSlot.Value);
            grpSorter.Controls.Add(btnGoSlot);

            btnCamera = new Button
            {
                Text = "Camera / ROI",
                Left = 20,
                Top = 70,
                Width = 110
            };
            btnCamera.Click += BtnCamera_Click;
            grpSorter.Controls.Add(btnCamera);

            btnTestRun = new Button
            {
                Text = "Test Run…",
                Left = 150,
                Top = 70,
                Width = 110
            };
            btnTestRun.Click += BtnTestRun_Click;
            grpSorter.Controls.Add(btnTestRun);

            grpLLM = new GroupBox
            {
                Text = "LLM / Classification",
                Left = 10,
                Top = 190,
                Width = leftPanel.ClientSize.Width - 40,
                Height = 260,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            leftPanel.Controls.Add(grpLLM);

            var lblUrl = new Label
            {
                Text = "LM Studio URL:",
                Left = 15,
                Top = 30,
                AutoSize = true
            };
            grpLLM.Controls.Add(lblUrl);

            txtLmUrl = new TextBox
            {
                Left = 130,
                Top = 25,
                Width = grpLLM.Width - 150,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = _runConfig.LmUrl
            };
            txtLmUrl.TextChanged += (s, e) =>
            {
                _runConfig.LmUrl = txtLmUrl.Text.Trim();
                SaveRunConfigSafe();
            };
            grpLLM.Controls.Add(txtLmUrl);

            var lblModel = new Label
            {
                Text = "Model:",
                Left = 15,
                Top = 60,
                AutoSize = true
            };
            grpLLM.Controls.Add(lblModel);

            txtModel = new TextBox
            {
                Left = 130,
                Top = 55,
                Width = grpLLM.Width - 150,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = _runConfig.Model
            };
            txtModel.TextChanged += (s, e) =>
            {
                _runConfig.Model = txtModel.Text.Trim();
                SaveRunConfigSafe();
            };
            grpLLM.Controls.Add(txtModel);

            var lblTemp = new Label
            {
                Text = "Temperature:",
                Left = 15,
                Top = 90,
                AutoSize = true
            };
            grpLLM.Controls.Add(lblTemp);

            numTemp = new NumericUpDown
            {
                Left = 130,
                Top = 85,
                Width = 80,
                Minimum = 0,
                Maximum = 200,
                DecimalPlaces = 2,
                Increment = 0.05M,
                Value = (decimal)_runConfig.Temperature
            };
            grpLLM.Controls.Add(numTemp);

            chkUseTemp = new CheckBox
            {
                Text = "Use",
                Left = 220,
                Top = 87,
                Width = 60
            };
            chkUseTemp.Checked = _runConfig.UseTemperature;
            chkUseTemp.CheckedChanged += (s, e) =>
            {
                _runConfig.UseTemperature = chkUseTemp.Checked;
                numTemp.Enabled = chkUseTemp.Checked;
                SaveRunConfigSafe();
            };
            grpLLM.Controls.Add(chkUseTemp);
            numTemp.Enabled = chkUseTemp.Checked;

            var lblMaxT = new Label
            {
                Text = "Max tokens:",
                Left = 15,
                Top = 120,
                AutoSize = true
            };
            grpLLM.Controls.Add(lblMaxT);

            numMaxTokens = new NumericUpDown
            {
                Left = 130,
                Top = 115,
                Width = 80,
                Minimum = 16,
                Maximum = 8192,
                Increment = 64,
                Value = _runConfig.MaxOutputTokens
            };
            grpLLM.Controls.Add(numMaxTokens);

            chkUseMaxTokens = new CheckBox
            {
                Text = "Use",
                Left = 220,
                Top = 117,
                Width = 60
            };
            chkUseMaxTokens.Checked = _runConfig.UseMaxTokens;
            chkUseMaxTokens.CheckedChanged += (s, e) =>
            {
                _runConfig.UseMaxTokens = chkUseMaxTokens.Checked;
                numMaxTokens.Enabled = chkUseMaxTokens.Checked;
                SaveRunConfigSafe();
            };
            grpLLM.Controls.Add(chkUseMaxTokens);
            numMaxTokens.Enabled = chkUseMaxTokens.Checked;

            var lblSys = new Label
            {
                Text = "System prompt (optional):",
                Left = 15,
                Top = 150,
                AutoSize = true
            };
            grpLLM.Controls.Add(lblSys);

            txtSystemPrompt = new TextBox
            {
                Left = 15,
                Top = 170,
                Width = grpLLM.Width - 30,
                Height = 70,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = _runConfig.LmSystemPrompt ?? string.Empty
            };
            txtSystemPrompt.TextChanged += (s, e) =>
            {
                _runConfig.LmSystemPrompt = txtSystemPrompt.Text;
                SaveRunConfigSafe();
            };
            grpLLM.Controls.Add(txtSystemPrompt);

            grpLLM.Resize += (s, e) =>
            {
                txtLmUrl.Width = grpLLM.Width - 150;
                txtModel.Width = grpLLM.Width - 150;
                txtSystemPrompt.Width = grpLLM.Width - 30;
            };

            grpCartridges = new GroupBox
            {
                Text = "Cartridges / Headstamps",
                Left = 10,
                Top = grpLLM.Bottom + 10,
                Width = leftPanel.ClientSize.Width - 40,
                Height = 260,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            leftPanel.Controls.Add(grpCartridges);

            var lblCartList = new Label
            {
                Text = "Cartridges:",
                Left = 15,
                Top = 25,
                AutoSize = true
            };
            grpCartridges.Controls.Add(lblCartList);

            lstCartridges = new ListBox
            {
                Left = 15,
                Top = 45,
                Width = 160,
                Height = 160,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom
            };
            lstCartridges.SelectedIndexChanged += LstCartridges_SelectedIndexChanged;
            grpCartridges.Controls.Add(lstCartridges);

            btnCartAdd = new Button
            {
                Text = "Add",
                Left = 15,
                Top = 210,
                Width = 75,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            btnCartAdd.Click += BtnCartAdd_Click;
            grpCartridges.Controls.Add(btnCartAdd);

            btnCartRemove = new Button
            {
                Text = "Remove",
                Left = 100,
                Top = 210,
                Width = 75,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            btnCartRemove.Click += BtnCartRemove_Click;
            grpCartridges.Controls.Add(btnCartRemove);

            var lblSelectedCart = new Label
            {
                Text = "Selected cartridge name:",
                Left = 195,
                Top = 25,
                AutoSize = true
            };
            grpCartridges.Controls.Add(lblSelectedCart);

            txtCartridgeName = new TextBox
            {
                Left = 195,
                Top = 45,
                Width = grpCartridges.Width - 210,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            txtCartridgeName.Leave += TxtCartridgeName_Leave;
            grpCartridges.Controls.Add(txtCartridgeName);

            dgvHeadstamps = new DataGridView
            {
                Left = 195,
                Top = 75,
                Width = grpCartridges.Width - 210,
                Height = 130,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            var colBin = new DataGridViewTextBoxColumn
            {
                HeaderText = "Bin",
                Width = 60,
                FillWeight = 30
            };
            var colLabel = new DataGridViewTextBoxColumn
            {
                HeaderText = "Headstamp",
                FillWeight = 70
            };
            dgvHeadstamps.Columns.Add(colBin);
            dgvHeadstamps.Columns.Add(colLabel);
            dgvHeadstamps.CellEndEdit += DgvHeadstamps_CellEndEdit;
            grpCartridges.Controls.Add(dgvHeadstamps);

            btnHeadAdd = new Button
            {
                Text = "Add headstamp",
                Left = 195,
                Top = 210,
                Width = 120,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            btnHeadAdd.Click += BtnHeadAdd_Click;
            grpCartridges.Controls.Add(btnHeadAdd);

            btnHeadRemove = new Button
            {
                Text = "Remove selected",
                Left = 325,
                Top = 210,
                Width = 130,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            btnHeadRemove.Click += BtnHeadRemove_Click;
            grpCartridges.Controls.Add(btnHeadRemove);

            grpCartridges.Resize += (s, e) =>
            {
                txtCartridgeName.Width = grpCartridges.Width - 210;
                dgvHeadstamps.Width = grpCartridges.Width - 210;
                dgvHeadstamps.Height = grpCartridges.Height - 120;
                btnCartAdd.Top = grpCartridges.Height - 40;
                btnCartRemove.Top = grpCartridges.Height - 40;
                btnHeadAdd.Top = grpCartridges.Height - 40;
                btnHeadRemove.Top = grpCartridges.Height - 40;
            };

            NormalizeRunConfigCollections();
            ReloadCartridgeUiFromConfig();
        }

        private void InitializeRightPanel(Control parent)
        {
            grpRight = new GroupBox
            {
                Text = "Test Classifier (single image)",
                Dock = DockStyle.Fill
            };
            parent.Controls.Add(grpRight);

            pbRightPreview = new PictureBox
            {
                Left = 10,
                Top = 20,
                Width = grpRight.ClientSize.Width - 20,
                Height = 260,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            grpRight.Controls.Add(pbRightPreview);

            btnTestCapture = new Button
            {
                Text = "Capture from camera",
                Left = 10,
                Top = pbRightPreview.Bottom + 5,
                Width = 160
            };
            btnTestCapture.Click += async (s, e) => await CaptureTestImageAsync();
            grpRight.Controls.Add(btnTestCapture);

            btnTestClassify = new Button
            {
                Text = "Classify",
                Left = btnTestCapture.Right + 10,
                Top = pbRightPreview.Bottom + 5,
                Width = 100
            };
            btnTestClassify.Click += async (s, e) => await ClassifyTestImageAsync();
            grpRight.Controls.Add(btnTestClassify);

            btnTestForceFeed = new Button
            {
                Text = "Force feed",
                Left = btnTestClassify.Right + 10,
                Top = pbRightPreview.Bottom + 5,
                Width = 100
            };
            btnTestForceFeed.Click += (s, e) => SendSerialCommand("xf:0");
            grpRight.Controls.Add(btnTestForceFeed);

            txtClassifierResult = new TextBox
            {
                Left = 10,
                Top = btnTestCapture.Bottom + 10,
                Width = grpRight.ClientSize.Width - 20,
                Height = grpRight.ClientSize.Height - (btnTestCapture.Bottom + 20),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            grpRight.Controls.Add(txtClassifierResult);

            grpRight.Resize += (s, e) =>
            {
                pbRightPreview.Width = grpRight.ClientSize.Width - 20;

                btnTestCapture.Top = pbRightPreview.Bottom + 5;
                btnTestClassify.Top = pbRightPreview.Bottom + 5;
                btnTestClassify.Left = btnTestCapture.Right + 10;

                btnTestForceFeed.Top = pbRightPreview.Bottom + 5;
                btnTestForceFeed.Left = btnTestClassify.Right + 10;

                txtClassifierResult.Top = btnTestCapture.Bottom + 10;
                txtClassifierResult.Width = grpRight.ClientSize.Width - 20;
                txtClassifierResult.Height = grpRight.ClientSize.Height - txtClassifierResult.Top - 10;
            };
        }

        private void AdjustSplitterForRightWidth()
        {
            if (_bodySplit == null) return;
            const int minRight = 420;
            int total = _bodySplit.Width;
            if (total <= 0) return;

            if (total - _bodySplit.SplitterDistance < minRight)
            {
                int newDist = Math.Max(300, total - minRight);
                if (newDist < 0) newDist = 0;
                if (newDist > total) newDist = total;
                if (newDist != _bodySplit.SplitterDistance)
                    _bodySplit.SplitterDistance = newDist;
            }
        }

        // ====== Serial ======

        private void RefreshSerialPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            cbPorts.Items.Clear();
            cbPorts.Items.AddRange(ports);

            if (ports.Length > 0)
            {
                cbPorts.SelectedIndex = 0;
            }
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            if (_serialClient.IsConnected)
            {
                CloseSerial();
                return;
            }

            if (cbPorts.SelectedItem == null)
            {
                MessageBox.Show("Select a COM port first.", "Serial", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var portName = cbPorts.SelectedItem.ToString();
            try
            {
                _serialClient.Connect(portName);

                lblSerialStatus.Text = "Connected: " + portName;
                lblSerialStatus.ForeColor = Color.DarkGreen;
                btnConnect.Text = "Disconnect";

                SendSerialCommand("ping");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open serial port: " + ex.Message, "Serial",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                CloseSerial();
            }
        }

        private void CloseSerial()
        {
            try
            {
                _serialClient.Disconnect();
            }
            catch
            {
            }

            lblSerialStatus.Text = "Disconnected";
            lblSerialStatus.ForeColor = Color.DarkRed;
            btnConnect.Text = "Connect";
        }

        public void SendSerialCommand(string cmd)
        {
            _serialClient.Send(cmd);
        }

        public void SendCommandFromCamera(string cmd)
        {
            SendSerialCommand(cmd);
        }

        public Task<bool> WaitForDoneAsync(int timeoutMs)
        {
            return _serialClient.WaitForDoneAsync(timeoutMs);
        }

        // ====== Sorter / camera buttons ======

        private void BtnHomeSorter_Click(object sender, EventArgs e)
        {
            if (!IsSerialConnected)
            {
                MessageBox.Show("Serial not connected.", "Sorter",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SendSerialCommand("homesorter");
        }

        private void BtnCamera_Click(object sender, EventArgs e)
        {
            var cam = GetCameraForm();
            if (!cam.Visible)
                cam.Show(this);
            else
                cam.Focus();
        }

        private void BtnTestRun_Click(object sender, EventArgs e)
        {
            if (_testRunForm == null || _testRunForm.IsDisposed)
            {
                _testRunForm = new TestRunForm(this);
                _testRunForm.Show(this);
            }
            else
            {
                _testRunForm.Focus();
            }
        }

        // ====== Test capture / classify ======

        private async Task CaptureTestImageAsync()
        {
            try
            {
                var cam = GetCameraForm();
                cam.StartCamera();

                int startSeq = cam.GetFrameSeq();
                while (cam.GetFrameSeq() - startSeq < 3)
                    await Task.Delay(10);

                const int ThumbSize = 300;
                Bitmap bmp = cam.CaptureThumbFromRoi(ThumbSize);
                if (bmp == null)
                {
                    MessageBox.Show("No image available from camera / ROI.", "Test Capture",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Image old = pbRightPreview.Image;
                pbRightPreview.Image = (Bitmap)bmp.Clone();
                if (old != null) old.Dispose();

                using (bmp)
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                    _testPreviewBytes = ms.ToArray();
                }

                txtClassifierResult.Text = "Captured image from camera. Click \"Classify\" to send it to the model.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error capturing image: " + ex.Message, "Test Capture",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ClassifyTestImageAsync()
        {
            if (_testPreviewBytes == null || _testPreviewBytes.Length == 0)
            {
                MessageBox.Show("Capture an image first.", "Test Classifier",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                txtClassifierResult.Text = "Classifying…";

                var result = await CallLmAsync(_testPreviewBytes);
                string rawText = result.text;
                int? bin = result.bin;
                double secs = result.secs;

                var sb = new StringBuilder();
                sb.AppendLine("Raw model response:");
                sb.AppendLine(rawText ?? string.Empty);
                sb.AppendLine();
                sb.AppendLine("Parsed bin: " + (bin.HasValue ? bin.Value.ToString() : "(none)"));
                sb.AppendLine("Time: " + secs.ToString("F3") + " s");

                txtClassifierResult.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                txtClassifierResult.Text = "Error: " + ex.Message;
            }
        }

        // ====== LLM call ======

        public async Task<(string text, int? bin, double secs)> CallLmAsync(byte[] jpegBytes)
        {
            if (jpegBytes == null || jpegBytes.Length == 0)
                throw new ArgumentException("Empty image");

            var (text, secs) = await _qwenClient.ClassifyAsync(jpegBytes, _runConfig);

            int? binFromDigits = CartridgeMapper.ParseBinFromText(text);
            int? binFromMap = CartridgeMapper.MapUsingCartridges(text, _runConfig);
            int? finalBin = binFromDigits ?? binFromMap;

            return (text, finalBin, secs);
        }

        public CameraForm GetCameraForm()
        {
            if (_cameraForm == null || _cameraForm.IsDisposed)
            {
                _cameraForm = new CameraForm(this);
            }
            return _cameraForm;
        }

        // ====== Cartridge helpers ======

        private void NormalizeRunConfigCollections()
        {
            if (_runConfig.Cartridges == null)
                _runConfig.Cartridges = new List<CartridgeConfig>();

            foreach (var c in _runConfig.Cartridges)
            {
                if (c.Headstamps == null)
                    c.Headstamps = new List<HeadstampConfig>();
            }

            if (_runConfig.SelectedCartridgeIndex < -1 ||
                _runConfig.SelectedCartridgeIndex >= _runConfig.Cartridges.Count)
            {
                _runConfig.SelectedCartridgeIndex = _runConfig.Cartridges.Count > 0 ? 0 : -1;
            }
        }

        private void ReloadCartridgeUiFromConfig()
        {
            lstCartridges.Items.Clear();

            if (_runConfig.Cartridges != null)
            {
                foreach (var c in _runConfig.Cartridges)
                {
                    lstCartridges.Items.Add(c.Name ?? string.Empty);
                }
            }

            if (_runConfig.Cartridges != null &&
                _runConfig.Cartridges.Count > 0 &&
                _runConfig.SelectedCartridgeIndex >= 0 &&
                _runConfig.SelectedCartridgeIndex < _runConfig.Cartridges.Count)
            {
                lstCartridges.SelectedIndex = _runConfig.SelectedCartridgeIndex;
            }
            else if (lstCartridges.Items.Count > 0)
            {
                lstCartridges.SelectedIndex = 0;
            }
            else
            {
                lstCartridges.SelectedIndex = -1;
                LoadSelectedCartridgeIntoUi(-1);
            }
        }

        private void LoadSelectedCartridgeIntoUi(int index)
        {
            if (_runConfig.Cartridges == null || index < 0 || index >= _runConfig.Cartridges.Count)
            {
                txtCartridgeName.Text = string.Empty;
                txtCartridgeName.Enabled = false;
                dgvHeadstamps.Rows.Clear();
                dgvHeadstamps.Enabled = false;
                btnHeadAdd.Enabled = false;
                btnHeadRemove.Enabled = false;
                return;
            }

            var cart = _runConfig.Cartridges[index];

            txtCartridgeName.Enabled = true;
            dgvHeadstamps.Enabled = true;
            btnHeadAdd.Enabled = true;
            btnHeadRemove.Enabled = true;

            txtCartridgeName.Text = cart.Name ?? string.Empty;

            dgvHeadstamps.Rows.Clear();
            if (cart.Headstamps != null)
            {
                foreach (var hs in cart.Headstamps)
                {
                    int rowIndex = dgvHeadstamps.Rows.Add(hs.Bin, hs.Label ?? string.Empty);
                    dgvHeadstamps.Rows[rowIndex].Tag = hs;
                }
            }
        }

        private void LstCartridges_SelectedIndexChanged(object sender, EventArgs e)
        {
            int index = lstCartridges.SelectedIndex;
            _runConfig.SelectedCartridgeIndex = index;
            LoadSelectedCartridgeIntoUi(index);
            SaveRunConfigSafe();
        }

        private void BtnCartAdd_Click(object sender, EventArgs e)
        {
            if (_runConfig.Cartridges == null)
                _runConfig.Cartridges = new List<CartridgeConfig>();

            string baseName = "New cartridge";
            string name = baseName;
            int counter = 1;
            while (_runConfig.Cartridges.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                counter++;
                name = baseName + " " + counter;
            }

            var cart = new CartridgeConfig { Name = name, Headstamps = new List<HeadstampConfig>() };
            _runConfig.Cartridges.Add(cart);
            SaveRunConfigSafe();

            ReloadCartridgeUiFromConfig();
            if (lstCartridges.Items.Count > 0)
            {
                lstCartridges.SelectedIndex = lstCartridges.Items.Count - 1;
            }
        }

        private void BtnCartRemove_Click(object sender, EventArgs e)
        {
            if (_runConfig.Cartridges == null || _runConfig.Cartridges.Count == 0)
                return;

            int index = lstCartridges.SelectedIndex;
            if (index < 0 || index >= _runConfig.Cartridges.Count)
                return;

            _runConfig.Cartridges.RemoveAt(index);
            SaveRunConfigSafe();

            ReloadCartridgeUiFromConfig();
        }

        private void TxtCartridgeName_Leave(object sender, EventArgs e)
        {
            if (_runConfig.Cartridges == null) return;
            int index = lstCartridges.SelectedIndex;
            if (index < 0 || index >= _runConfig.Cartridges.Count) return;

            string newName = txtCartridgeName.Text ?? string.Empty;
            _runConfig.Cartridges[index].Name = newName;

            if (index < lstCartridges.Items.Count)
            {
                lstCartridges.Items[index] = newName;
            }

            SaveRunConfigSafe();
        }

        private CartridgeConfig GetSelectedCartridge()
        {
            if (_runConfig.Cartridges == null) return null;
            int index = lstCartridges.SelectedIndex;
            if (index < 0 || index >= _runConfig.Cartridges.Count) return null;
            return _runConfig.Cartridges[index];
        }

        private void BtnHeadAdd_Click(object sender, EventArgs e)
        {
            var cart = GetSelectedCartridge();
            if (cart == null)
                return;

            if (cart.Headstamps == null)
                cart.Headstamps = new List<HeadstampConfig>();

            var hs = new HeadstampConfig { Bin = 0, Label = string.Empty };
            cart.Headstamps.Add(hs);

            int rowIndex = dgvHeadstamps.Rows.Add(hs.Bin, hs.Label);
            dgvHeadstamps.Rows[rowIndex].Tag = hs;

            SaveRunConfigSafe();
        }

        private void BtnHeadRemove_Click(object sender, EventArgs e)
        {
            var cart = GetSelectedCartridge();
            if (cart == null || cart.Headstamps == null)
                return;

            foreach (DataGridViewRow row in dgvHeadstamps.SelectedRows)
            {
                var hs = row.Tag as HeadstampConfig;
                if (hs != null)
                {
                    cart.Headstamps.Remove(hs);
                }
                if (!row.IsNewRow)
                {
                    dgvHeadstamps.Rows.Remove(row);
                }
            }

            SaveRunConfigSafe();
        }

        private void DgvHeadstamps_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            var cart = GetSelectedCartridge();
            if (cart == null || cart.Headstamps == null) return;
            if (e.RowIndex < 0 || e.RowIndex >= dgvHeadstamps.Rows.Count) return;

            DataGridViewRow row = dgvHeadstamps.Rows[e.RowIndex];
            var hs = row.Tag as HeadstampConfig;
            if (hs == null) return;

            object binObj = row.Cells[0].Value;
            int bin;
            if (binObj == null || !int.TryParse(Convert.ToString(binObj), out bin))
            {
                bin = hs.Bin;
            }

            if (bin < 0) bin = 0;
            if (bin > 255) bin = 255;

            hs.Bin = bin;
            row.Cells[0].Value = bin;

            object labelObj = row.Cells[1].Value;
            hs.Label = labelObj == null ? string.Empty : Convert.ToString(labelObj);

            SaveRunConfigSafe();
        }

        // ====== Config ======

        private void LoadRunConfigSafe()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    _runConfig = new RunConfig();
                }
                else
                {
                    string json = File.ReadAllText(ConfigPath);
                    _runConfig = JsonConvert.DeserializeObject<RunConfig>(json) ?? new RunConfig();
                }
            }
            catch
            {
                _runConfig = new RunConfig();
            }

            NormalizeRunConfigCollections();
        }

        private void SaveRunConfigSafe()
        {
            try
            {
                NormalizeRunConfigCollections();
                string json = JsonConvert.SerializeObject(_runConfig, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            try
            {
                if (_cameraForm != null && !_cameraForm.IsDisposed)
                {
                    _cameraForm.ForceStopCameraAndClose();
                }
            }
            catch
            {
            }

            CloseSerial();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            try
            {
                _serialClient.Dispose();
            }
            catch
            {
            }

            try
            {
                _qwenClient.Dispose();
            }
            catch
            {
            }
        }
    }
}
