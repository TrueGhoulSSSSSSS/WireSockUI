using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WireSockUI.Config;
using WireSockUI.Extensions;
using WireSockUI.Native;
using WireSockUI.Properties;
using static WireSockUI.Native.WireguardBoosterExports;

namespace WireSockUI.Forms
{
    public partial class FrmMain : Form, IMessageFilter
    {
        // ═══════════════════════════════════════════
        //  BACKEND
        // ═══════════════════════════════════════════
        private readonly BackgroundWorker _tunnelConnectionWorker;
        private readonly BackgroundWorker _tunnelStateWorker;
        private readonly WireSockManager _wiresock;
        private readonly ZapretManager _zapret;
        private ConnectionState _currentState = ConnectionState.Disconnected;

        // ═══════════════════════════════════════════
        //  TRAY
        // ═══════════════════════════════════════════
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private bool _allowClose = false;

        // ═══════════════════════════════════════════
        //  UI CONTROLS
        // ═══════════════════════════════════════════
        private GlowToggle _toggleBtn;
        private AnimatedColorLabel _statusLabel;
        private AnimatedColorLabel _statusHint;
        private ComboBox _profileSelector;
        private Label _ipLabel;
        private GradientPanel _heroCard;

        // Кнопки управления профилем
        private GradientButton _btnAddProfile;
        private GradientButton _btnGenerate;
        private GradientButton _btnEditProfile;
        private GradientButton _btnDeleteProfile;

        private GradientButton _btnLogs;

        // ═══════════════════════════════════════════
        //  TERMINAL LOG
        // ═══════════════════════════════════════════
        private Panel _pnlLogs;
        private RichTextBox _txtLog;
        private System.Windows.Forms.Timer _logAnimationTimer;
        private bool _isLogOpen = false;
        private int _logTargetHeight = 280;
        private int _logCurrentHeight = 0;
        private int _baseFormHeight = 680;

        private ModernCheckBox _chkAutostart;
        private Label _lblSessionTimer;
        private System.Windows.Forms.Timer _sessionTimer;
        private DateTime _sessionStartTime;
        private bool _hasShownPromoNotification = false;

        public enum ConnectionState { Connecting, Connected, Disconnected }

        public FrmMain()
        {
            InitializeComponent();
            Application.AddMessageFilter(this); // Подключаем глобальный перехват Drag&Drop

            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.DoubleBuffer |
                ControlStyles.OptimizedDoubleBuffer, true);

            // ── Window ──
            this.Controls.Clear();
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Text = "ThanksRKN";
            this.ClientSize = new Size(420, _baseFormHeight);
            this.BackColor = Theme.BgDeep;
            this.Icon = Resources.ico;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9f);

            SetupTrayIcon();
            BuildUI();

            // ── Backend init ──
            if (!Debugger.IsAttached && !IsCurrentProcessElevated() && !Settings.Default.DisableAutoAdmin)
                RestartAsAdmin();

            if (IsApplicationAlreadyRunning())
            {
                MessageBox.Show(Resources.AlreadyRunningMessage, Resources.AlreadyRunningTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                Environment.Exit(1);
            }

            _tunnelConnectionWorker = InitializeTunnelConnectionWorker();
            _tunnelStateWorker = InitTunnelStateWorker();

            _wiresock = new WireSockManager(OnWireSockLogMessage);
            _zapret = new ZapretManager();

            // ── ЖЕСТКИЙ ФИЛЬТР ЛОГОВ ZAPRET ──
            _zapret.LogMessage += (sender, msg) => {
                string lowerMsg = msg.ToLowerInvariant();

                // Оставляем только успешную инициализацию
                if (lowerMsg.Contains("windivert initialized. capture is started."))
                {
                    LogToTerminal("[DPI] windivert initialized. capture is started.", Theme.Cyan);
                }
                // Пропускаем реальные ошибки, если они будут
                else if (lowerMsg.Contains("error") || lowerMsg.Contains("failed") || lowerMsg.Contains("exception"))
                {
                    LogToTerminal($"[DPI] {msg}", Theme.Error);
                }
            };

            _wiresock.LogLevel = _wiresock.LogLevelSetting;
            LoadProfilesToCombo();

            LogToTerminal("ThanksRKN initialized. Ready.", Theme.Accent);

            _sessionTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _sessionTimer.Tick += OnSessionTimerTick;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UIHelpers.ApplyDarkTitleBar(this.Handle);
            UIHelpers.ApplyMicaBackdrop(this.Handle);
            EnableAdminDragDrop(this); // Активируем обход блокировки UAC
        }

        // Рекурсивное применение обхода UAC для всех элементов окна
        private void EnableAdminDragDrop(Control control)
        {
            UIHelpers.ChangeWindowMessageFilterEx(control.Handle, 0x0233, 1, IntPtr.Zero); // WM_DROPFILES
            UIHelpers.ChangeWindowMessageFilterEx(control.Handle, 0x0049, 1, IntPtr.Zero); // WM_COPYGLOBALDATA
            UIHelpers.ChangeWindowMessageFilterEx(control.Handle, 0x004A, 1, IntPtr.Zero); // WM_COPYDATA
            UIHelpers.DragAcceptFiles(control.Handle, true);

            foreach (Control child in control.Controls)
                EnableAdminDragDrop(child);
        }

        // Ловим перетаскивание файлов системным хуком (в обход WinForms)
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == 0x0233) // WM_DROPFILES
            {
                IntPtr hDrop = m.WParam;
                uint count = UIHelpers.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                string[] files = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    StringBuilder sb = new StringBuilder(260);
                    UIHelpers.DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    files[i] = sb.ToString();
                }
                UIHelpers.DragFinish(hDrop);

                HandleDroppedFiles(files);
                return true;
            }
            return false;
        }

        // ═══════════════════════════════════════════════════════════════
        //  U I   L A Y O U T
        // ════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            // ── HEADER ──
            var pnlHeader = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Theme.BgDeep };
            var lblBrand = new Label { Text = "THANKS", Font = new Font("Segoe UI", 16f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = new Point(24, 22), BackColor = Color.Transparent };
            var lblTag = new GradientLabel
            {
                Text = "RKN",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                AutoSize = true,
                ColorTop = Theme.Error,
                ColorBottom = Theme.ErrorDeep,
                BackColor = Color.Transparent
            };

            lblBrand.HandleCreated += (s, e) => { lblTag.Location = new Point(lblBrand.Right + 2, 28); };

            _btnLogs = new GradientButton
            {
                Text = "⌘  LOGS",
                ForeColor = Theme.TextDim,
                BaseTop = Theme.Surface,
                BaseBottom = Theme.SurfaceDeep,
                HoverTop = Color.FromArgb(40, 40, 50),
                HoverBottom = Color.FromArgb(32, 32, 42),
                Size = new Size(96, 36),
                Location = new Point(this.ClientSize.Width - 120, 18),
                Radius = 10,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _btnLogs.Click += (s, e) => ToggleLogs();

            pnlHeader.Controls.Add(lblBrand);
            pnlHeader.Controls.Add(lblTag);
            pnlHeader.Controls.Add(_btnLogs);

            // ── MAIN BODY ──
            var pnlBody = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 6, 20, 16), BackColor = Theme.BgDeep };
            _heroCard = new GradientPanel
            {
                Dock = DockStyle.Top,
                Height = 480,
                ColorTop = Theme.CardTop,
                ColorBottom = Theme.CardBottom,
                Radius = 20,
                BorderColor = Theme.Border,
                BorderThickness = 1,
                AccentColor = Theme.Accent,
                AccentIntensity = 0.0f
            };

            _statusLabel = new AnimatedColorLabel
            {
                Text = "DISCONNECTED",
                Dock = DockStyle.Top,
                Height = 64,
                TextAlign = ContentAlignment.BottomCenter,
                Font = new Font("Segoe UI Black", 17f, FontStyle.Bold),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent
            };
            _statusHint = new AnimatedColorLabel
            {
                Text = "select profile and connect",
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.TopCenter,
                Font = new Font("Segoe UI", 9.25f),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent
            };

            var pnlToggle = new Panel { Dock = DockStyle.Top, Height = 110, BackColor = Color.Transparent };
            _toggleBtn = new GlowToggle { Size = new Size(96, 52) };
            pnlToggle.SizeChanged += (s, e) => { _toggleBtn.Location = new Point((pnlToggle.Width - _toggleBtn.Width) / 2, (pnlToggle.Height - _toggleBtn.Height) / 2); };
            _toggleBtn.CheckedChanged += OnToggleChanged;
            pnlToggle.Controls.Add(_toggleBtn);

            _lblSessionTimer = new Label
            {
                Text = "00:00:00",
                Dock = DockStyle.Top,
                Height = 22,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 12f, FontStyle.Bold),
                ForeColor = Theme.Accent,
                BackColor = Color.Transparent,
                Visible = false
            };

            // ──── Профиль (комбо сверху) ────
            var pnlComboRow = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.Transparent, Padding = new Padding(22, 6, 22, 6) };
            _profileSelector = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 11f),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 28,
                Cursor = Cursors.Hand
            };
            _profileSelector.DrawItem += ComboBox_DrawItem;
            pnlComboRow.Controls.Add(_profileSelector);

            // ──── Экшен-кнопки (DEL / EDIT / Создать / ADD) ────
            var pnlButtonsRow = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Color.Transparent, Padding = new Padding(22, 0, 22, 14) };
            var tblButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent };
            tblButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            tblButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            tblButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            tblButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));

            _btnDeleteProfile = new GradientButton
            {
                Text = "🗑  DEL",
                Dock = DockStyle.Fill,
                BaseTop = Theme.Surface,
                BaseBottom = Theme.SurfaceDeep,
                HoverTop = Theme.Error,
                HoverBottom = Theme.ErrorDeep,
                ForeColor = Theme.TextDim,
                HoverForeColor = Color.White,
                Radius = 9,
                Font = new Font("Segoe UI Semibold", 8.75f),
                Margin = new Padding(0, 0, 3, 0),
                Glow = true,
                GlowColor = Theme.Error
            };
            _btnEditProfile = new GradientButton
            {
                Text = "✎  EDIT",
                Dock = DockStyle.Fill,
                BaseTop = Theme.Surface,
                BaseBottom = Theme.SurfaceDeep,
                HoverTop = Color.FromArgb(56, 189, 248),
                HoverBottom = Color.FromArgb(14, 165, 233),
                ForeColor = Theme.TextDim,
                HoverForeColor = Color.White,
                Radius = 9,
                Font = new Font("Segoe UI Semibold", 8.75f),
                Margin = new Padding(2, 0, 2, 0),
                Glow = true,
                GlowColor = Color.FromArgb(56, 189, 248)
            };
            _btnGenerate = new GradientButton
            {
                Text = "⚡  Создать",
                Dock = DockStyle.Fill,
                BaseTop = Theme.Surface,
                BaseBottom = Theme.SurfaceDeep,
                HoverTop = Theme.Warn,
                HoverBottom = Theme.WarnDeep,
                ForeColor = Theme.Warn,
                HoverForeColor = Color.Black,
                Radius = 9,
                Font = new Font("Segoe UI Semibold", 8.75f),
                Margin = new Padding(2, 0, 2, 0),
                Glow = true,
                GlowColor = Theme.Warn
            };
            _btnAddProfile = new GradientButton
            {
                Text = "+  ADD",
                Dock = DockStyle.Fill,
                BaseTop = Theme.Surface,
                BaseBottom = Theme.SurfaceDeep,
                HoverTop = Theme.Accent,
                HoverBottom = Theme.AccentDeep,
                ForeColor = Theme.Accent,
                HoverForeColor = Color.Black,
                Radius = 9,
                Font = new Font("Segoe UI Semibold", 8.75f),
                Margin = new Padding(3, 0, 0, 0),
                Glow = true,
                GlowColor = Theme.Accent
            };

            _btnDeleteProfile.Click += OnDeleteProfileClick;
            _btnEditProfile.Click += OnEditProfileClick;
            _btnGenerate.Click += OnGenerateClick;
            _btnAddProfile.Click += OnAddProfileClick;

            tblButtons.Controls.Add(_btnDeleteProfile, 0, 0);
            tblButtons.Controls.Add(_btnEditProfile, 1, 0);
            tblButtons.Controls.Add(_btnGenerate, 2, 0);
            tblButtons.Controls.Add(_btnAddProfile, 3, 0);

            pnlButtonsRow.Controls.Add(tblButtons);

            _ipLabel = new Label
            {
                Text = "tap toggle to connect",
                Dock = DockStyle.Bottom,
                Height = 50,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 9.5f),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent
            };

            var pnlAutostart = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent };
            _chkAutostart = new ModernCheckBox
            {
                Text = "Launch at startup",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
                Checked = IsInStartup(),
                AutoSize = false,
                Width = 160,
                Height = 24
            };
            _chkAutostart.CheckedChanged += OnAutostartChanged;
            pnlAutostart.SizeChanged += (s, e) => { _chkAutostart.Location = new Point((pnlAutostart.Width - _chkAutostart.Width) / 2 + 5, (pnlAutostart.Height - _chkAutostart.Height) / 2); };
            pnlAutostart.Controls.Add(_chkAutostart);

            // Сборка карточки (Снизу вверх из-за Dock)
            _heroCard.Controls.Add(pnlAutostart);
            _heroCard.Controls.Add(_ipLabel);
            _heroCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 16, BackColor = Color.Transparent });
            _heroCard.Controls.Add(pnlButtonsRow);
            _heroCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Color.Transparent });
            _heroCard.Controls.Add(pnlComboRow);
            _heroCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.Transparent });
            _heroCard.Controls.Add(_lblSessionTimer);
            _heroCard.Controls.Add(pnlToggle);
            _heroCard.Controls.Add(_statusHint);
            _heroCard.Controls.Add(_statusLabel);

            pnlBody.Controls.Add(_heroCard);

            // ── FOOTER ──
            var pnlFooter = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(20, 0, 20, 20), BackColor = Theme.BgDeep };
            var tblLinks = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
            tblLinks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            tblLinks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            var btnDiscord = new GradientButton
            {
                Text = "Discord",
                BaseTop = Color.FromArgb(40, 42, 60),
                BaseBottom = Color.FromArgb(28, 30, 46),
                HoverTop = Color.FromArgb(110, 122, 255),
                HoverBottom = Theme.Discord,
                ForeColor = Theme.Text,
                HoverForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Radius = 10,
                Margin = new Padding(0, 0, 5, 0),
                Dock = DockStyle.Fill,
                Glow = true,
                GlowColor = Theme.Discord
            };
            var btnTg = new GradientButton
            {
                Text = "Telegram",
                BaseTop = Color.FromArgb(28, 44, 60),
                BaseBottom = Color.FromArgb(22, 34, 48),
                HoverTop = Color.FromArgb(64, 192, 240),
                HoverBottom = Theme.Telegram,
                ForeColor = Theme.Text,
                HoverForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Radius = 10,
                Margin = new Padding(5, 0, 0, 0),
                Dock = DockStyle.Fill,
                Glow = true,
                GlowColor = Theme.Telegram
            };

            btnDiscord.Click += (s, e) => SafeOpenUrl("https://discord.gg/CnjNJZGJ");
            btnTg.Click += (s, e) => SafeOpenUrl("https://t.me/hatethisproject");

            tblLinks.Controls.Add(btnDiscord, 0, 0);
            tblLinks.Controls.Add(btnTg, 1, 0);

            var lblCredits = new Label
            {
                Text = "thanksrkn.dev · @hatethisproject",
                Dock = DockStyle.Bottom,
                Height = 22,
                TextAlign = ContentAlignment.BottomCenter,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(110, 110, 124),
                BackColor = Color.Transparent
            };

            pnlFooter.Controls.Add(tblLinks);
            pnlFooter.Controls.Add(lblCredits);

            this.Controls.Add(pnlBody);
            this.Controls.Add(pnlFooter);
            this.Controls.Add(pnlHeader);

            SetupLogsPanel();
        }

        private void ComboBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            ComboBox combo = sender as ComboBox;
            bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool isFocused = (e.State & DrawItemState.Focus) == DrawItemState.Focus;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // background
            using (var b = new SolidBrush(Theme.Surface))
                g.FillRectangle(b, e.Bounds);

            if (isSelected)
            {
                var pillRect = Rectangle.Inflate(e.Bounds, -4, -2);
                using (var path = UIHelpers.GetRoundedPath(pillRect, 6))
                using (var grad = new LinearGradientBrush(pillRect, Theme.Accent, Theme.AccentDeep, LinearGradientMode.Vertical))
                    g.FillPath(grad, path);

                TextRenderer.DrawText(g, combo.Items[e.Index].ToString(), combo.Font,
                    Rectangle.Inflate(e.Bounds, -8, 0), Color.Black,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            }
            else
            {
                TextRenderer.DrawText(g, combo.Items[e.Index].ToString(), combo.Font,
                    Rectangle.Inflate(e.Bounds, -8, 0), Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  TERMINAL / LOGS
        // ═══════════════════════════════════════════════════════════════

        private void SetupLogsPanel()
        {
            _pnlLogs = new Panel { Height = 0, Dock = DockStyle.Bottom, BackColor = Color.FromArgb(8, 8, 12) };

            var header = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.FromArgb(14, 14, 20) };
            var lblTerm = new Label { Text = "> TERMINAL", ForeColor = Theme.Accent, Font = new Font("Consolas", 9f, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0), BackColor = Color.Transparent };
            header.Controls.Add(lblTerm);
            header.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border });

            var txtWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 12), BackColor = Color.FromArgb(8, 8, 12) };
            _txtLog = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(8, 8, 12), ForeColor = Color.FromArgb(190, 195, 210), Font = new Font("Consolas", 8.75f), BorderStyle = BorderStyle.None, ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Vertical };
            txtWrap.Controls.Add(_txtLog);

            _pnlLogs.Controls.Add(txtWrap);
            _pnlLogs.Controls.Add(header);

            this.Controls.Add(_pnlLogs);
            _pnlLogs.BringToFront();

            _logAnimationTimer = new System.Windows.Forms.Timer { Interval = 14 };
            _logAnimationTimer.Tick += OnLogAnimationTick;
        }

        private void ToggleLogs()
        {
            _isLogOpen = !_isLogOpen;
            _logAnimationTimer.Start();
            _btnLogs.ForeColor = _isLogOpen ? Theme.Accent : Theme.TextDim;
        }

        private void OpenLogs()
        {
            if (!_isLogOpen) { ToggleLogs(); }
        }

        private void OnLogAnimationTick(object sender, EventArgs e)
        {
            int target = _isLogOpen ? _logTargetHeight : 0;
            int delta = target - _logCurrentHeight;

            if (Math.Abs(delta) < 2)
            {
                _logCurrentHeight = target;
                _pnlLogs.Height = _logCurrentHeight;
                this.ClientSize = new Size(this.ClientSize.Width, _baseFormHeight + _logCurrentHeight);
                _logAnimationTimer.Stop();
                return;
            }

            // Cubic ease-out style step.
            int step = (int)Math.Round(delta * 0.22);
            if (step == 0) step = Math.Sign(delta);

            _logCurrentHeight += step;
            _pnlLogs.Height = _logCurrentHeight;
            this.ClientSize = new Size(this.ClientSize.Width, _baseFormHeight + _logCurrentHeight);
        }

        private void LogToTerminal(string text, Color color)
        {
            if (_txtLog == null || _txtLog.IsDisposed) return;
            Action logAction = () => {
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.SelectionLength = 0;
                _txtLog.SelectionColor = Color.FromArgb(80, 80, 95);
                _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] ");
                _txtLog.SelectionColor = Color.FromArgb(120, 120, 135);
                _txtLog.AppendText("[SYS] ");
                _txtLog.SelectionColor = color;
                _txtLog.AppendText(text + Environment.NewLine);
                _txtLog.ScrollToCaret();
            };
            if (this.InvokeRequired) this.Invoke(logAction); else logAction();
        }

        private void OnWireSockLogMessage(WireSockManager.LogMessage msg)
        {
            if (_txtLog == null || _txtLog.IsDisposed) return;
            Action logAction = () => {
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.SelectionLength = 0;
                _txtLog.SelectionColor = Color.FromArgb(80, 80, 95);
                _txtLog.AppendText($"[{msg.Timestamp:HH:mm:ss}] ");
                _txtLog.SelectionColor = Color.FromArgb(120, 120, 135);
                _txtLog.AppendText("[TUN] ");

                string lowerMsg = msg.Message.ToLowerInvariant();
                if (lowerMsg.Contains("error") || lowerMsg.Contains("failed") || lowerMsg.Contains("failure")) _txtLog.SelectionColor = Theme.Error;
                else if (lowerMsg.Contains("warning")) _txtLog.SelectionColor = Theme.Warn;
                else if (lowerMsg.Contains("handshake") || lowerMsg.Contains("connected")) _txtLog.SelectionColor = Color.FromArgb(56, 189, 248);
                else _txtLog.SelectionColor = Color.FromArgb(190, 195, 210);

                if (lowerMsg.Contains("tunnel seems to be down"))
                {
                    _txtLog.SelectionColor = Theme.Warn;
                    if (_currentState == ConnectionState.Connected)
                    {
                        _statusLabel.SetText("UNSTABLE", Theme.Warn);
                        _statusHint.SetText("connection disrupted", Theme.Warn);
                        if (_heroCard != null) { _heroCard.AccentColor = Theme.Warn; _heroCard.AccentIntensity = 0.55f; }
                    }
                }
                else if (lowerMsg.Contains("handshake response"))
                {
                    if (_statusLabel.Text == "UNSTABLE")
                    {
                        _statusLabel.SetText("CONNECTED", Theme.Accent);
                        _statusHint.SetText("tunnel active", Theme.Accent);
                        if (_heroCard != null) { _heroCard.AccentColor = Theme.Accent; _heroCard.AccentIntensity = 0.7f; }
                    }
                }

                _txtLog.AppendText(msg.Message + Environment.NewLine);
                _txtLog.ScrollToCaret();
            };
            if (this.InvokeRequired) this.Invoke(logAction); else logAction();
        }

        // ═══════════════════════════════════════════════════════════════
        //  TRAY
        // ═══════════════════════════════════════════════════════════════

        private void SetupTrayIcon()
        {
            _trayMenu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };
            _trayMenu.Items.Add("Open UI", null, (s, e) => ShowFromTray());
            _trayMenu.Items.Add("-");
            _trayMenu.Items.Add("Exit", null, (s, e) => { _allowClose = true; Application.Exit(); });

            _trayIcon = new NotifyIcon { Text = "ThanksRKN", Icon = Resources.ico, ContextMenuStrip = _trayMenu, Visible = true };
            _trayIcon.DoubleClick += (s, e) => ShowFromTray();
        }

        private void ShowFromTray() { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; this.Hide(); return; }

            Application.RemoveMessageFilter(this); // Отключаем хук
            try
            {
                if (_zapret != null && _zapret.IsRunning) _zapret.Stop();
                _wiresock?.Disconnect();
                if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            }
            catch { /* swallow shutdown errors */ }
            base.OnFormClosing(e);
        }

        // ═══════════════════════════════════════════════════════════════
        //  DRAG & DROP
        // ═══════════════════════════════════════════════════════════════

        private void HandleDroppedFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext == ".conf") { ImportConfig(file); OpenLogs(); }
                else if (ext == ".exe") { AddAppToTunnel(file); OpenLogs(); }
            }
        }

        private void AddAppToTunnel(string exePath)
        {
            if (_profileSelector.SelectedItem == null || _profileSelector.SelectedItem.ToString() == "No Configs") { LogToTerminal("No profile selected to add application.", Theme.Error); return; }
            string profileName = _profileSelector.SelectedItem.ToString();
            string profilePath = Profile.GetProfilePath(profileName);
            string appName = Path.GetFileName(exePath);

            if (!File.Exists(profilePath)) return;
            try
            {
                string content = File.ReadAllText(profilePath);
                if (!content.Contains("[Interface]")) { LogToTerminal("Invalid config.", Theme.Error); return; }

                var regex = new Regex(@"AllowedApps\s*=\s*(.*)", RegexOptions.IgnoreCase);
                var match = regex.Match(content);
                string newContent;

                if (match.Success)
                {
                    string currentApps = match.Groups[1].Value.Trim();
                    if (currentApps.Contains(appName)) { LogToTerminal($"{appName} is already added.", Color.Yellow); return; }
                    newContent = content.Replace(match.Value, $"AllowedApps = {currentApps}, {appName}");
                }
                else
                {
                    int i = content.IndexOf("[Interface]", StringComparison.OrdinalIgnoreCase);
                    int eol = content.IndexOf(Environment.NewLine, i);
                    if (eol == -1) eol = content.Length;
                    newContent = content.Insert(eol, $"{Environment.NewLine}AllowedApps = {appName}");
                }
                File.WriteAllText(profilePath, newContent);
                LogToTerminal($"Added '{appName}' to tunnel.", Theme.Accent);

                if (_currentState == ConnectionState.Connected)
                {
                    _wiresock.Disconnect(); Thread.Sleep(500); _wiresock.Connect(profileName);
                }
            }
            catch (Exception ex) { LogToTerminal($"Failed to add app: {ex.Message}", Theme.Error); }
        }

        private void ImportConfig(string filePath)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                File.Copy(filePath, Path.Combine(Global.ConfigsFolder, fileName), true);
                LoadProfilesToCombo();
                _profileSelector.SelectedItem = Path.GetFileNameWithoutExtension(fileName);
                LogToTerminal($"Config imported: {fileName}", Theme.Text);
            }
            catch (Exception ex) { LogToTerminal($"Import error: {ex.Message}", Theme.Error); }
        }

        // ═══════════════════════════════════════════════════════════════
        //  PROFILE ACTIONS
        // ═══════════════════════════════════════════════════════════════

        private void OnAddProfileClick(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog { Title = "Import Config", Filter = "WireGuard Config (*.conf)|*.conf", RestoreDirectory = true })
                if (dlg.ShowDialog() == DialogResult.OK) ImportConfig(dlg.FileName);
        }

        private void OnGenerateClick(object sender, EventArgs e) { using (var dlg = new WarpBotDialog()) dlg.ShowDialog(this); }

        private void OnEditProfileClick(object sender, EventArgs e)
        {
            if (_profileSelector.SelectedItem == null || _profileSelector.SelectedItem.ToString() == "No Configs") return;
            string profileName = _profileSelector.SelectedItem.ToString();
            string profilePath = Profile.GetProfilePath(profileName);
            if (!File.Exists(profilePath)) return;

            using (var dlg = new FrmEditor(profileName, File.ReadAllText(profilePath)))
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(profilePath, dlg.ResultContent);
                    LogToTerminal($"Profile '{profileName}' updated.", Color.Yellow);
                }
            }
        }

        // МЕХАНИЗМ УДАЛЕНИЯ
        private void OnDeleteProfileClick(object sender, EventArgs e)
        {
            if (_profileSelector.SelectedItem == null || _profileSelector.SelectedItem.ToString() == "No Configs") return;

            string profileName = _profileSelector.SelectedItem.ToString();

            // Подтверждение
            var result = MessageBox.Show($"Are you sure you want to delete profile '{profileName}'?", "Delete Config", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                // Защита от удаления прямо во время работы ВПН
                if (_currentState != ConnectionState.Disconnected && _wiresock.ProfileName == profileName)
                {
                    MessageBox.Show("Please disconnect from the VPN before deleting the active profile.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                try
                {
                    string profilePath = Profile.GetProfilePath(profileName);
                    if (File.Exists(profilePath))
                    {
                        File.Delete(profilePath);
                        LogToTerminal($"Profile '{profileName}' deleted.", Theme.Warn);
                        LoadProfilesToCombo();
                        OpenLogs();
                    }
                }
                catch (Exception ex) { LogToTerminal($"Failed to delete profile: {ex.Message}", Theme.Error); }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  CONNECTION LOGIC
        // ═══════════════════════════════════════════════════════════════

        // ФИКС ЗАВИСАНИЯ (Асинхронный метод)
        private async void OnToggleChanged(object sender, EventArgs e)
        {
            if (_profileSelector.SelectedItem == null || _profileSelector.SelectedItem.ToString() == "No Configs")
            {
                if (_toggleBtn.Checked) _toggleBtn.Checked = false;
                return;
            }

            string profile = _profileSelector.SelectedItem.ToString();

            if (_toggleBtn.Checked)
            {
                if (_currentState != ConnectionState.Connected && _currentState != ConnectionState.Connecting)
                {
                    UpdateState(ConnectionState.Connecting);

                    // Микро-пауза для UI: дает интерфейсу плавно нарисовать анимацию тумблера до того, как система нагрузится
                    await Task.Delay(50);

                    bool isElevated = IsCurrentProcessElevated();
                    bool useAdapter = Settings.Default.UseAdapter;

                    // Запускаем всё тяжелое в фоне
                    await Task.Run(() =>
                    {
                        if (!_zapret.IsRunning)
                        {
                            this.Invoke((Action)(() => LogToTerminal("Activating DPI Bypass...", Theme.Cyan)));
                            if (!_zapret.Start())
                            {
                                this.Invoke((Action)(() => { _toggleBtn.Checked = false; LogToTerminal("DPI Bypass failed to start.", Theme.Warn); }));
                            }
                        }

                        _wiresock.TunnelMode = isElevated && useAdapter ? WireSockManager.Mode.VirtualAdapter : WireSockManager.Mode.Transparent;

                        if (!_wiresock.Connect(profile))
                        {
                            this.Invoke((Action)(() => { _toggleBtn.Checked = false; LogToTerminal("Connection request failed.", Theme.Error); _zapret.Stop(); }));
                        }
                    });
                }
            }
            else
            {
                UpdateState(ConnectionState.Disconnected);

                await Task.Run(() =>
                {
                    if (_zapret.IsRunning)
                    {
                        this.Invoke((Action)(() => LogToTerminal("Deactivating DPI Bypass...", Theme.TextDim)));
                        _zapret.Stop();
                    }
                });
            }
        }

        private void LoadProfilesToCombo()
        {
            string currentSelection = _profileSelector.SelectedItem?.ToString();
            _profileSelector.Items.Clear();
            var profiles = Profile.GetProfiles().ToList();

            if (profiles.Count > 0)
            {
                _profileSelector.Items.AddRange(profiles.ToArray());
                _profileSelector.SelectedItem = profiles.Contains(currentSelection) ? currentSelection : (profiles.Contains(Settings.Default.LastProfile) ? Settings.Default.LastProfile : profiles[0]);
                _toggleBtn.Enabled = true; LockControls(false);
            }
            else
            {
                _profileSelector.Items.Add("No Configs"); _profileSelector.SelectedIndex = 0;
                _toggleBtn.Enabled = false; LockControls(true);
            }
        }

        private void UpdateState(ConnectionState state, bool notify = true)
        {
            _currentState = state;
            Action updateAction = () => {
                switch (state)
                {
                    case ConnectionState.Connecting:
                        _statusLabel.SetText("CONNECTING", Theme.Warn);
                        _statusHint.SetText("establishing tunnel...", Theme.Warn);
                        if (_heroCard != null) { _heroCard.AccentColor = Theme.Warn; _heroCard.AccentIntensity = 0.55f; }
                        _toggleBtn.Checked = true; LockControls(true); LogToTerminal("Initiating connection...", Theme.Warn);
                        if (!_tunnelConnectionWorker.IsBusy) _tunnelConnectionWorker.RunWorkerAsync();
                        break;
                    case ConnectionState.Connected:
                        _statusLabel.SetText("CONNECTED", Theme.Accent);
                        _statusHint.SetText("tunnel active", Theme.Accent);
                        if (_heroCard != null) { _heroCard.AccentColor = Theme.Accent; _heroCard.AccentIntensity = 0.85f; }
                        _toggleBtn.Checked = true; LockControls(true);
                        Settings.Default.LastProfile = _wiresock.ProfileName; Settings.Default.Save();
                        if (!_tunnelStateWorker.IsBusy) _tunnelStateWorker.RunWorkerAsync();
                        _ipLabel.ForeColor = Theme.TextDim; LogToTerminal("Connection established.", Theme.Accent);
                        _sessionStartTime = DateTime.Now; _sessionTimer.Start(); _lblSessionTimer.Visible = true;
                        ShowTrayNotification("ThanksRKN Connected", $"Profile: {_wiresock.ProfileName}", ToolTipIcon.Info);
                        if (!_hasShownPromoNotification)
                        {
                            var promoTimer = new System.Windows.Forms.Timer { Interval = 10000 };
                            promoTimer.Tick += (s, args) => { ShowTrayNotification("Присоединяйтесь к нам!", "Discord & Telegram — жми на эти кнопочки.", ToolTipIcon.Info); _hasShownPromoNotification = true; promoTimer.Stop(); promoTimer.Dispose(); };
                            promoTimer.Start();
                        }
                        break;
                    case ConnectionState.Disconnected:
                        _statusLabel.SetText("DISCONNECTED", Theme.TextDim);
                        _statusHint.SetText("select profile and connect", Theme.TextDim);
                        if (_heroCard != null) { _heroCard.AccentColor = Theme.Border; _heroCard.AccentIntensity = 0f; }
                        _toggleBtn.Checked = false; _ipLabel.Text = "tap toggle to connect"; _ipLabel.ForeColor = Theme.TextDim;
                        LockControls(false);
                        if (_tunnelStateWorker != null && _tunnelStateWorker.IsBusy) _tunnelStateWorker.CancelAsync();
                        _wiresock.Disconnect();
                        LogToTerminal("Disconnected.", Theme.TextDim); _sessionTimer.Stop(); _lblSessionTimer.Visible = false;
                        break;
                }
            };
            if (this.InvokeRequired) this.Invoke(updateAction); else updateAction();
        }

        private void LockControls(bool locked)
        {
            if (_btnAddProfile != null) _btnAddProfile.Enabled = !locked;
            if (_btnEditProfile != null) _btnEditProfile.Enabled = !locked;
            if (_btnGenerate != null) _btnGenerate.Enabled = !locked;
            if (_btnDeleteProfile != null) _btnDeleteProfile.Enabled = !locked;
            if (_profileSelector != null) _profileSelector.Enabled = !locked;
        }

        private BackgroundWorker InitializeTunnelConnectionWorker()
        {
            var worker = new BackgroundWorker { WorkerSupportsCancellation = true, WorkerReportsProgress = true };
            worker.DoWork += (s, e) => {
                for (int i = 0; i < 20 && !worker.CancellationPending && !_wiresock.Connected; i++) { Thread.Sleep(500); worker.ReportProgress(0, _wiresock.Connected); }
            };
            worker.ProgressChanged += (s, e) => { if ((bool)e.UserState) UpdateState(ConnectionState.Connected); };
            return worker;
        }

        private BackgroundWorker InitTunnelStateWorker()
        {
            var worker = new BackgroundWorker { WorkerSupportsCancellation = true, WorkerReportsProgress = true };
            worker.DoWork += (s, e) => { while (!worker.CancellationPending) { Thread.Sleep(1000); if (_wiresock.Connected) worker.ReportProgress(0, _wiresock.GetState()); } };
            worker.ProgressChanged += (s, e) => { if (e.UserState is WgbStats stats) _ipLabel.Text = $"↓ {stats.rx_bytes.AsHumanReadable()}   ↑ {stats.tx_bytes.AsHumanReadable()}\nRTT: {stats.estimated_rtt} ms"; };
            return worker;
        }

        // ═══════════════════════════════════════════════════════════════
        //  SYSTEM UTILS
        // ═══════════════════════════════════════════════════════════════

        private static bool IsCurrentProcessElevated() { using (var identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
        private void RestartAsAdmin() { try { Process.Start(new ProcessStartInfo { UseShellExecute = true, FileName = Application.ExecutablePath, Verb = "runas" }); Environment.Exit(1); } catch { } }
        private static bool IsApplicationAlreadyRunning() { Global.AlreadyRunning = new Mutex(true, "Global\\WiresockClientService", out var createdNew); if (createdNew) return false; Global.AlreadyRunning.Dispose(); return true; }
        private static string GetStartupRegistryKey() => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private bool IsInStartup() { try { using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GetStartupRegistryKey(), false)) return key?.GetValue("ThanksRKN") != null; } catch { return false; } }

        private void SetStartup(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GetStartupRegistryKey(), true))
                {
                    if (enable) { key?.SetValue("ThanksRKN", $"\"{Application.ExecutablePath}\" --minimized"); LogToTerminal("Autostart enabled.", Theme.Accent); }
                    else { key?.DeleteValue("ThanksRKN", false); LogToTerminal("Autostart disabled.", Theme.TextDim); }
                }
            }
            catch (Exception ex) { LogToTerminal($"Autostart error: {ex.Message}", Theme.Error); }
        }

        private static void SafeOpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* swallow — OS may block external launch */ }
        }

        private void OnAutostartChanged(object sender, EventArgs e) => SetStartup(_chkAutostart.Checked);
        private void OnSessionTimerTick(object sender, EventArgs e) { if (_currentState == ConnectionState.Connected) _lblSessionTimer.Text = $"{(DateTime.Now - _sessionStartTime):hh\\:mm\\:ss}"; }
        private void ShowTrayNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info) => _trayIcon?.ShowBalloonTip(4000, title, message, icon);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  THEME
    // ═══════════════════════════════════════════════════════════════════

    internal static class Theme
    {
        // Background
        public static readonly Color BgDeep = Color.FromArgb(10, 10, 14);
        public static readonly Color BgTop = Color.FromArgb(15, 16, 24);
        public static readonly Color BgBottom = Color.FromArgb(8, 8, 14);

        // Surfaces
        public static readonly Color Surface = Color.FromArgb(22, 22, 28);
        public static readonly Color SurfaceDeep = Color.FromArgb(16, 16, 22);
        public static readonly Color CardTop = Color.FromArgb(28, 28, 38);
        public static readonly Color CardBottom = Color.FromArgb(18, 18, 26);
        public static readonly Color Border = Color.FromArgb(46, 46, 58);

        // Text
        public static readonly Color Text = Color.FromArgb(248, 250, 252);
        public static readonly Color TextDim = Color.FromArgb(150, 160, 178);

        // Brand / states
        public static readonly Color Accent = Color.FromArgb(16, 185, 129);     // Emerald
        public static readonly Color AccentDeep = Color.FromArgb(5, 150, 105);
        public static readonly Color Error = Color.FromArgb(244, 63, 94);       // Rose
        public static readonly Color ErrorDeep = Color.FromArgb(190, 18, 60);
        public static readonly Color Warn = Color.FromArgb(245, 158, 11);       // Amber
        public static readonly Color WarnDeep = Color.FromArgb(217, 119, 6);
        public static readonly Color Cyan = Color.FromArgb(56, 189, 248);
        public static readonly Color Discord = Color.FromArgb(88, 101, 242);
        public static readonly Color Telegram = Color.FromArgb(40, 168, 233);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CUSTOM UI ELEMENTS & UTILS
    // ═══════════════════════════════════════════════════════════════════

    public static class UIHelpers
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr changeInfo);

        [DllImport("shell32.dll")]
        public static extern void DragAcceptFiles(IntPtr hwnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] StringBuilder lpszFile, uint cch);

        [DllImport("shell32.dll")]
        public static extern void DragFinish(IntPtr hDrop);

        public static void ApplyDarkTitleBar(IntPtr handle)
        {
            try
            {
                if (Environment.OSVersion.Version.Major >= 10)
                {
                    int useImmersiveDarkMode = 1;
                    DwmSetWindowAttribute(handle, 20, ref useImmersiveDarkMode, sizeof(int));
                    DwmSetWindowAttribute(handle, 19, ref useImmersiveDarkMode, sizeof(int));
                }
            }
            catch { }
        }

        public static void ApplyMicaBackdrop(IntPtr handle)
        {
            // DWMWA_SYSTEMBACKDROP_TYPE = 38, value 2 (Mica) on Win 11. Silently no-op elsewhere.
            try
            {
                int backdrop = 2;
                DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
            }
            catch { }
        }

        public static GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(rect); return path; }
            int d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static float EaseOutCubic(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            float inv = 1f - t;
            return 1f - inv * inv * inv;
        }
    }

    // Animated label that smoothly cross-fades ForeColor between targets.
    public class AnimatedColorLabel : Label
    {
        private Color _fromColor;
        private Color _targetColor;
        private float _animPos = 1f;
        private readonly System.Windows.Forms.Timer _timer;

        public AnimatedColorLabel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            _fromColor = base.ForeColor;
            _targetColor = base.ForeColor;
            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += OnTick;
        }

        private void OnTick(object sender, EventArgs e)
        {
            _animPos += 0.075f;
            if (_animPos >= 1f) { _animPos = 1f; _timer.Stop(); }
            base.ForeColor = UIHelpers.Blend(_fromColor, _targetColor, UIHelpers.EaseOutCubic(_animPos));
        }

        public void SetText(string text, Color color)
        {
            base.Text = text;
            _fromColor = base.ForeColor;
            _targetColor = color;
            _animPos = 0f;
            _timer.Start();
        }
    }

    // Label whose text is rendered with a vertical gradient.
    public class GradientLabel : Label
    {
        public Color ColorTop { get; set; } = Color.White;
        public Color ColorBottom { get; set; } = Color.LightGray;

        public GradientLabel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var path = new GraphicsPath())
            {
                if (string.IsNullOrEmpty(Text)) return;
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    var rect = new RectangleF(0, 0, Width, Height);
                    path.AddString(Text, Font.FontFamily, (int)Font.Style, g.DpiY * Font.SizeInPoints / 72f, rect, sf);
                    using (var brush = new LinearGradientBrush(ClientRectangle, ColorTop, ColorBottom, LinearGradientMode.Vertical))
                        g.FillPath(brush, path);
                }
            }
        }
    }

    // Smoothly animated rounded panel with two-stop linear gradient + accent border.
    // Paints into a cached bitmap so transparent children see the painted gradient correctly
    // and form-level invalidations stay cheap.
    public class GradientPanel : Panel
    {
        public int Radius { get; set; } = 16;
        public int BorderThickness { get; set; } = 1;

        private Color _colorTop = Color.FromArgb(28, 28, 38);
        private Color _colorBottom = Color.FromArgb(18, 18, 26);
        private Color _borderColor = Color.FromArgb(46, 46, 58);
        private Color _accentColor = Color.FromArgb(16, 185, 129);
        private float _accentIntensity = 0f;
        private Bitmap _cache;
        private bool _cacheDirty = true;

        public Color ColorTop { get { return _colorTop; } set { if (_colorTop != value) { _colorTop = value; _cacheDirty = true; Invalidate(); } } }
        public Color ColorBottom { get { return _colorBottom; } set { if (_colorBottom != value) { _colorBottom = value; _cacheDirty = true; Invalidate(); } } }
        public Color BorderColor { get { return _borderColor; } set { if (_borderColor != value) { _borderColor = value; _cacheDirty = true; Invalidate(); } } }
        public Color AccentColor { get { return _accentColor; } set { if (_accentColor != value) { _accentColor = value; _cacheDirty = true; Invalidate(); } } }
        public float AccentIntensity { get { return _accentIntensity; } set { if (_accentIntensity != value) { _accentIntensity = value; _cacheDirty = true; Invalidate(); } } }

        public GradientPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // Solid fallback so any sliver outside the rounded path matches the parent.
            BackColor = Color.FromArgb(8, 8, 14);
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); _cacheDirty = true; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _cache?.Dispose(); _cache = null; }
            base.Dispose(disposing);
        }

        private void RebuildCache()
        {
            _cache?.Dispose();
            if (Width <= 0 || Height <= 0) { _cache = null; _cacheDirty = false; return; }
            _cache = new Bitmap(Width, Height); // transparent by default
            using (var g = Graphics.FromImage(_cache))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = UIHelpers.GetRoundedPath(rect, Radius))
                {
                    using (var brush = new LinearGradientBrush(rect, _colorTop, _colorBottom, LinearGradientMode.Vertical))
                        g.FillPath(brush, path);
                    int hh = Math.Max(1, rect.Height / 2);
                    using (var hl = new LinearGradientBrush(new Rectangle(rect.X, rect.Y, rect.Width, hh), Color.FromArgb(14, 255, 255, 255), Color.Transparent, LinearGradientMode.Vertical))
                    {
                        var clip = g.Clip;
                        g.SetClip(path);
                        g.FillRectangle(hl, new Rectangle(rect.X, rect.Y, rect.Width, hh));
                        g.Clip = clip;
                    }
                    Color borderColor = UIHelpers.Blend(_borderColor, _accentColor, _accentIntensity);
                    if (BorderThickness > 0)
                        using (var pen = new Pen(borderColor, BorderThickness)) g.DrawPath(pen, path);
                    if (_accentIntensity > 0.05f)
                    {
                        using (var pen = new Pen(Color.FromArgb((int)(60 * _accentIntensity), _accentColor), 1.5f))
                        {
                            var inner = new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
                            using (var p = UIHelpers.GetRoundedPath(inner, Math.Max(1, Radius - 1))) g.DrawPath(pen, p);
                        }
                    }
                }
            }
            _cacheDirty = false;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Clear with parent's BackColor so rounded corners blend cleanly.
            Color clear = Parent != null ? Parent.BackColor : BackColor;
            if (clear.A < 255) clear = BackColor;
            using (var b = new SolidBrush(clear)) e.Graphics.FillRectangle(b, ClientRectangle);

            if (_cacheDirty || _cache == null) RebuildCache();
            if (_cache != null) e.Graphics.DrawImageUnscaled(_cache, 0, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Everything is painted in OnPaintBackground for transparency support.
        }
    }

    // Animated gradient toggle with glow halo.
    public class GlowToggle : CheckBox
    {
        private System.Windows.Forms.Timer _animTimer;
        private float _animPos = 0f;
        private float _glowAnim = 0f;

        public GlowToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Appearance = Appearance.Button; AutoSize = false; Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            _animTimer = new System.Windows.Forms.Timer { Interval = 14 };
            _animTimer.Tick += (s, e) => {
                float target = Checked ? 1f : 0f;
                _animPos += (target - _animPos) * 0.28f;
                _glowAnim += (target - _glowAnim) * 0.18f;
                if (Math.Abs(target - _animPos) < 0.01f && Math.Abs(target - _glowAnim) < 0.01f)
                { _animPos = target; _glowAnim = target; _animTimer.Stop(); }
                Invalidate();
            };
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); _animTimer.Start(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;

            // Outer glow when ON
            if (_glowAnim > 0.02f && Enabled)
            {
                int glow = (int)(14 * _glowAnim);
                for (int i = glow; i > 0; i--)
                {
                    int alpha = (int)(60 * _glowAnim * (1f - (float)i / glow));
                    if (alpha < 1) continue;
                    using (var pen = new Pen(Color.FromArgb(Math.Min(255, alpha), Theme.Accent), 1f))
                    {
                        var r = new Rectangle(-i, 4 - i, Width + i * 2 - 1, Height - 9 + i * 2);
                        using (var p = UIHelpers.GetRoundedPath(r, (Height - 9) / 2 + i)) g.DrawPath(pen, p);
                    }
                }
            }

            var trackRect = new Rectangle(0, 4, Width - 1, Height - 9);

            // Track gradient
            Color trackOff1 = Color.FromArgb(45, 45, 55);
            Color trackOff2 = Color.FromArgb(30, 30, 40);
            Color trackOn1 = Color.FromArgb(34, 197, 200); // cyan
            Color trackOn2 = Theme.Accent;

            Color t1 = UIHelpers.Blend(trackOff1, trackOn1, _animPos);
            Color t2 = UIHelpers.Blend(trackOff2, trackOn2, _animPos);

            using (var path = UIHelpers.GetRoundedPath(trackRect, trackRect.Height / 2))
            using (var brush = new LinearGradientBrush(trackRect, t1, t2, LinearGradientMode.Horizontal))
            {
                g.FillPath(brush, path);
                // inner shading
                using (var pen = new Pen(Color.FromArgb(40, 0, 0, 0), 1f))
                    g.DrawPath(pen, path);
            }

            // Thumb
            float thumbSize = trackRect.Height - 6;
            float thumbX = trackRect.X + 3 + (trackRect.Width - thumbSize - 6) * UIHelpers.EaseOutCubic(_animPos);
            float thumbY = trackRect.Y + 3;

            // Thumb shadow
            using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                g.FillEllipse(shadow, thumbX + 1, thumbY + 2, thumbSize, thumbSize);

            // Thumb body
            using (var thumbPath = new GraphicsPath())
            {
                thumbPath.AddEllipse(thumbX, thumbY, thumbSize, thumbSize);
                using (var grad = new LinearGradientBrush(new RectangleF(thumbX, thumbY, thumbSize, thumbSize),
                    Color.White, Color.FromArgb(220, 220, 230), LinearGradientMode.Vertical))
                {
                    g.FillPath(Enabled ? (Brush)grad : new SolidBrush(Color.FromArgb(120, 120, 130)), thumbPath);
                }

                // Inner accent ring while ON
                if (_animPos > 0.02f)
                {
                    using (var pen = new Pen(Color.FromArgb((int)(120 * _animPos), Theme.AccentDeep), 1.4f))
                        g.DrawEllipse(pen, thumbX + 3, thumbY + 3, thumbSize - 6, thumbSize - 6);
                }
            }
        }
    }

    public class ModernCheckBox : CheckBox
    {
        public ModernCheckBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); Cursor = Cursors.Hand; BackColor = Color.Transparent; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            // Transparent background

            int boxSize = 18; var boxRect = new Rectangle(0, (Height - boxSize) / 2, boxSize, boxSize);

            using (var path = UIHelpers.GetRoundedPath(boxRect, 5))
            {
                if (Checked)
                {
                    using (var brush = new LinearGradientBrush(boxRect, Theme.Accent, Theme.AccentDeep, LinearGradientMode.Vertical))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(Color.Black, 2.5f))
                    {
                        g.DrawLine(pen, boxRect.X + 4, boxRect.Y + 9, boxRect.X + 8, boxRect.Y + 13);
                        g.DrawLine(pen, boxRect.X + 8, boxRect.Y + 13, boxRect.X + 13, boxRect.Y + 5);
                    }
                }
                else
                {
                    using (var brush = new LinearGradientBrush(boxRect, Color.FromArgb(45, 45, 55), Color.FromArgb(30, 30, 40), LinearGradientMode.Vertical))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(Color.FromArgb(80, 80, 95), 1f)) g.DrawPath(pen, path);
                }
            }
            using (var brush = new SolidBrush(ForeColor)) using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                g.DrawString(Text, Font, brush, new Rectangle(boxSize + 10, 0, Width - boxSize - 10, Height), sf);
        }
    }

    // Animated gradient button with optional outer glow on hover.
    public class GradientButton : Button
    {
        public Color BaseTop { get; set; } = Color.FromArgb(40, 40, 48);
        public Color BaseBottom { get; set; } = Color.FromArgb(32, 32, 40);
        public Color HoverTop { get; set; } = Color.FromArgb(60, 60, 70);
        public Color HoverBottom { get; set; } = Color.FromArgb(50, 50, 60);
        public Color HoverForeColor { get; set; } = Color.Empty;
        public int Radius { get; set; } = 10;
        public bool Glow { get; set; } = false;
        public Color GlowColor { get; set; } = Color.FromArgb(16, 185, 129);

        private float _hoverAmount = 0f;
        private bool _hovered = false;
        private bool _pressed = false;
        private System.Windows.Forms.Timer _animTimer;

        public GradientButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            _animTimer = new System.Windows.Forms.Timer { Interval = 14 };
            _animTimer.Tick += (s, e) => {
                float target = (_hovered || _pressed) ? 1f : 0f;
                float delta = target - _hoverAmount;
                if (Math.Abs(delta) < 0.015f) { _hoverAmount = target; _animTimer.Stop(); }
                else _hoverAmount += delta * 0.28f;
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; _animTimer.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _animTimer.Start(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { _pressed = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { _pressed = false; Invalidate(); base.OnMouseUp(mevent); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Glow under button
            if (Glow && _hoverAmount > 0.02f && Enabled)
            {
                int glow = (int)(8 * _hoverAmount);
                for (int i = glow; i > 0; i--)
                {
                    int alpha = (int)(45 * _hoverAmount * (1f - (float)i / glow));
                    if (alpha < 1) continue;
                    using (var pen = new Pen(Color.FromArgb(Math.Min(255, alpha), GlowColor), 1f))
                    {
                        var r = new Rectangle(-i, -i, Width + i * 2 - 1, Height + i * 2 - 1);
                        using (var p = UIHelpers.GetRoundedPath(r, Radius + i)) g.DrawPath(pen, p);
                    }
                }
            }

            float ease = UIHelpers.EaseOutCubic(_hoverAmount);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color top = UIHelpers.Blend(BaseTop, HoverTop, ease);
            Color bot = UIHelpers.Blend(BaseBottom, HoverBottom, ease);
            if (_pressed) { top = UIHelpers.Blend(top, Color.Black, 0.20f); bot = UIHelpers.Blend(bot, Color.Black, 0.20f); }
            if (!Enabled) { top = Color.FromArgb(28, 28, 34); bot = Color.FromArgb(22, 22, 28); }

            using (var path = UIHelpers.GetRoundedPath(rect, Radius))
            {
                using (var brush = new LinearGradientBrush(rect, top, bot, LinearGradientMode.Vertical))
                    g.FillPath(brush, path);

                // Subtle highlight at top half
                int hlAlpha = (int)((22 + 10 * (1 - ease)) * (Enabled ? 1 : 0.3));
                using (var hl = new LinearGradientBrush(new Rectangle(rect.X, rect.Y, rect.Width, Math.Max(1, rect.Height / 2)), Color.FromArgb(hlAlpha, 255, 255, 255), Color.Transparent, LinearGradientMode.Vertical))
                {
                    var clip = g.Clip;
                    g.SetClip(path);
                    g.FillRectangle(hl, new Rectangle(rect.X, rect.Y, rect.Width, Math.Max(1, rect.Height / 2)));
                    g.Clip = clip;
                }

                // Light border
                using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0), 1f)) g.DrawPath(pen, path);
            }

            Color baseFg = Enabled ? ForeColor : Color.FromArgb(100, 100, 110);
            Color hoverFg = HoverForeColor.IsEmpty ? baseFg : HoverForeColor;
            Color textColor = UIHelpers.Blend(baseFg, hoverFg, ease);

            TextRenderer.DrawText(g, Text, Font, rect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DIALOGS
    // ═══════════════════════════════════════════════════════════════════

    public class WarpBotDialog : Form
    {
        public WarpBotDialog()
        {
            Text = "WARP Config";
            ClientSize = new Size(420, 400);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BgDeep;
            Font = new Font("Segoe UI", 9f);

            var bg = new GradientPanel
            {
                Dock = DockStyle.Fill,
                ColorTop = Theme.BgTop,
                ColorBottom = Theme.BgBottom,
                Radius = 0,
                BorderThickness = 0
            };

            var lblIcon = new Label { Text = "⚡", Font = new Font("Segoe UI", 36), ForeColor = Theme.Warn, Dock = DockStyle.Top, Height = 80, TextAlign = ContentAlignment.BottomCenter, BackColor = Color.Transparent };
            var lblTitle = new Label { Text = "Генерация конфига через Telegram", Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = Theme.Text, Dock = DockStyle.Top, Height = 42, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent };

            var pnlStepsWrap = new Panel { Dock = DockStyle.Top, Height = 160, Padding = new Padding(28, 14, 28, 8), BackColor = Color.Transparent };
            var pnlSteps = new GradientPanel
            {
                Dock = DockStyle.Fill,
                ColorTop = Color.FromArgb(26, 26, 36),
                ColorBottom = Color.FromArgb(18, 18, 28),
                Radius = 12,
                BorderThickness = 1,
                BorderColor = Theme.Border
            };

            string[] steps = { "1. Откройте бота @ThanksRKNWarp_bot", "2. Нажмите «Создать конфиг ThanksRKN»", "3. Скачайте файл .conf ", "4. В основном меню нажмите + ADD" };
            int y = 18;
            foreach (var step in steps)
            {
                pnlSteps.Controls.Add(new Label
                {
                    Text = step,
                    Font = new Font("Segoe UI", 9.75f),
                    ForeColor = Color.FromArgb(200, 205, 220),
                    Location = new Point(18, y),
                    AutoSize = true,
                    BackColor = Color.Transparent
                });
                y += 26;
            }
            pnlStepsWrap.Controls.Add(pnlSteps);

            var pnlBtn = new Panel { Dock = DockStyle.Top, Height = 90, Padding = new Padding(28, 24, 28, 24), BackColor = Color.Transparent };
            var btnOpen = new GradientButton
            {
                Text = "Открыть бота @ThanksRKNWarp_bot",
                Dock = DockStyle.Fill,
                BaseTop = Color.FromArgb(56, 189, 248),
                BaseBottom = Color.FromArgb(14, 165, 233),
                HoverTop = Color.FromArgb(96, 209, 255),
                HoverBottom = Color.FromArgb(40, 180, 240),
                ForeColor = Color.White,
                HoverForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Radius = 10,
                Glow = true,
                GlowColor = Color.FromArgb(56, 189, 248)
            };
            btnOpen.Click += (s, ev) => { try { Process.Start(new ProcessStartInfo("https://t.me/ThanksRKNWarp_bot") { UseShellExecute = true }); } catch { } };
            pnlBtn.Controls.Add(btnOpen);

            bg.Controls.Add(pnlBtn);
            bg.Controls.Add(pnlStepsWrap);
            bg.Controls.Add(lblTitle);
            bg.Controls.Add(lblIcon);
            Controls.Add(bg);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UIHelpers.ApplyDarkTitleBar(this.Handle); }
    }

    public class FrmEditor : Form
    {
        public string ResultContent { get; private set; }
        private readonly TextBox _txtContent;

        public FrmEditor(string profileName, string initialContent)
        {
            Text = $"Edit: {profileName}";
            ClientSize = new Size(560, 620);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BgDeep;
            Font = new Font("Segoe UI", 9f);

            var bg = new GradientPanel
            {
                Dock = DockStyle.Fill,
                ColorTop = Theme.BgTop,
                ColorBottom = Theme.BgBottom,
                Radius = 0,
                BorderThickness = 0
            };

            _txtContent = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10.5f),
                BackColor = Color.FromArgb(20, 20, 28),
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                Text = initialContent.Replace("\n", Environment.NewLine).Replace(Environment.NewLine + Environment.NewLine, Environment.NewLine)
            };

            var pnlEditorWrapper = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18), BackColor = Color.Transparent };
            pnlEditorWrapper.Controls.Add(_txtContent);

            var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 70, BackColor = Color.Transparent, Padding = new Padding(18, 0, 18, 18) };
            var btnSave = new GradientButton
            {
                Text = "💾  SAVE CONFIG",
                DialogResult = DialogResult.OK,
                Dock = DockStyle.Right,
                Width = 160,
                BaseTop = Theme.Accent,
                BaseBottom = Theme.AccentDeep,
                HoverTop = Color.FromArgb(50, 220, 165),
                HoverBottom = Color.FromArgb(10, 170, 120),
                ForeColor = Color.Black,
                HoverForeColor = Color.Black,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
                Radius = 8,
                Glow = true,
                GlowColor = Theme.Accent
            };
            btnSave.Click += (s, e) => { ResultContent = _txtContent.Text; };
            pnlBottom.Controls.Add(btnSave);

            bg.Controls.Add(pnlEditorWrapper);
            bg.Controls.Add(pnlBottom);
            Controls.Add(bg);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UIHelpers.ApplyDarkTitleBar(this.Handle); }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  RENDERERS
    // ═══════════════════════════════════════════════════════════════════

    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = true; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Color.White; base.OnRenderItemText(e); }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) { base.OnRenderMenuItemBackground(e); return; }
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using (var path = UIHelpers.GetRoundedPath(rect, 6))
            using (var brush = new LinearGradientBrush(rect, Theme.Accent, Theme.AccentDeep, LinearGradientMode.Vertical))
                g.FillPath(brush, path);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            using (var brush = new LinearGradientBrush(e.AffectedBounds, Theme.CardTop, Theme.CardBottom, LinearGradientMode.Vertical))
                g.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(Theme.Border, 1))
                e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1));
        }
    }

    public class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.Accent;
        public override Color MenuItemSelectedGradientBegin => Theme.Accent;
        public override Color MenuItemSelectedGradientEnd => Theme.AccentDeep;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuBorder => Theme.Border;
        public override Color ToolStripDropDownBackground => Theme.CardTop;
        public override Color ImageMarginGradientBegin => Theme.CardTop;
        public override Color ImageMarginGradientMiddle => Theme.CardTop;
        public override Color ImageMarginGradientEnd => Theme.CardTop;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
