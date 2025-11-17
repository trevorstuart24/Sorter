using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using AForge.Video;
using AForge.Video.DirectShow;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using DrawingPoint = System.Drawing.Point;

namespace Sorter
{
    public class CameraForm : Form
    {
        private readonly MainForm _main;

        private FilterInfoCollection _videoDevices;
        private VideoCaptureDevice _videoSource;

        private ComboBox cbCameras;
        private Button btnStart;
        private Button btnStop;
        private Button btnSettings;
        private Button btnCapture;
        private Button btnFeedForce;
        private TrackBar tbLed;
        private Label lblLedValue;
        private Label lblStatus;
        private PictureBox pbImage;
        private PictureBox pbCropPreview;

        private TrackBar tbPrimerMask;
        private Label lblPrimerMask;
        private TrackBar tbBgClip;
        private Label lblBgClip;

        private TrackBar tbOuterMask;
        private Label lblOuterMask;

        private readonly object _frameLock = new object();
        private Bitmap _lastFrame;
        private int _frameWidth;
        private int _frameHeight;
        private int _frameSeq;

        private Rectangle _roiImageRect;
        private bool _roiInitialized;

        private float _scaleX = 1f;
        private float _scaleY = 1f;

        private bool _dragging;
        private bool _resizing;
        private DrawingPoint _dragStartImagePoint;
        private Rectangle _roiStartRect;

        private const int ResizeHandleSize = 16;
        private const int TargetCropSize = 300;

        // central primer mask in final cropped image
        private int _primerMaskDiameter = 80;
        // background clip by brightness (0 = off)
        private int _bgClipThreshold = 20;

        // outer circular mask scale (% of detected case radius)
        private int _outerMaskPercent = 115;

        // last detected case circle in the final cropped image
        private bool _hasCaseCircleDest;
        private double _caseCenterXDest;
        private double _caseCenterYDest;
        private double _caseRadiusDest;

        private const string RoiConfigFileName = "camera_roi.txt";
        private const string PrimerMaskFileName = "primer_mask.txt";
        private const string BgClipFileName = "bg_clip.txt";
        private const string OuterMaskFileName = "outer_mask_scale.txt";

        public CameraForm(MainForm main)
        {
            _main = main ?? throw new ArgumentNullException(nameof(main));

            Text = "Camera / Feed";
            Width = 1000;
            Height = 700;
            StartPosition = FormStartPosition.CenterScreen;

            InitializeComponent();
            LoadCameras();
            LoadRoiFromDisk();
            LoadPrimerMaskFromDisk();
            LoadBgClipFromDisk();
            LoadOuterMaskFromDisk();

            tbPrimerMask.Value = Math.Max(tbPrimerMask.Minimum, Math.Min(tbPrimerMask.Maximum, _primerMaskDiameter));
            lblPrimerMask.Text = "Primer mask: " + _primerMaskDiameter + " px";

            tbBgClip.Value = Math.Max(tbBgClip.Minimum, Math.Min(tbBgClip.Maximum, _bgClipThreshold));
            lblBgClip.Text = "BG clip: " + _bgClipThreshold;

            tbOuterMask.Value = Math.Max(tbOuterMask.Minimum, Math.Min(tbOuterMask.Maximum, _outerMaskPercent));
            lblOuterMask.Text = "Outer mask: " + _outerMaskPercent + "%";
        }

        // ------------------ public API used elsewhere ------------------

        public int GetFrameSeq()
        {
            lock (_frameLock)
            {
                return _frameSeq;
            }
        }

        public Bitmap CaptureAndCropForRun()
        {
            lock (_frameLock)
            {
                if (_lastFrame == null || _frameWidth <= 0 || _frameHeight <= 0)
                    return null;

                if (!_roiInitialized)
                    InitDefaultRoi();

                Rectangle srcRect = ClampRoiToImage(_roiImageRect);
                if (srcRect.Width <= 0 || srcRect.Height <= 0)
                    return null;

                // Copy ROI out of latest frame
                Bitmap roiBmp = new Bitmap(srcRect.Width, srcRect.Height);
                using (Graphics gRoi = Graphics.FromImage(roiBmp))
                {
                    gRoi.SmoothingMode = SmoothingMode.HighSpeed;
                    gRoi.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gRoi.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    gRoi.DrawImage(
                        _lastFrame,
                        new Rectangle(0, 0, srcRect.Width, srcRect.Height),
                        srcRect,
                        GraphicsUnit.Pixel);
                }

                float cx;
                float cy;
                float radius;
                bool foundCircle = TryDetectCaseCircle(roiBmp, out cx, out cy, out radius);

                Rectangle cropRect;

                if (foundCircle)
                {
                    // crop around detected head, then scale to 300x300
                    int side = (int)(radius * 2.2f);
                    if (side < 50) side = 50;
                    int maxSide = Math.Min(roiBmp.Width, roiBmp.Height);
                    if (side > maxSide) side = maxSide;

                    int x = (int)(cx - side / 2f);
                    int y = (int)(cy - side / 2f);

                    if (x < 0) x = 0;
                    if (y < 0) y = 0;
                    if (x + side > roiBmp.Width) x = roiBmp.Width - side;
                    if (y + side > roiBmp.Height) y = roiBmp.Height - side;

                    cropRect = new Rectangle(x, y, side, side);
                }
                else
                {
                    // fallback: center square inside ROI
                    int side = Math.Min(roiBmp.Width, roiBmp.Height);
                    int x = (roiBmp.Width - side) / 2;
                    int y = (roiBmp.Height - side) / 2;
                    cropRect = new Rectangle(x, y, side, side);
                }

                // map detected circle (if any) into the final 300x300 image
                UpdateCaseCircleDest(foundCircle, cx, cy, radius, cropRect, TargetCropSize);

                Bitmap dest = new Bitmap(TargetCropSize, TargetCropSize);
                using (Graphics g = Graphics.FromImage(dest))
                {
                    g.SmoothingMode = SmoothingMode.HighSpeed;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Black);

                    g.DrawImage(
                        roiBmp,
                        new Rectangle(0, 0, TargetCropSize, TargetCropSize),
                        cropRect,
                        GraphicsUnit.Pixel);
                }

                roiBmp.Dispose();

                ApplyBackgroundClip(dest);
                ApplyOuterCircularMask(dest);   // only if we have a valid detected center
                ApplyPrimerMask(dest);          // only if we have a valid detected center

                return dest;
            }
        }

        public Bitmap CaptureThumbFromCurrentRoi(int size)
        {
            lock (_frameLock)
            {
                if (_lastFrame == null || _frameWidth <= 0 || _frameHeight <= 0)
                    return null;

                if (!_roiInitialized)
                    InitDefaultRoi();

                if (size <= 0) size = TargetCropSize;

                Rectangle srcRect = ClampRoiToImage(_roiImageRect);
                if (srcRect.Width <= 0 || srcRect.Height <= 0)
                    return null;

                Bitmap roiBmp = new Bitmap(srcRect.Width, srcRect.Height);
                using (Graphics gRoi = Graphics.FromImage(roiBmp))
                {
                    gRoi.SmoothingMode = SmoothingMode.HighSpeed;
                    gRoi.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gRoi.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    gRoi.DrawImage(
                        _lastFrame,
                        new Rectangle(0, 0, srcRect.Width, srcRect.Height),
                        srcRect,
                        GraphicsUnit.Pixel);
                }

                float cx;
                float cy;
                float radius;
                bool foundCircle = TryDetectCaseCircle(roiBmp, out cx, out cy, out radius);

                Rectangle cropRect;

                if (foundCircle)
                {
                    int side = (int)(radius * 2.2f);
                    if (side < 50) side = 50;
                    int maxSide = Math.Min(roiBmp.Width, roiBmp.Height);
                    if (side > maxSide) side = maxSide;

                    int x = (int)(cx - side / 2f);
                    int y = (int)(cy - side / 2f);

                    if (x < 0) x = 0;
                    if (y < 0) y = 0;
                    if (x + side > roiBmp.Width) x = roiBmp.Width - side;
                    if (y + side > roiBmp.Height) y = roiBmp.Height - side;

                    cropRect = new Rectangle(x, y, side, side);
                }
                else
                {
                    int side = Math.Min(roiBmp.Width, roiBmp.Height);
                    int x = (roiBmp.Width - side) / 2;
                    int y = (roiBmp.Height - side) / 2;
                    cropRect = new Rectangle(x, y, side, side);
                }

                if (size > cropRect.Width) size = cropRect.Width;
                if (size > cropRect.Height) size = cropRect.Height;

                // map detected circle (if any) into the final thumb image
                UpdateCaseCircleDest(foundCircle, cx, cy, radius, cropRect, size);

                Bitmap dest = new Bitmap(size, size);
                using (Graphics g = Graphics.FromImage(dest))
                {
                    g.SmoothingMode = SmoothingMode.HighSpeed;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Black);

                    g.DrawImage(
                        roiBmp,
                        new Rectangle(0, 0, size, size),
                        cropRect,
                        GraphicsUnit.Pixel);
                }

                roiBmp.Dispose();

                ApplyBackgroundClip(dest);
                ApplyOuterCircularMask(dest);   // only if we have a valid detected center
                ApplyPrimerMask(dest);          // only if we have a valid detected center

                return dest;
            }
        }

        public Bitmap CaptureThumbFromRoi(int size)
        {
            return CaptureThumbFromCurrentRoi(size);
        }

        public static int ComputeThumbDiff(Bitmap a, Bitmap b)
        {
            if (a == null || b == null) return int.MaxValue;
            if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;

            int diff = 0;
            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    Color c1 = a.GetPixel(x, y);
                    Color c2 = b.GetPixel(x, y);
                    int dr = c1.R - c2.R;
                    int dg = c1.G - c2.G;
                    int db = c1.B - c2.B;
                    diff += Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db);
                }
            }
            return diff;
        }

        public void StartCamera()
        {
            try
            {
                if (_videoSource != null && _videoSource.IsRunning)
                    return;

                if (_videoDevices == null || _videoDevices.Count == 0)
                {
                    MessageBox.Show("No video devices found.", "Camera",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (cbCameras.SelectedIndex < 0)
                    cbCameras.SelectedIndex = 0;

                FilterInfo devInfo = _videoDevices[cbCameras.SelectedIndex];
                _main.LastCameraMoniker = devInfo.MonikerString;

                VideoCaptureDevice dev = new VideoCaptureDevice(devInfo.MonikerString);

                string resInfo = string.Empty;
                try
                {
                    VideoCapabilities[] caps = dev.VideoCapabilities;
                    if (caps != null && caps.Length > 0)
                    {
                        VideoCapabilities best = caps[0];
                        int bestArea = best.FrameSize.Width * best.FrameSize.Height;

                        for (int i = 1; i < caps.Length; i++)
                        {
                            VideoCapabilities c = caps[i];
                            int area = c.FrameSize.Width * c.FrameSize.Height;
                            if (area > bestArea)
                            {
                                best = c;
                                bestArea = area;
                            }
                        }

                        dev.VideoResolution = best;
                        resInfo = " (" + best.FrameSize.Width + "x" + best.FrameSize.Height + ")";
                    }
                }
                catch
                {
                }

                _videoSource = dev;
                _videoSource.NewFrame += VideoSource_NewFrame;
                _videoSource.Start();

                lblStatus.Text = "Status: Camera running" + resInfo + ".";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start camera: " + ex.Message,
                    "Camera", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void StopCamera()
        {
            try
            {
                if (_videoSource != null)
                {
                    _videoSource.NewFrame -= VideoSource_NewFrame;

                    if (_videoSource.IsRunning)
                    {
                        _videoSource.SignalToStop();
                        _videoSource.WaitForStop();
                    }

                    _videoSource = null;
                }
            }
            catch
            {
            }

            lock (_frameLock)
            {
                if (_lastFrame != null)
                {
                    _lastFrame.Dispose();
                    _lastFrame = null;
                }
                _frameWidth = 0;
                _frameHeight = 0;
                _frameSeq = 0;
            }

            lblStatus.Text = "Status: Camera stopped.";
            pbImage.Invalidate();
        }

        public void ForceStopCameraAndClose()
        {
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(ForceStopCameraAndClose));
                    return;
                }

                StopCamera();

                if (!IsDisposed)
                    Close();
            }
            catch
            {
            }
        }

        // ------------------ UI wiring ------------------

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

            var top = new Panel { Dock = DockStyle.Fill };
            root.Controls.Add(top, 0, 0);

            var center = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 700
            };
            root.Controls.Add(center, 0, 1);

            cbCameras = new ComboBox
            {
                Left = 10,
                Top = 8,
                Width = 230,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            top.Controls.Add(cbCameras);

            btnStart = new Button { Text = "Start", Left = 250, Top = 7, Width = 60 };
            btnStart.Click += BtnStart_Click;
            top.Controls.Add(btnStart);

            btnStop = new Button { Text = "Stop", Left = 315, Top = 7, Width = 60 };
            btnStop.Click += BtnStop_Click;
            top.Controls.Add(btnStop);

            btnSettings = new Button { Text = "Settings…", Left = 380, Top = 7, Width = 80 };
            btnSettings.Click += BtnSettings_Click;
            top.Controls.Add(btnSettings);

            btnCapture = new Button { Text = "Capture → ROI", Left = 470, Top = 7, Width = 110 };
            btnCapture.Click += BtnCapture_Click;
            top.Controls.Add(btnCapture);

            btnFeedForce = new Button { Text = "Force Feed (xf:0)", Left = 585, Top = 7, Width = 130 };
            btnFeedForce.Click += BtnFeedForce_Click;
            top.Controls.Add(btnFeedForce);

            var lblLed = new Label { Text = "LED PWM:", Left = 720, Top = 11, AutoSize = true };
            top.Controls.Add(lblLed);

            tbLed = new TrackBar
            {
                Left = 785,
                Top = 0,
                Width = 140,
                Minimum = 0,
                Maximum = 255,
                TickFrequency = 32,
                Value = _main.LedPwm,
                AutoSize = false,
                Height = 40
            };
            tbLed.ValueChanged += TbLed_ValueChanged;
            top.Controls.Add(tbLed);

            lblLedValue = new Label
            {
                Left = 930,
                Top = 11,
                AutoSize = true,
                Text = tbLed.Value.ToString()
            };
            top.Controls.Add(lblLedValue);

            lblPrimerMask = new Label
            {
                Left = 10,
                Top = 35,
                AutoSize = true,
                Text = "Primer mask: " + _primerMaskDiameter + " px"
            };
            top.Controls.Add(lblPrimerMask);

            tbPrimerMask = new TrackBar
            {
                Left = 110,
                Top = 30,
                Width = 180,
                Minimum = 20,
                Maximum = 150,
                TickFrequency = 10,
                AutoSize = false,
                Height = 30
            };
            tbPrimerMask.ValueChanged += TbPrimerMask_ValueChanged;
            top.Controls.Add(tbPrimerMask);

            lblBgClip = new Label
            {
                Left = 300,
                Top = 35,
                AutoSize = true,
                Text = "BG clip: " + _bgClipThreshold
            };
            top.Controls.Add(lblBgClip);

            tbBgClip = new TrackBar
            {
                Left = 360,
                Top = 30,
                Width = 180,
                Minimum = 0,
                Maximum = 80,
                TickFrequency = 10,
                AutoSize = false,
                Height = 30
            };
            tbBgClip.ValueChanged += TbBgClip_ValueChanged;
            top.Controls.Add(tbBgClip);

            lblOuterMask = new Label
            {
                Left = 550,
                Top = 35,
                AutoSize = true,
                Text = "Outer mask: " + _outerMaskPercent + "%"
            };
            top.Controls.Add(lblOuterMask);

            tbOuterMask = new TrackBar
            {
                Left = 640,
                Top = 30,
                Width = 180,
                Minimum = 80,
                Maximum = 140,
                TickFrequency = 5,
                AutoSize = false,
                Height = 30
            };
            tbOuterMask.ValueChanged += TbOuterMask_ValueChanged;
            top.Controls.Add(tbOuterMask);

            lblStatus = new Label
            {
                Left = 830,
                Top = 35,
                AutoSize = true,
                Text = "Status: Idle"
            };
            top.Controls.Add(lblStatus);

            pbImage = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            pbImage.Paint += PbImage_Paint;
            pbImage.MouseDown += PbImage_MouseDown;
            pbImage.MouseMove += PbImage_MouseMove;
            pbImage.MouseUp += PbImage_MouseUp;
            pbImage.Resize += (s, e) => pbImage.Invalidate();
            center.Panel1.Controls.Add(pbImage);

            pbCropPreview = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.DimGray,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            center.Panel2.Controls.Add(pbCropPreview);

            FormClosing += CameraForm_FormClosing;
        }

        private void TbLed_ValueChanged(object sender, EventArgs e)
        {
            lblLedValue.Text = tbLed.Value.ToString();
            _main.LedPwm = tbLed.Value;
        }

        private void TbPrimerMask_ValueChanged(object sender, EventArgs e)
        {
            _primerMaskDiameter = tbPrimerMask.Value;
            lblPrimerMask.Text = "Primer mask: " + _primerMaskDiameter + " px";
            SavePrimerMaskToDisk();
            UpdateCropPreview();
        }

        private void TbBgClip_ValueChanged(object sender, EventArgs e)
        {
            _bgClipThreshold = tbBgClip.Value;
            lblBgClip.Text = "BG clip: " + _bgClipThreshold;
            SaveBgClipToDisk();
            UpdateCropPreview();
        }

        private void TbOuterMask_ValueChanged(object sender, EventArgs e)
        {
            _outerMaskPercent = tbOuterMask.Value;
            lblOuterMask.Text = "Outer mask: " + _outerMaskPercent + "%";
            SaveOuterMaskToDisk();
            UpdateCropPreview();
        }

        private async void BtnFeedForce_Click(object sender, EventArgs e)
        {
            if (!_main.IsSerialConnected)
            {
                MessageBox.Show("Serial not connected.", "Feed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnFeedForce.Enabled = false;
            lblStatus.Text = "Status: Feed (force) xf:0…";

            try
            {
                _main.SendCommandFromCamera("xf:0");
                bool done = await _main.WaitForDoneAsync(10000);

                lblStatus.Text = done
                    ? "Status: Feed (force) done."
                    : "Status: Feed (force) timeout waiting for 'done'.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Status: Feed (force) error.";
                MessageBox.Show("Feed (force) failed: " + ex.Message,
                    "Feed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnFeedForce.Enabled = true;
            }
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            StartCamera();
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            StopCamera();
        }

        private void CameraForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopCamera();
        }

        private void BtnSettings_Click(object sender, EventArgs e)
        {
            try
            {
                if (_videoSource != null)
                {
                    _videoSource.DisplayPropertyPage(Handle);
                    return;
                }

                if (_videoDevices == null || _videoDevices.Count == 0)
                {
                    MessageBox.Show("No video devices found.", "Camera settings",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (cbCameras.SelectedIndex < 0)
                {
                    MessageBox.Show("Select a camera first.", "Camera settings",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                FilterInfo devInfo = _videoDevices[cbCameras.SelectedIndex];
                VideoCaptureDevice tempSource = new VideoCaptureDevice(devInfo.MonikerString);
                tempSource.DisplayPropertyPage(Handle);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open camera properties: " + ex.Message,
                    "Camera settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnCapture_Click(object sender, EventArgs e)
        {
            UpdateCropPreview();
        }

        private void LoadCameras()
        {
            try
            {
                _videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                cbCameras.Items.Clear();

                string lastMoniker = _main.LastCameraMoniker;
                int selectedIndex = -1;

                for (int i = 0; i < _videoDevices.Count; i++)
                {
                    FilterInfo fi = _videoDevices[i];
                    cbCameras.Items.Add(fi.Name);
                    if (!string.IsNullOrEmpty(lastMoniker) && fi.MonikerString == lastMoniker)
                        selectedIndex = i;
                }

                if (cbCameras.Items.Count > 0)
                {
                    if (selectedIndex >= 0)
                        cbCameras.SelectedIndex = selectedIndex;
                    else
                        cbCameras.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to enumerate cameras: " + ex.Message,
                    "Camera", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            Bitmap frame;
            try
            {
                frame = (Bitmap)eventArgs.Frame.Clone();
            }
            catch
            {
                return;
            }

            lock (_frameLock)
            {
                if (_lastFrame != null)
                    _lastFrame.Dispose();

                _lastFrame = frame;
                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                _frameSeq++;
            }

            try
            {
                if (!IsDisposed && pbImage.IsHandleCreated)
                {
                    pbImage.BeginInvoke(new Action(() =>
                    {
                        Bitmap toShow = null;
                        lock (_frameLock)
                        {
                            if (_lastFrame != null)
                                toShow = (Bitmap)_lastFrame.Clone();
                        }

                        Bitmap oldUi = pbImage.Image as Bitmap;
                        pbImage.Image = toShow;
                        if (oldUi != null) oldUi.Dispose();

                        pbImage.Invalidate();
                    }));
                }
            }
            catch
            {
            }
        }

        // ------------------ ROI + mapping ------------------

        private Rectangle ClampRoiToImage(Rectangle rect)
        {
            if (_frameWidth <= 0 || _frameHeight <= 0)
                return Rectangle.Empty;

            if (rect.X < 0) rect.X = 0;
            if (rect.Y < 0) rect.Y = 0;
            if (rect.Right > _frameWidth) rect.Width = _frameWidth - rect.X;
            if (rect.Bottom > _frameHeight) rect.Height = _frameHeight - rect.Y;

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                int size = Math.Min(_frameWidth, _frameHeight);
                return new Rectangle(0, 0, size, size);
            }

            return rect;
        }

        private void InitDefaultRoi()
        {
            if (_frameWidth <= 0 || _frameHeight <= 0)
                return;

            int size = Math.Min(_frameWidth, _frameHeight);
            int x = (_frameWidth - size) / 2;
            int y = (_frameHeight - size) / 2;
            _roiImageRect = new Rectangle(x, y, size, size);
            _roiInitialized = true;
        }

        private Rectangle ImageRectToControl(Rectangle imgRect)
        {
            if (pbImage == null || _frameWidth <= 0 || _frameHeight <= 0)
                return Rectangle.Empty;

            Rectangle clientRect = pbImage.ClientRectangle;
            if (clientRect.Width <= 0 || clientRect.Height <= 0)
                return Rectangle.Empty;

            float imgAspect = (float)_frameWidth / _frameHeight;
            float ctrlAspect = (float)clientRect.Width / clientRect.Height;

            Rectangle drawRect;
            if (imgAspect > ctrlAspect)
            {
                int w = clientRect.Width;
                int h = (int)(w / imgAspect);
                int y = clientRect.Top + (clientRect.Height - h) / 2;
                drawRect = new Rectangle(clientRect.Left, y, w, h);
            }
            else
            {
                int h = clientRect.Height;
                int w = (int)(h * imgAspect);
                int x = clientRect.Left + (clientRect.Width - w) / 2;
                drawRect = new Rectangle(x, clientRect.Top, w, h);
            }

            _scaleX = (float)drawRect.Width / _frameWidth;
            _scaleY = (float)drawRect.Height / _frameHeight;

            int rx = drawRect.Left + (int)(imgRect.X * _scaleX);
            int ry = drawRect.Top + (int)(imgRect.Y * _scaleY);
            int rw = (int)(imgRect.Width * _scaleX);
            int rh = (int)(imgRect.Height * _scaleY);

            return new Rectangle(rx, ry, rw, rh);
        }

        private DrawingPoint ControlPointToImage(DrawingPoint pt)
        {
            if (pbImage == null || _frameWidth <= 0 || _frameHeight <= 0)
                return DrawingPoint.Empty;

            Rectangle clientRect = pbImage.ClientRectangle;
            if (clientRect.Width <= 0 || clientRect.Height <= 0)
                return DrawingPoint.Empty;

            float imgAspect = (float)_frameWidth / _frameHeight;
            float ctrlAspect = (float)clientRect.Width / clientRect.Height;

            Rectangle drawRect;
            if (imgAspect > ctrlAspect)
            {
                int w = clientRect.Width;
                int h = (int)(w / imgAspect);
                int y = clientRect.Top + (clientRect.Height - h) / 2;
                drawRect = new Rectangle(clientRect.Left, y, w, h);
            }
            else
            {
                int h = clientRect.Height;
                int w = (int)(h * imgAspect);
                int x = clientRect.Left + (clientRect.Width - w) / 2;
                drawRect = new Rectangle(x, clientRect.Top, w, h);
            }

            if (!drawRect.Contains(pt))
                return DrawingPoint.Empty;

            float u = (pt.X - drawRect.Left) / (float)drawRect.Width;
            float v = (pt.Y - drawRect.Top) / (float)drawRect.Height;

            int ix = (int)(u * _frameWidth);
            int iy = (int)(v * _frameHeight);

            if (ix < 0) ix = 0;
            if (iy < 0) iy = 0;
            if (ix >= _frameWidth) ix = _frameWidth - 1;
            if (iy >= _frameHeight) iy = _frameHeight - 1;

            return new DrawingPoint(ix, iy);
        }

        private void PbImage_Paint(object sender, PaintEventArgs e)
        {
            if (_frameWidth <= 0 || _frameHeight <= 0)
                return;

            if (!_roiInitialized)
                InitDefaultRoi();

            Rectangle ctrlRect = ImageRectToControl(_roiImageRect);
            if (ctrlRect.Width <= 0 || ctrlRect.Height <= 0)
                return;

            using (Pen pen = new Pen(Color.Lime, 2))
            {
                pen.Alignment = PenAlignment.Inset;
                e.Graphics.DrawRectangle(pen, ctrlRect);
            }

            Rectangle handleRect = new Rectangle(
                ctrlRect.Right - ResizeHandleSize,
                ctrlRect.Bottom - ResizeHandleSize,
                ResizeHandleSize,
                ResizeHandleSize);

            using (SolidBrush b = new SolidBrush(Color.FromArgb(160, Color.Lime)))
            {
                e.Graphics.FillRectangle(b, handleRect);
            }
        }

        private void PbImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (_frameWidth <= 0 || _frameHeight <= 0)
                return;

            if (!_roiInitialized)
                InitDefaultRoi();

            DrawingPoint imgPoint = ControlPointToImage(e.Location);
            if (imgPoint == DrawingPoint.Empty)
                return;

            Rectangle ctrlRect = ImageRectToControl(_roiImageRect);
            Rectangle handleRect = new Rectangle(
                ctrlRect.Right - ResizeHandleSize,
                ctrlRect.Bottom - ResizeHandleSize,
                ResizeHandleSize,
                ResizeHandleSize);

            if (handleRect.Contains(e.Location))
            {
                _resizing = true;
                _dragging = false;
                _dragStartImagePoint = imgPoint;
                _roiStartRect = _roiImageRect;
            }
            else
            {
                _dragging = true;
                _resizing = false;
                _dragStartImagePoint = imgPoint;
                _roiStartRect = _roiImageRect;
            }
        }

        private void PbImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging && !_resizing)
                return;

            DrawingPoint imgPoint = ControlPointToImage(e.Location);
            if (imgPoint == DrawingPoint.Empty)
                return;

            if (_resizing)
            {
                int dx = imgPoint.X - _dragStartImagePoint.X;
                int dy = imgPoint.Y - _dragStartImagePoint.Y;
                int delta = Math.Max(dx, dy);

                Rectangle newRect = new Rectangle(
                    _roiStartRect.X,
                    _roiStartRect.Y,
                    _roiStartRect.Width + delta,
                    _roiStartRect.Height + delta);

                if (newRect.Width < 50) newRect.Width = 50;
                if (newRect.Height < 50) newRect.Height = 50;

                newRect = ClampRoiToImage(newRect);
                _roiImageRect = newRect;
            }
            else if (_dragging)
            {
                int dx = imgPoint.X - _dragStartImagePoint.X;
                int dy = imgPoint.Y - _dragStartImagePoint.Y;

                Rectangle newRect = new Rectangle(
                    _roiStartRect.X + dx,
                    _roiStartRect.Y + dy,
                    _roiStartRect.Width,
                    _roiStartRect.Height);

                newRect = ClampRoiToImage(newRect);
                _roiImageRect = newRect;
            }

            pbImage.Invalidate();
            UpdateCropPreview();
        }

        private void PbImage_MouseUp(object sender, MouseEventArgs e)
        {
            _dragging = false;
            _resizing = false;
            SaveRoiToDisk();
        }

        private void UpdateCropPreview()
        {
            Bitmap crop = null;
            try
            {
                crop = CaptureAndCropForRun();
            }
            catch
            {
            }

            Bitmap old = pbCropPreview.Image as Bitmap;
            pbCropPreview.Image = crop;
            if (old != null) old.Dispose();
        }

        // ------------------ OpenCV helpers ------------------

        private bool TryDetectCaseCircle(Bitmap roiBmp, out float centerX, out float centerY, out float radius)
        {
            centerX = roiBmp.Width / 2f;
            centerY = roiBmp.Height / 2f;
            radius = Math.Min(roiBmp.Width, roiBmp.Height) / 2f;

            try
            {
                using (Mat mat = BitmapConverter.ToMat(roiBmp))
                using (Mat gray = new Mat())
                {
                    Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
                    Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(9, 9), 2, 2);

                    // Tune radius range to prefer OUTER case head, not primer pocket
                    int minDim = Math.Min(roiBmp.Width, roiBmp.Height);
                    int minRadius = (int)(minDim * 0.30); // ignore small circles (primers etc.)
                    int maxRadius = (int)(minDim * 0.55); // a bit larger than expected rim

                    CircleSegment[] circles = Cv2.HoughCircles(
                        gray,
                        HoughModes.Gradient,
                        1.5,
                        minDim / 4,    // min distance between circle centers
                        100,
                        40,
                        minRadius,
                        maxRadius);

                    if (circles != null && circles.Length > 0)
                    {
                        // Choose largest radius within this constrained band
                        CircleSegment best = circles[0];
                        for (int i = 1; i < circles.Length; i++)
                        {
                            if (circles[i].Radius > best.Radius)
                                best = circles[i];
                        }

                        centerX = best.Center.X;
                        centerY = best.Center.Y;
                        radius = best.Radius;

                        // extra sanity check to reject "primer-sized" circles that sneak through
                        float rMinOk = minDim * 0.28f;
                        float rMaxOk = minDim * 0.60f;
                        if (radius < rMinOk || radius > rMaxOk)
                            return false;

                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private void UpdateCaseCircleDest(bool foundCircle, float cx, float cy, float radius,
                                          Rectangle cropRect, int destSize)
        {
            if (!foundCircle || cropRect.Width <= 0 || cropRect.Height <= 0)
            {
                _hasCaseCircleDest = false;
                _caseCenterXDest = _caseCenterYDest = _caseRadiusDest = 0;
                return;
            }

            // ensure the detected center lies inside the cropRect
            if (cx < cropRect.Left || cx > cropRect.Right ||
                cy < cropRect.Top || cy > cropRect.Bottom)
            {
                _hasCaseCircleDest = false;
                _caseCenterXDest = _caseCenterYDest = _caseRadiusDest = 0;
                return;
            }

            double scaleX = (double)destSize / cropRect.Width;
            double scaleY = (double)destSize / cropRect.Height;

            _caseCenterXDest = (cx - cropRect.X) * scaleX;
            _caseCenterYDest = (cy - cropRect.Y) * scaleY;
            _caseRadiusDest = radius * ((scaleX + scaleY) * 0.5); // should be same, but average just in case
            _hasCaseCircleDest = true;
        }

        private void ApplyBackgroundClip(Bitmap bmp)
        {
            if (bmp == null) return;
            if (_bgClipThreshold <= 0) return;

            int w = bmp.Width;
            int h = bmp.Height;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    int brightness = (c.R + c.G + c.B) / 3;
                    if (brightness <= _bgClipThreshold)
                    {
                        bmp.SetPixel(x, y, Color.Black);
                    }
                }
            }
        }

        private void ApplyOuterCircularMask(Bitmap bmp)
        {
            if (bmp == null) return;
            if (!_hasCaseCircleDest) return;
            if (_outerMaskPercent <= 0) return;

            int w = bmp.Width;
            int h = bmp.Height;
            if (w == 0 || h == 0) return;

            double cx = _caseCenterXDest;
            double cy = _caseCenterYDest;
            double radius = _caseRadiusDest * (_outerMaskPercent / 100.0);
            if (radius <= 0) return;

            double r2 = radius * radius;

            for (int y = 0; y < h; y++)
            {
                double dy = y - cy;
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx;
                    if (dx * dx + dy * dy > r2)
                    {
                        bmp.SetPixel(x, y, Color.Black);
                    }
                }
            }
        }

        private void ApplyPrimerMask(Bitmap bmp)
        {
            if (bmp == null) return;
            if (!_hasCaseCircleDest) return; // do NOT guess using image center

            int w = bmp.Width;
            int h = bmp.Height;
            if (w == 0 || h == 0) return;

            int diameter = _primerMaskDiameter;
            if (diameter <= 0) return;

            if (diameter > w || diameter > h)
                diameter = Math.Min(w, h);

            double radius = diameter / 2.0;
            double r2 = radius * radius;
            double cx = _caseCenterXDest;
            double cy = _caseCenterYDest;

            for (int y = 0; y < h; y++)
            {
                double dy = y - cy;
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx;
                    if (dx * dx + dy * dy <= r2)
                    {
                        bmp.SetPixel(x, y, Color.Black);
                    }
                }
            }
        }

        // ------------------ persistence ------------------

        private void LoadRoiFromDisk()
        {
            try
            {
                if (!File.Exists(RoiConfigFileName))
                    return;

                string text = File.ReadAllText(RoiConfigFileName);
                string[] parts = text.Split(',');
                if (parts.Length == 4)
                {
                    int x = int.Parse(parts[0]);
                    int y = int.Parse(parts[1]);
                    int w = int.Parse(parts[2]);
                    int h = int.Parse(parts[3]);

                    _roiImageRect = new Rectangle(x, y, w, h);
                    _roiInitialized = true;
                }
            }
            catch
            {
            }
        }

        private void SaveRoiToDisk()
        {
            try
            {
                if (!_roiInitialized)
                    return;

                string text = _roiImageRect.X + "," + _roiImageRect.Y + "," +
                              _roiImageRect.Width + "," + _roiImageRect.Height;
                File.WriteAllText(RoiConfigFileName, text);
            }
            catch
            {
            }
        }

        private void LoadPrimerMaskFromDisk()
        {
            try
            {
                if (!File.Exists(PrimerMaskFileName))
                    return;

                string text = File.ReadAllText(PrimerMaskFileName).Trim();
                int value;
                if (int.TryParse(text, out value))
                {
                    if (value < 20) value = 20;
                    if (value > 150) value = 150;
                    _primerMaskDiameter = value;
                }
            }
            catch
            {
            }
        }

        private void SavePrimerMaskToDisk()
        {
            try
            {
                File.WriteAllText(PrimerMaskFileName, _primerMaskDiameter.ToString());
            }
            catch
            {
            }
        }

        private void LoadBgClipFromDisk()
        {
            try
            {
                if (!File.Exists(BgClipFileName))
                    return;

                string text = File.ReadAllText(BgClipFileName).Trim();
                int value;
                if (int.TryParse(text, out value))
                {
                    if (value < 0) value = 0;
                    if (value > 80) value = 80;
                    _bgClipThreshold = value;
                }
            }
            catch
            {
            }
        }

        private void SaveBgClipToDisk()
        {
            try
            {
                File.WriteAllText(BgClipFileName, _bgClipThreshold.ToString());
            }
            catch
            {
            }
        }

        private void LoadOuterMaskFromDisk()
        {
            try
            {
                if (!File.Exists(OuterMaskFileName))
                    return;

                string text = File.ReadAllText(OuterMaskFileName).Trim();
                int value;
                if (int.TryParse(text, out value))
                {
                    if (value < 80) value = 80;
                    if (value > 140) value = 140;
                    _outerMaskPercent = value;
                }
            }
            catch
            {
            }
        }

        private void SaveOuterMaskToDisk()
        {
            try
            {
                File.WriteAllText(OuterMaskFileName, _outerMaskPercent.ToString());
            }
            catch
            {
            }
        }
    }
}
