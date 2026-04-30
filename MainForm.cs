using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PlusControl.Pro
{
    public partial class MainForm : Form
    {
        // === DEPENDENCIAS ===
        private readonly IConfigurationService _configService;
        private readonly IHardwareMonitorService _hardwareService;
        private readonly IProcessManagerService _processService;
        private readonly IStatisticsService _statistics;

        // === ESTADO ===
        private CancellationTokenSource? _debounceToken;
        private bool _isExiting = false;
        private string _lastProcessName = "";
        private string _lastAppliedProfile = "";
        private bool _isStableAppConfigured = false;
        private int _currentBaseRank = 3;
        private int _currentActualRank = 3;
        private int _heatCounter = 0;
        private int _coolCounter = 0;
        private long? _tempAffinityActive = null;
        private long? _tempAffinityBg = null;

        // === CACHÉ DE ICONOS ===
        private readonly Dictionary<string, Image> _iconCache = new();

        // === CONTROLES UI ===
        private System.Windows.Forms.Timer? _monitorTimer;
        private NotifyIcon? _trayIcon;
        private Label? _lblStatus, _lblTempStatus, _lblStats;

        // Controles de Pestañas
        private Panel? _panelConfig, _panelReglas;
        private Button? _btnTabConfig, _btnTabReglas;

        // Controles de Configuración
        private TextBox? _txtProceso;
        private ComboBox? _cmbPerfil, _cmbPrioridad, _cmbPrioridadFondo, _cmbDefaultProfile;
        private Button? _btnAdd, _btnBrowse, _btnAffinity;
        private CheckBox? _chkEnableTemp;
        private NumericUpDown? _numTempHigh, _numSecHigh, _numTempLow, _numSecLow;

        // Controles de Reglas (Lista Moderna)
        private FlowLayoutPanel? _flowReglas;

        private static readonly ObjectPool<Bitmap> BitmapPool = new ObjectPool<Bitmap>(16, () => new Bitmap(16, 16));

        private static readonly Dictionary<string, string> ProfileOptions = new()
        {
            {"MAX_PERFORMANCE / Perfil 1", "%{NUMPAD1}"},
            {"GAMING_ULTRA / Perfil 2", "%{NUMPAD2}"},
            {"BALANCED_TURBO / Perfil 3", "%{NUMPAD3}"},
            {"BATTERY_OPTIMIZED / Perfil 4", "%{NUMPAD4}"}
        };

        private static readonly Dictionary<string, ProcessPriorityClass?> PriorityOptions = new()
        {
            {"Sin cambios", null}, {"Tiempo Real", ProcessPriorityClass.RealTime},
            {"Alta", ProcessPriorityClass.High}, {"Por encima normal", ProcessPriorityClass.AboveNormal},
            {"Normal", ProcessPriorityClass.Normal}, {"Por debajo normal", ProcessPriorityClass.BelowNormal},
            {"Baja", ProcessPriorityClass.Idle}
        };

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        public MainForm()
        {
            var basePath = Application.StartupPath;
            _configService = new ConfigurationService(basePath);
            _hardwareService = new HardwareMonitorService();
            _processService = new ProcessManagerService();
            _statistics = new StatisticsService(_configService);

            _configService.OnConfigChanged += OnConfigChanged;

            ConfigureDarkTheme();
            ConfigureSystemTray();
            InitializeTimer();

            this.Text = "PlusControl PRO v2.0";
            this.TopMost = _configService.Config.SiempreArriba;
            this.Opacity = _configService.Config.OpacidadVentana / 100.0;

            this.Load += MainForm_Load;
            this.FormClosing += MainForm_FormClosing;
        }

        private void SetApplicationPriority()
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime; }
            catch { try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { } }
        }

        private void InitializeTimer()
        {
            _monitorTimer = new System.Windows.Forms.Timer { Interval = _configService.Config.TickRate > 0 ? _configService.Config.TickRate : 1000 };
            _monitorTimer.Tick += MonitorTick;
            _monitorTimer.Start();

            Task.Run(FocusLoop);
        }

        private async Task FocusLoop()
        {
            while (!_isExiting)
            {
                try
                {
                    var foregroundProc = _processService.GetForegroundProcess();
                    if (foregroundProc != null && foregroundProc.ProcessName != _lastProcessName)
                    {
                        HandleProcessChangeFast(foregroundProc, foregroundProc.ProcessName);
                    }
                }
                catch { }
                await Task.Delay(50);
            }
        }

        private async void MonitorTick(object? sender, EventArgs e)
        {
            _monitorTimer?.Stop();
            try
            {
                await Task.Run(() => _hardwareService.Update());
                var temp = _hardwareService.CurrentTemperature;
                UpdateTemperatureDisplay(temp);
                _statistics.RecordTemperature(temp);

                if (_configService.Config.ControlTermico.Enabled && _isStableAppConfigured && temp > 0)
                    ProcessThermalControl(temp);

                UpdateTrayIcon(temp);
            }
            catch { }
            finally { _monitorTimer?.Start(); }
        }

        private void HandleProcessChangeFast(ProcessInfo process, string currentProcess)
        {
            this.InvokeIfRequired(() => _lblStatus!.Text = $"Focus: [{currentProcess}] -> Evaluando...");

            if (!string.IsNullOrEmpty(_lastProcessName) && _configService.Config.Reglas.ContainsKey(_lastProcessName))
                _processService.ApplyBackgroundState(_lastProcessName, _configService.Config.Reglas[_lastProcessName]);

            _lastProcessName = currentProcess;
            _isStableAppConfigured = false;
            _heatCounter = 0;
            _coolCounter = 0;

            _statistics.RecordProcessDetected(currentProcess);

            _debounceToken?.Cancel();
            _debounceToken = new CancellationTokenSource();
            var token = _debounceToken.Token;

            string targetHotkey = _configService.Config.AtajoDefecto;
            bool isManagedRule = false;

            if (_configService.Config.Reglas.TryGetValue(currentProcess, out var rule))
            {
                targetHotkey = rule.Atajo;
                isManagedRule = true;
            }

            int delay = _configService.Config.DelayGeneral;
            if ((_lastAppliedProfile == "%{NUMPAD1}" && targetHotkey == "%{NUMPAD2}") ||
                (_lastAppliedProfile == "%{NUMPAD2}" && targetHotkey == "%{NUMPAD1}"))
            {
                delay = _configService.Config.Delay1y2;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token);
                    if (!token.IsCancellationRequested)
                        this.InvokeIfRequired(() => ApplyBaseProfile(process, targetHotkey, isManagedRule));
                }
                catch (TaskCanceledException) { }
            }, token);
        }

        private void ApplyBaseProfile(ProcessInfo process, string hotkey, bool isManagedRule)
        {
            PerfilConfig? config = _configService.Config.Reglas.ContainsKey(process.ProcessName)
                ? _configService.Config.Reglas[process.ProcessName] : null;

            _isStableAppConfigured = config != null;

            _processService.ApplyProfile(process, config ?? new PerfilConfig { Atajo = hotkey }, isManagedRule);

            _currentBaseRank = HotkeyToRank(hotkey);
            _currentActualRank = _currentBaseRank;
            _heatCounter = 0; _coolCounter = 0;

            if (hotkey != _lastAppliedProfile)
            {
                _processService.AplicarPerfilHardware(hotkey);
                _lastAppliedProfile = hotkey;
                _statistics.RecordProfileChange(process.ProcessName, hotkey);
                ShowNotification($"Perfil: {GetProfileName(hotkey)}", process.ProcessName);
            }

            UpdateStatusLabel(process.ProcessName);
            UpdateTrayIcon(_hardwareService.CurrentTemperature);
        }

        private void ProcessThermalControl(float temp)
        {
            var thermalCfg = _configService.Config.ControlTermico;

            if (temp >= thermalCfg.TempAlta) { _heatCounter++; _coolCounter = 0; }
            else if (temp <= thermalCfg.TempBaja) { _coolCounter++; _heatCounter = 0; }
            else { _heatCounter = 0; _coolCounter = 0; }

            if (_heatCounter >= thermalCfg.SegundosAlta && _currentActualRank < 4)
            {
                _heatCounter = 0; _currentActualRank++;
                var newHotkey = RankToHotkey(_currentActualRank);
                _processService.AplicarPerfilHardware(newHotkey);
                _lastAppliedProfile = newHotkey;
                UpdateStatusLabel(_lastProcessName, " ⚠️ Por Calor");
                ShowNotification("⚠️ Temperatura Alta", $"Reduciendo a {GetProfileName(newHotkey)}");
            }
            else if (_coolCounter >= thermalCfg.SegundosBaja && _currentActualRank > _currentBaseRank)
            {
                _coolCounter = 0; _currentActualRank--;
                var newHotkey = RankToHotkey(_currentActualRank);
                _processService.AplicarPerfilHardware(newHotkey);
                _lastAppliedProfile = newHotkey;
                UpdateStatusLabel(_lastProcessName, " ✓ Recuperado");
                ShowNotification("✓ Temperatura Normal", $"Restaurando a {GetProfileName(newHotkey)}");
            }
        }

        private void OnConfigChanged(PlusControlData config)
        {
            this.InvokeIfRequired(() => {
                this.TopMost = config.SiempreArriba;
                this.Opacity = config.OpacidadVentana / 100.0;
                if (_monitorTimer != null) _monitorTimer.Interval = config.TickRate;
            });
        }

        private void MainForm_Load(object? sender, EventArgs e)
        {
            SetApplicationPriority();
            if (_configService.Config.IniciarMinimizado)
            {
                this.WindowState = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
                this.Hide();
            }
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!_isExiting && _configService.Config.CerrarMinimiza && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; this.WindowState = FormWindowState.Minimized; return;
            }

            _isExiting = true;
            if (_configService.Config.RestaurarAlSalir)
                foreach (var procName in _configService.Config.Reglas.Keys) _processService.RestoreProcess(procName);

            _monitorTimer?.Stop();
            _hardwareService.Dispose();
            _trayIcon?.Dispose();
            _configService.Save();
        }

        private void UpdateTemperatureDisplay(float temp)
        {
            this.InvokeIfRequired(() => {
                if (_lblTempStatus == null) return;
                if (temp > 0)
                {
                    _lblTempStatus.Text = $"🌡️ {temp:F1}°C";
                    _lblTempStatus.ForeColor = temp >= _configService.Config.ControlTermico.TempAlta ? Color.Salmon
                        : temp <= _configService.Config.ControlTermico.TempBaja ? Color.LightSkyBlue : Color.LightGray;
                }
                else
                {
                    _lblTempStatus.Text = "🌡️ N/D"; _lblTempStatus.ForeColor = Color.Gray;
                }
            });
        }

        private void UpdateStatusLabel(string process, string extra = "")
        {
            this.InvokeIfRequired(() => {
                if (_lblStatus != null) _lblStatus.Text = $"Focus: [{process}] → {GetProfileName(_lastAppliedProfile)} | {(_isStableAppConfigured ? "✓ Regla" : "○ Defecto")}{extra}";
            });
        }

        private void UpdateTrayIcon(float temp)
        {
            if (_trayIcon == null) return;
            var rankText = _currentActualRank.ToString();
            var iconColor = temp >= _configService.Config.ControlTermico.TempAlta ? Color.FromArgb(255, 80, 80)
                : temp <= _configService.Config.ControlTermico.TempBaja && temp > 0 ? Color.FromArgb(80, 200, 255) : Color.White;

            var bmp = BitmapPool.Get();
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    g.Clear(Color.Transparent);
                    using (var font = new Font("Tahoma", 10, FontStyle.Bold))
                    {
                        using (var shadow = new SolidBrush(Color.Black)) g.DrawString(rankText, font, shadow, new PointF(1, 1));
                        using (var brush = new SolidBrush(iconColor)) g.DrawString(rankText, font, brush, new PointF(0, 0));
                    }
                }
                var oldIcon = _trayIcon.Icon;
                _trayIcon.Icon = Icon.FromHandle(bmp.GetHicon());
                _trayIcon.Text = $"PlusControl PRO | P{_currentActualRank} | {temp:F0}°C";
                if (oldIcon != null && oldIcon != SystemIcons.Application) { DestroyIcon(oldIcon.Handle); oldIcon.Dispose(); }
            }
            finally { BitmapPool.Return(bmp); }
        }

        private void ShowNotification(string title, string message)
        {
            if (!_configService.Config.MostrarNotificaciones || _configService.Config.ModoSilencioso) return;
            _trayIcon?.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }

        #region === CONFIGURACIÓN DE UI (NUEVO DISEÑO CON PESTAÑAS Y CARDS) ===

        private void ConfigureDarkTheme()
        {
            var bgDark = Color.FromArgb(30, 30, 30);
            var bgPanel = Color.FromArgb(45, 45, 48);
            var textLight = Color.FromArgb(240, 240, 240);
            var accentBlue = Color.FromArgb(0, 122, 204);

            this.BackColor = bgDark;
            this.ForeColor = textLight;
            this.Size = new Size(720, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Font = new Font("Segoe UI", 9.5f);

            // Título
            var lblTitle = new Label { Text = "🖥️ PlusControl PRO", Location = new Point(20, 15), AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = accentBlue };
            this.Controls.Add(lblTitle);

            // Botón Ajustes
            var btnSettings = new Button { Text = "⚙️", Location = new Point(655, 13), Size = new Size(35, 35), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 14), BackColor = bgPanel, ForeColor = textLight, Cursor = Cursors.Hand };
            btnSettings.FlatAppearance.BorderSize = 0;
            btnSettings.Click += (s, e) => ShowAdvancedSettings();
            this.Controls.Add(btnSettings);

            // === BARRA DE NAVEGACIÓN (PESTAÑAS) ===
            var navPanel = new Panel { Location = new Point(20, 60), Size = new Size(665, 35), BackColor = bgPanel };

            _btnTabConfig = new Button { Text = "⚙️ Configurar Nueva Regla", FlatStyle = FlatStyle.Flat, Size = new Size(332, 35), Location = new Point(0, 0), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            _btnTabConfig.FlatAppearance.BorderSize = 0;
            _btnTabConfig.Click += (s, e) => SwitchTab(0);

            _btnTabReglas = new Button { Text = "📋 Procesos Registrados", FlatStyle = FlatStyle.Flat, Size = new Size(333, 35), Location = new Point(332, 0), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            _btnTabReglas.FlatAppearance.BorderSize = 0;
            _btnTabReglas.Click += (s, e) => SwitchTab(1);

            navPanel.Controls.Add(_btnTabConfig);
            navPanel.Controls.Add(_btnTabReglas);
            this.Controls.Add(navPanel);

            // === PANEL 1: CONFIGURACIÓN ===
            _panelConfig = new Panel { Location = new Point(0, 95), Size = new Size(720, 400), BackColor = bgDark };

            // Térmico
            var grpThermal = new GroupBox { Text = "🌡️ Control Térmico Inteligente", Location = new Point(20, 10), Size = new Size(665, 100), ForeColor = Color.LightGray, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            _chkEnableTemp = new CheckBox { Text = "Automático", Location = new Point(15, 25), AutoSize = true, Checked = _configService.Config.ControlTermico.Enabled };
            _chkEnableTemp.CheckedChanged += (s, e) => SaveCurrentConfig();
            grpThermal.Controls.Add(_chkEnableTemp);

            var cfg = _configService.Config.ControlTermico;
            grpThermal.Controls.Add(new Label { Text = "↓ Bajar si:", Location = new Point(15, 58), AutoSize = true });
            _numTempHigh = new NumericUpDown { Location = new Point(90, 55), Size = new Size(50, 23), BackColor = bgPanel, ForeColor = textLight, Value = cfg.TempAlta }; grpThermal.Controls.Add(_numTempHigh);
            grpThermal.Controls.Add(new Label { Text = "°C por", Location = new Point(145, 58), AutoSize = true });
            _numSecHigh = new NumericUpDown { Location = new Point(195, 55), Size = new Size(45, 23), BackColor = bgPanel, ForeColor = textLight, Value = cfg.SegundosAlta }; grpThermal.Controls.Add(_numSecHigh);
            grpThermal.Controls.Add(new Label { Text = "seg", Location = new Point(245, 58), AutoSize = true });

            grpThermal.Controls.Add(new Label { Text = "↑ Subir si:", Location = new Point(310, 58), AutoSize = true });
            _numTempLow = new NumericUpDown { Location = new Point(385, 55), Size = new Size(50, 23), BackColor = bgPanel, ForeColor = textLight, Value = cfg.TempBaja }; grpThermal.Controls.Add(_numTempLow);
            grpThermal.Controls.Add(new Label { Text = "°C por", Location = new Point(440, 58), AutoSize = true });
            _numSecLow = new NumericUpDown { Location = new Point(490, 55), Size = new Size(45, 23), BackColor = bgPanel, ForeColor = textLight, Value = cfg.SegundosBaja }; grpThermal.Controls.Add(_numSecLow);
            grpThermal.Controls.Add(new Label { Text = "seg", Location = new Point(540, 58), AutoSize = true });

            EventHandler saveHandler = (s, e) => SaveCurrentConfig();
            _numTempHigh.ValueChanged += saveHandler; _numSecHigh.ValueChanged += saveHandler; _numTempLow.ValueChanged += saveHandler; _numSecLow.ValueChanged += saveHandler;
            _panelConfig.Controls.Add(grpThermal);

            // Perfil Defecto
            _panelConfig.Controls.Add(new Label { Text = "Perfil por Defecto:", Location = new Point(20, 133), AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
            _cmbDefaultProfile = new ComboBox { Location = new Point(160, 130), Size = new Size(180, 25), BackColor = bgPanel, ForeColor = textLight, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var key in ProfileOptions.Keys) _cmbDefaultProfile.Items.Add(key);
            _cmbDefaultProfile.SelectedItem = GetProfileName(_configService.Config.AtajoDefecto);
            _cmbDefaultProfile.SelectedIndexChanged += (s, e) => { _configService.Config.AtajoDefecto = ProfileOptions[_cmbDefaultProfile.SelectedItem!.ToString()!]; SaveCurrentConfig(); };
            _panelConfig.Controls.Add(_cmbDefaultProfile);

            // Controles de Nueva Regla
            int yPos = 185;
            _panelConfig.Controls.Add(new Label { Text = "Crear / Editar Regla de Proceso:", Location = new Point(20, yPos), AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = accentBlue });

            yPos += 35;
            _panelConfig.Controls.Add(new Label { Text = "Nombre del Proceso:", Location = new Point(20, yPos + 3), AutoSize = true });
            _txtProceso = new TextBox { Location = new Point(155, yPos), Size = new Size(160, 25), BackColor = bgPanel, ForeColor = textLight, BorderStyle = BorderStyle.FixedSingle }; _panelConfig.Controls.Add(_txtProceso);
            _btnBrowse = new Button { Text = "🔍 Buscar", Location = new Point(325, yPos - 1), Size = new Size(95, 27), FlatStyle = FlatStyle.Flat, BackColor = bgPanel, Cursor = Cursors.Hand };
            _btnBrowse.Click += (s, e) => ShowProcessBrowser(); _panelConfig.Controls.Add(_btnBrowse);

            _panelConfig.Controls.Add(new Label { Text = "Perfil de Rendimiento:", Location = new Point(440, yPos + 3), AutoSize = true });
            _cmbPerfil = new ComboBox { Location = new Point(585, yPos), Size = new Size(100, 25), BackColor = bgPanel, ForeColor = textLight, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var key in ProfileOptions.Keys) _cmbPerfil.Items.Add(key); _cmbPerfil.SelectedIndex = 0; _panelConfig.Controls.Add(_cmbPerfil);

            yPos += 45;
            _panelConfig.Controls.Add(new Label { Text = "Prioridad (Foco):", Location = new Point(20, yPos + 3), AutoSize = true });
            _cmbPrioridad = new ComboBox { Location = new Point(135, yPos), Size = new Size(140, 25), BackColor = bgPanel, ForeColor = textLight, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var key in PriorityOptions.Keys) _cmbPrioridad.Items.Add(key); _cmbPrioridad.SelectedIndex = 0; _panelConfig.Controls.Add(_cmbPrioridad);

            _panelConfig.Controls.Add(new Label { Text = "Prioridad (Fondo):", Location = new Point(285, yPos + 3), AutoSize = true });
            _cmbPrioridadFondo = new ComboBox { Location = new Point(405, yPos), Size = new Size(140, 25), BackColor = bgPanel, ForeColor = textLight, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var key in PriorityOptions.Keys) _cmbPrioridadFondo.Items.Add(key);
            _cmbPrioridadFondo.SelectedItem = "Baja";
            _panelConfig.Controls.Add(_cmbPrioridadFondo);

            _btnAffinity = new Button { Text = "⚡ Núcleos CPU", Location = new Point(555, yPos - 1), Size = new Size(130, 27), FlatStyle = FlatStyle.Flat, BackColor = bgPanel, Cursor = Cursors.Hand };
            _btnAffinity.Click += (s, e) => ShowAffinityDialog(); _panelConfig.Controls.Add(_btnAffinity);

            yPos += 55;
            _btnAdd = new Button { Text = "➕ Guardar Regla", Location = new Point(20, yPos), Size = new Size(665, 35), FlatStyle = FlatStyle.Flat, BackColor = accentBlue, ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
            _btnAdd.Click += BtnAdd_Click; _panelConfig.Controls.Add(_btnAdd);

            this.Controls.Add(_panelConfig);

            // === PANEL 2: LISTA DE REGLAS (Tarjetas Modernas) ===
            _panelReglas = new Panel { Location = new Point(0, 95), Size = new Size(720, 400), BackColor = bgDark, Visible = false };

            _flowReglas = new FlowLayoutPanel
            {
                Location = new Point(20, 10),
                Size = new Size(665, 370),
                BackColor = bgDark,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };

            RefreshRulesList();
            _panelReglas.Controls.Add(_flowReglas);

            this.Controls.Add(_panelReglas);

            // === BARRA DE ESTADO (Parte Inferior) ===
            var statusBar = new Panel { Location = new Point(0, 510), Size = new Size(720, 50), BackColor = Color.FromArgb(20, 20, 22) };
            _lblTempStatus = new Label { Text = "🌡️ -- °C", Location = new Point(15, 15), AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.LightGray }; statusBar.Controls.Add(_lblTempStatus);
            _lblStatus = new Label { Text = "Inicializando...", Location = new Point(130, 18), AutoSize = true, Font = new Font("Segoe UI", 9), ForeColor = Color.LightGray }; statusBar.Controls.Add(_lblStatus);
            _lblStats = new Label { Text = "", Location = new Point(480, 18), AutoSize = true, Font = new Font("Segoe UI", 8), ForeColor = Color.Gray }; statusBar.Controls.Add(_lblStats);

            var statsTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            statsTimer.Tick += (s, e) => { var res = _statistics.ObtenerResumen(); _lblStats!.Text = $"Max Temp: {res.TempMax:F0}°C | Cambios: {res.TotalCambios}"; };
            statsTimer.Start();
            this.Controls.Add(statusBar);

            SwitchTab(0);
        }

        private void SwitchTab(int tabIndex)
        {
            var accentBlue = Color.FromArgb(0, 122, 204);
            var bgPanel = Color.FromArgb(45, 45, 48);

            if (tabIndex == 0) // Configurar
            {
                _btnTabConfig!.BackColor = accentBlue;
                _btnTabReglas!.BackColor = bgPanel;
                _panelConfig!.Visible = true;
                _panelReglas!.Visible = false;
            }
            else // Reglas
            {
                _btnTabConfig!.BackColor = bgPanel;
                _btnTabReglas!.BackColor = accentBlue;
                _panelConfig!.Visible = false;
                _panelReglas!.Visible = true;
                RefreshRulesList(); // Refrescar las tarjetas al entrar
            }
        }

        #endregion

        // === GENERADOR DE TARJETAS (CARDS) ===
        private void RefreshRulesList()
        {
            if (_flowReglas == null) return;

            _flowReglas.SuspendLayout();
            _flowReglas.Controls.Clear();

            foreach (var rule in _configService.Config.Reglas)
            {
                var card = CreateRuleCard(rule.Key, rule.Value);
                _flowReglas.Controls.Add(card);
            }

            _flowReglas.ResumeLayout();
        }

        private Panel CreateRuleCard(string processName, PerfilConfig config)
        {
            var cardBg = Color.FromArgb(45, 45, 48);
            var textLight = Color.White;
            var accentBlue = Color.FromArgb(0, 122, 204);

            var card = new Panel
            {
                Size = new Size(640, 70), // Más ancho, altura fija
                BackColor = cardBg,
                Margin = new Padding(0, 0, 0, 10),
                BorderStyle = BorderStyle.FixedSingle
            };

            // 1. Obtener y dibujar el Ícono
            var pBox = new PictureBox
            {
                Size = new Size(40, 40),
                Location = new Point(15, 15),
                SizeMode = PictureBoxSizeMode.Zoom
            };

            // Intentar extraer el ícono real del ejecutable
            Image? procIcon = GetIconForProcess(processName);
            if (procIcon != null)
            {
                pBox.Image = procIcon;
            }
            else
            {
                // Ícono genérico dibujado si no se encuentra el original
                var bmp = new Bitmap(40, 40);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var brush = new SolidBrush(Color.FromArgb(80, 80, 85)))
                        g.FillEllipse(brush, 0, 0, 40, 40);
                    using (var font = new Font("Segoe UI", 14, FontStyle.Bold))
                        g.DrawString(processName.Substring(0, 1).ToUpper(), font, Brushes.White, new PointF(10, 8));
                }
                pBox.Image = bmp;
            }
            card.Controls.Add(pBox);

            // 2. Nombre del proceso
            var lblName = new Label
            {
                Text = processName,
                Location = new Point(65, 12),
                AutoSize = true,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = textLight
            };
            card.Controls.Add(lblName);

            // 3. Etiqueta del Perfil Asignado (Con colorcito)
            string profileName = GetProfileName(config.Atajo).Split('/')[0].Trim();
            var lblProfile = new Label
            {
                Text = profileName,
                Location = new Point(65, 38),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 200, 255) // Azul brillante
            };
            card.Controls.Add(lblProfile);

            // 4. Información técnica (Prioridades y Núcleos)
            string priFoco = GetPriorityName(config.Prioridad);
            string priFondo = GetPriorityName(config.PrioridadFondo);
            string afinidad = (config.AfinidadActiva.HasValue || config.AfinidadFondo.HasValue) ? "Personalizado" : "Todos";

            var lblTech = new Label
            {
                Text = $"Foco: {priFoco}  |  Fondo: {priFondo}  |  CPU: {afinidad}",
                Location = new Point(195, 38),
                AutoSize = false,             // FIX: Ya no crecerá infinitamente
                Size = new Size(310, 18),     // FIX: Ancho máximo antes de truncar
                AutoEllipsis = true,          // FIX: Pone "..." si el texto es muy largo
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.Gray
            };
            card.Controls.Add(lblTech);

            // 5. Botón EDITAR
            var btnEdit = new Button
            {
                Text = "✏️ Editar",
                Location = new Point(515, 18), // FIX: Movido más a la derecha
                Size = new Size(70, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = textLight,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.5f)
            };
            btnEdit.FlatAppearance.BorderSize = 0;
            btnEdit.Click += (s, e) =>
            {
                EditRuleFromCard(processName, config);
            };
            card.Controls.Add(btnEdit);

            // 6. Botón ELIMINAR
            var btnDel = new Button
            {
                Text = "🗑️",
                Location = new Point(590, 18), // FIX: Movido más a la derecha
                Size = new Size(35, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(180, 60, 60),
                ForeColor = textLight,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10)
            };
            btnDel.FlatAppearance.BorderSize = 0;
            btnDel.Click += (s, e) =>
            {
                var result = MessageBox.Show(this, $"¿Seguro que deseas eliminar la regla para '{processName}'?", "Eliminar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result == DialogResult.Yes)
                {
                    _configService.Config.Reglas.Remove(processName);
                    SaveCurrentConfig();
                    RefreshRulesList();
                }
            };
            card.Controls.Add(btnDel);

            return card;
        }

        // Método auxiliar para intentar extraer el icono de un proceso
        private Image? GetIconForProcess(string processName)
        {
            if (_iconCache.TryGetValue(processName, out var cachedImg))
                return cachedImg;

            try
            {
                // Intentamos buscar el proceso si está en ejecución para sacar la ruta
                var proc = Process.GetProcessesByName(processName).FirstOrDefault();
                if (proc != null)
                {
                    string path = proc.MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(path))
                    {
                        var icon = Icon.ExtractAssociatedIcon(path);
                        if (icon != null)
                        {
                            var img = icon.ToBitmap();
                            _iconCache[processName] = img;
                            return img;
                        }
                    }
                }
            }
            catch { /* Ignorar bloqueos de acceso a módulos (32 vs 64 bits) */ }

            return null; // Si falla, devuelve null para que use el icono dibujado genérico
        }

        private void EditRuleFromCard(string processName, PerfilConfig config)
        {
            _txtProceso!.Text = processName;
            _cmbPerfil!.SelectedItem = GetProfileName(config.Atajo);
            _cmbPrioridad!.SelectedItem = GetPriorityName(config.Prioridad);
            _cmbPrioridadFondo!.SelectedItem = GetPriorityName(config.PrioridadFondo);
            _tempAffinityActive = config.AfinidadActiva;
            _tempAffinityBg = config.AfinidadFondo;
            _btnAffinity!.Text = (_tempAffinityActive.HasValue || _tempAffinityBg.HasValue) ? "⚡ Personalizado" : "⚡ Núcleos CPU";

            SwitchTab(0); // Volver a la pestaña de configuración
        }

        private void ConfigureSystemTray()
        {
            _trayIcon = new NotifyIcon { Icon = SystemIcons.Application, Text = "PlusControl PRO", Visible = true };
            _trayIcon.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; BringToFront(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("📊 Mostrar Programa", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; BringToFront(); });
            menu.Items.Add("-");
            menu.Items.Add("📥 Exportar Config", null, (s, e) => ExportConfig());
            menu.Items.Add("📤 Importar Config", null, (s, e) => ImportConfig());
            menu.Items.Add("-");
            menu.Items.Add("❌ Salir", null, (s, e) => { _isExiting = true; Application.Exit(); });
            _trayIcon.ContextMenuStrip = menu;
            this.Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) { Hide(); ShowInTaskbar = false; } };
        }

        private void ShowAdvancedSettings()
        {
            var form = new Form { Text = "⚙️ Ajustes", Size = new Size(420, 480), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.FromArgb(240, 240, 240) };
            var cfg = _configService.Config; int y = 20;

            form.Controls.Add(new Label { Text = "Retraso (ms):", Location = new Point(20, y), AutoSize = true }); var numDelay = new NumericUpDown { Location = new Point(280, y - 3), Minimum = 0, Maximum = 10000, Value = cfg.DelayGeneral, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White }; form.Controls.Add(numDelay); y += 35;
            form.Controls.Add(new Label { Text = "Transición 1↔2 (ms):", Location = new Point(20, y), AutoSize = true }); var numFast = new NumericUpDown { Location = new Point(280, y - 3), Minimum = 0, Maximum = 10000, Value = cfg.Delay1y2, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White }; form.Controls.Add(numFast); y += 35;
            form.Controls.Add(new Label { Text = "Muestreo Temp (ms):", Location = new Point(20, y), AutoSize = true }); var numTick = new NumericUpDown { Location = new Point(280, y - 3), Minimum = 100, Maximum = 5000, Value = cfg.TickRate, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White }; form.Controls.Add(numTick); y += 45;

            var chkMin = new CheckBox { Text = "Iniciar minimizado", Location = new Point(20, y), AutoSize = true, Checked = cfg.IniciarMinimizado }; form.Controls.Add(chkMin); y += 27;
            var chkClose = new CheckBox { Text = "Cerrar (X) minimiza a bandeja", Location = new Point(20, y), AutoSize = true, Checked = cfg.CerrarMinimiza }; form.Controls.Add(chkClose); y += 27;
            var chkTop = new CheckBox { Text = "Siempre visible", Location = new Point(20, y), AutoSize = true, Checked = cfg.SiempreArriba }; form.Controls.Add(chkTop); y += 27;
            var chkToast = new CheckBox { Text = "Mostrar notificaciones", Location = new Point(20, y), AutoSize = true, Checked = cfg.MostrarNotificaciones }; form.Controls.Add(chkToast); y += 27;

            var btnSave = new Button { Text = "Guardar", Location = new Point(110, y + 20), Size = new Size(180, 40), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Cursor = Cursors.Hand };
            btnSave.Click += (s, e) => { cfg.DelayGeneral = (int)numDelay.Value; cfg.Delay1y2 = (int)numFast.Value; cfg.TickRate = (int)numTick.Value; cfg.IniciarMinimizado = chkMin.Checked; cfg.CerrarMinimiza = chkClose.Checked; cfg.SiempreArriba = chkTop.Checked; cfg.MostrarNotificaciones = chkToast.Checked; SaveCurrentConfig(); form.Close(); };

            form.Controls.Add(btnSave);
            form.TopMost = this.TopMost;
            form.ShowDialog(this);
        }

        private void ShowProcessBrowser()
        {
            var form = new Form { Text = "Procesos", Size = new Size(350, 480), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
            var txtSearch = new TextBox { Dock = DockStyle.Top, BackColor = Color.FromArgb(60, 60, 65), ForeColor = Color.White };
            var lst = new ListBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, Font = new Font("Consolas", 10) };
            var procs = Process.GetProcesses().Select(p => p.ProcessName).Distinct().OrderBy(p => p).ToArray();
            lst.Items.AddRange(procs);
            txtSearch.TextChanged += (s, e) => { lst.Items.Clear(); var f = txtSearch.Text.ToLower(); lst.Items.AddRange(string.IsNullOrWhiteSpace(f) ? procs : procs.Where(p => p.ToLower().Contains(f)).ToArray()); };
            var btnSel = new Button { Text = "Seleccionar", Dock = DockStyle.Bottom, Height = 45, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
            btnSel.Click += (s, e) => { if (lst.SelectedItem != null) { _txtProceso!.Text = lst.SelectedItem.ToString(); form.Close(); } };
            lst.DoubleClick += (s, e) => btnSel.PerformClick();

            form.Controls.Add(lst); form.Controls.Add(txtSearch); form.Controls.Add(btnSel);
            form.TopMost = this.TopMost;
            form.ShowDialog(this);
        }

        private void ShowAffinityDialog()
        {
            int cores = Environment.ProcessorCount;
            var form = new Form { Text = $"⚡ Configurar Afinidad (Hilos detectados: {cores})", Size = new Size(450, 520), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };

            form.Controls.Add(new Label { Text = "🎯 ACTIVO (Con Focus)", Location = new Point(20, 15), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
            var clbA = new CheckedListBox { Location = new Point(20, 40), Size = new Size(185, 360), BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true, IntegralHeight = false };

            form.Controls.Add(new Label { Text = "📱 SEGUNDO PLANO", Location = new Point(230, 15), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
            var clbB = new CheckedListBox { Location = new Point(230, 40), Size = new Size(185, 360), BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true, IntegralHeight = false };

            form.Controls.Add(clbA);
            form.Controls.Add(clbB);

            for (int i = 0; i < cores; i++)
            {
                clbA.Items.Add($"CPU Core {i}", !_tempAffinityActive.HasValue || ((_tempAffinityActive.Value & (1L << i)) != 0));
                clbB.Items.Add($"CPU Core {i}", !_tempAffinityBg.HasValue || ((_tempAffinityBg.Value & (1L << i)) != 0));
            }

            var btnSave = new Button { Text = "💾 Guardar Configuración", Location = new Point(20, 420), Size = new Size(395, 40), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
            btnSave.Click += (s, e) => {
                long mA = 0, mB = 0;
                for (int i = 0; i < cores; i++) { if (clbA.GetItemChecked(i)) mA |= (1L << i); if (clbB.GetItemChecked(i)) mB |= (1L << i); }
                _tempAffinityActive = mA == ((1L << cores) - 1) ? null : mA; _tempAffinityBg = mB == ((1L << cores) - 1) ? null : mB;
                _btnAffinity!.Text = (_tempAffinityActive.HasValue || _tempAffinityBg.HasValue) ? "⚡ Personalizado" : "⚡ Todos"; form.Close();
            };

            form.Controls.Add(btnSave);
            form.TopMost = this.TopMost;
            form.ShowDialog(this);
        }

        private void BtnAdd_Click(object? sender, EventArgs e)
        {
            var p = _txtProceso?.Text.Trim().ToLower();
            if (string.IsNullOrEmpty(p)) return;

            _configService.Config.Reglas[p] = new PerfilConfig
            {
                Atajo = ProfileOptions[_cmbPerfil!.SelectedItem!.ToString()!],
                Prioridad = PriorityOptions[_cmbPrioridad!.SelectedItem!.ToString()!],
                PrioridadFondo = PriorityOptions[_cmbPrioridadFondo!.SelectedItem!.ToString()!],
                AfinidadActiva = _tempAffinityActive,
                AfinidadFondo = _tempAffinityBg,
                Notas = $"Añadido {DateTime.Now:dd/MM/yyyy}"
            };
            SaveCurrentConfig();

            _txtProceso?.Clear();
            _tempAffinityActive = null;
            _tempAffinityBg = null;
            _btnAffinity!.Text = "⚡ Núcleos CPU";

            MessageBox.Show(this, "Regla guardada exitosamente.", "Éxito", MessageBoxButtons.OK, MessageBoxIcon.Information);

            // Ir a la pestaña de reglas para ver la tarjeta recién creada
            SwitchTab(1);
        }

        private void ExportConfig()
        {
            using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"pluscontrol_config_{DateTime.Now:yyyyMMdd}" };
            if (dialog.ShowDialog() == DialogResult.OK) { _configService.ExportToFile(dialog.FileName); MessageBox.Show(this, "Exportado", "Éxito", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        private void ImportConfig()
        {
            using var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
            if (dialog.ShowDialog() == DialogResult.OK) { try { _configService.ImportFromFile(dialog.FileName); RefreshRulesList(); } catch { } }
        }

        private void SaveCurrentConfig()
        {
            try
            {
                if (_cmbDefaultProfile != null) _configService.Config.AtajoDefecto = ProfileOptions[_cmbDefaultProfile.SelectedItem?.ToString() ?? "BALANCED_TURBO / Perfil 3"];
                if (_chkEnableTemp != null)
                {
                    _configService.Config.ControlTermico.Enabled = _chkEnableTemp.Checked; _configService.Config.ControlTermico.TempAlta = (int)_numTempHigh!.Value; _configService.Config.ControlTermico.SegundosAlta = (int)_numSecHigh!.Value; _configService.Config.ControlTermico.TempBaja = (int)_numTempLow!.Value; _configService.Config.ControlTermico.SegundosBaja = (int)_numSecLow!.Value;
                }
                _configService.Save();
            }
            catch { }
        }

        private string GetProfileName(string hotkey) => ProfileOptions.FirstOrDefault(x => x.Value == hotkey).Key ?? "Desconocido";
        private string GetPriorityName(ProcessPriorityClass? priority) => PriorityOptions.FirstOrDefault(x => x.Value == priority).Key ?? "Normal";
        private int HotkeyToRank(string hotkey) => hotkey switch { "%{NUMPAD1}" => 1, "%{NUMPAD2}" => 2, "%{NUMPAD4}" => 4, _ => 3 };
        private string RankToHotkey(int rank) => rank switch { <= 1 => "%{NUMPAD1}", 2 => "%{NUMPAD2}", 4 => "%{NUMPAD4}", _ => "%{NUMPAD3}" };
    }

    public static class ControlExtensions { public static void InvokeIfRequired(this Control c, Action a) { if (c.InvokeRequired) c.Invoke(a); else a(); } }

    public class ObjectPool<T> where T : class
    {
        private readonly Func<T> _factory; private readonly ConcurrentBag<T> _pool = new(); private readonly int _maxSize;
        public ObjectPool(int maxSize, Func<T> factory) { _maxSize = maxSize; _factory = factory; }
        public T Get() => _pool.TryTake(out var item) ? item : _factory();
        public void Return(T item) { if (_pool.Count < _maxSize) _pool.Add(item); else (item as IDisposable)?.Dispose(); }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime; } catch { try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { } }
            Application.Run(new MainForm());
        }
    }
}