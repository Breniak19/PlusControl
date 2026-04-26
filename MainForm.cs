using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes; // LIBRERÍA PARA PC_F
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PlusControl.Pro
{
    // ======= UpdateVisitor =======
    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            foreach (var hw in computer.Hardware)
                hw.Accept(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            try
            {
                hardware.Update();
            }
            catch { }

            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { /* no-op */ }
        public void VisitParameter(IParameter parameter) { /* no-op */ }
    }

    #region ==================== MODELOS DE DATOS ====================

    /// <summary>
    /// Modelo de configuración térmica con validación
    /// </summary>
    public class TempConfig : IValidatable
    {
        public bool Enabled { get; set; } = false;

        [Range(60, 100)]
        public int TempAlta { get; set; } = 80;

        [Range(5, 120)]
        public int SegundosAlta { get; set; } = 15;

        [Range(40, 70)]
        public int TempBaja { get; set; } = 65;

        [Range(5, 120)]
        public int SegundosBaja { get; set; } = 15;

        public bool IsValid(out string error)
        {
            error = string.Empty;
            if (TempBaja >= TempAlta)
            {
                error = "La temperatura baja debe ser menor que la alta";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Configuración principal con soporte para versionado y migración
    /// </summary>
    public class PlusControlData
    {
        public int Version { get; set; } = 2;
        public DateTime LastModified { get; set; } = DateTime.Now;

        public string AtajoDefecto { get; set; } = "%{NUMPAD3}";

        // Ajustes Avanzados
        public int DelayGeneral { get; set; } = 2000;
        public int Delay1y2 { get; set; } = 500;
        public int TickRate { get; set; } = 1000;
        public bool IniciarMinimizado { get; set; } = false;
        public bool CerrarMinimiza { get; set; } = true;
        public bool SiempreArriba { get; set; } = false;
        public bool ActivarBeep { get; set; } = false;
        public bool RestaurarAlSalir { get; set; } = true;
        public bool ModoSilencioso { get; set; } = false;
        public bool AutoDetectarJuegos { get; set; } = true;
        public bool MostrarNotificaciones { get; set; } = true;
        public int OpacidadVentana { get; set; } = 100;

        public TempConfig ControlTermico { get; set; } = new TempConfig();
        public Dictionary<string, PerfilConfig> Reglas { get; set; } = new Dictionary<string, PerfilConfig>();

        // Estadísticas persistentes
        public EstadisticasApp Estadisticas { get; set; } = new EstadisticasApp();
    }

    public class PerfilConfig
    {
        public string Atajo { get; set; } = "";
        public ProcessPriorityClass? Prioridad { get; set; }
        public long? AfinidadActiva { get; set; }
        public long? AfinidadFondo { get; set; }
        public DateTime FechaCreacion { get; set; } = DateTime.Now;
        public int VecesUsado { get; set; } = 0;
        public string Notas { get; set; } = "";
    }

    public class EstadisticasApp
    {
        public DateTime InicioSesion { get; set; } = DateTime.Now;
        public int TotalCambiosPerfil { get; set; } = 0;
        public float TemperaturaMaxima { get; set; } = 0f;
        public float TemperaturaMinima { get; set; } = 999f;
        public float TemperaturaPromedio { get; set; } = 0f;
        public int MuestrasTemperatura { get; set; } = 0;
        public Dictionary<string, int> ProcesosDetectados { get; set; } = new Dictionary<string, int>();
        public List<EventoHistorial> HistorialEventos { get; set; } = new List<EventoHistorial>();
    }

    public class EventoHistorial
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public TipoEvento Tipo { get; set; }
        public string Mensaje { get; set; } = "";
        public string Detalle { get; set; } = "";
        public float? Temperatura { get; set; }
    }

    public enum TipoEvento
    {
        CambioPerfil,
        AlertaTemperatura,
        RecuperacionTemperatura,
        Error,
        Info,
        ReglaAplicada,
        SistemaIniciado
    }

    public interface IValidatable
    {
        bool IsValid(out string error);
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class RangeAttribute : Attribute
    {
        public int Min { get; }
        public int Max { get; }
        public RangeAttribute(int min, int max) { Min = min; Max = max; }
    }

    #endregion

    #region ==================== SERVICIOS (LÓGICA DE NEGOCIO) ====================

    /// <summary>
    /// Servicio de Logging avanzado con rotación de archivos
    /// </summary>
    public interface ILoggerService
    {
        void LogInfo(string mensaje, string detalle = "");
        void LogError(string mensaje, Exception? ex = null);
        void LogEvento(TipoEvento tipo, string mensaje, string detalle = "", float? temp = null);
        IReadOnlyList<EventoHistorial> ObtenerHistorial(int ultimosN = 50);
        void LimpiarHistorial();
        event Action<EventoHistorial>? OnNuevoEvento;
    }

    public class LoggerService : ILoggerService
    {
        private readonly string _logPath;
        private readonly ConcurrentQueue<EventoHistorial> _historial = new();
        private readonly int _maxHistorial = 200;
        private readonly object _fileLock = new();

        public event Action<EventoHistorial>? OnNuevoEvento;

        public LoggerService(string basePath)
        {
            var logsDir = Path.Combine(basePath, "logs");
            Directory.CreateDirectory(logsDir);
            _logPath = Path.Combine(logsDir, $"pluscontrol_{DateTime.Now:yyyyMMdd}.log");

            LogEvento(TipoEvento.SistemaIniciado, "PlusControl PRO iniciado", $"Versión 2.0 | .NET {Environment.Version}");
        }

        public void LogInfo(string mensaje, string detalle = "")
        {
            LogEvento(TipoEvento.Info, mensaje, detalle);
            EscribirArchivo($"[INFO] {DateTime.Now:HH:mm:ss} - {mensaje}");
        }

        public void LogError(string mensaje, Exception? ex = null)
        {
            LogEvento(TipoEvento.Error, mensaje, ex?.Message ?? "");
            EscribirArchivo($"[ERROR] {DateTime.Now:HH:mm:ss} - {mensaje}\n{ex?.StackTrace}");
        }

        public void LogEvento(TipoEvento tipo, string mensaje, string detalle = "", float? temp = null)
        {
            var evento = new EventoHistorial
            {
                Timestamp = DateTime.Now,
                Tipo = tipo,
                Mensaje = mensaje,
                Detalle = detalle,
                Temperatura = temp
            };

            _historial.Enqueue(evento);
            while (_historial.Count > _maxHistorial) _historial.TryDequeue(out _);

            OnNuevoEvento?.Invoke(evento);
        }

        public IReadOnlyList<EventoHistorial> ObtenerHistorial(int ultimosN = 50)
        {
            return _historial.TakeLast(ultimosN).ToList().AsReadOnly();
        }

        public void LimpiarHistorial()
        {
            while (_historial.Count > 0) _historial.TryDequeue(out _);
        }

        private void EscribirArchivo(string linea)
        {
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_logPath, linea + Environment.NewLine);
                }
            }
            catch { /* Silencioso para no interrumpir flujo principal */ }
        }
    }

    /// <summary>
    /// Servicio de monitoreo de hardware con caching inteligente
    /// </summary>
    public interface IHardwareMonitorService
    {
        event Action<float>? OnTemperatureChanged;
        float CurrentTemperature { get; }
        int CoreCount { get; }
        bool IsAvailable { get; }
        void Update();
        void Dispose();
    }

    public class HardwareMonitorService : IHardwareMonitorService, IDisposable
    {
        private Computer? _computer;
        private readonly UpdateVisitor _visitor = new UpdateVisitor();
        private float _currentTemp = 0f;
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMilliseconds(900);
        private DateTime _lastUpdate = DateTime.MinValue;
        private bool _isAvailable;

        public event Action<float>? OnTemperatureChanged;
        public float CurrentTemperature => _currentTemp;
        public int CoreCount => Environment.ProcessorCount;
        public bool IsAvailable => _isAvailable;

        public HardwareMonitorService()
        {
            try
            {
                _computer = new Computer { IsCpuEnabled = true };
                _computer.Open();
                _computer.Accept(_visitor);
                _isAvailable = true;
            }
            catch
            {
                _isAvailable = false;
            }
        }

        public void Update()
        {
            if (_computer == null || !_isAvailable) return;

            // Cache inteligente: no leer más frecuente que necesario
            if (DateTime.Now - _lastUpdate < _cacheDuration) return;

            try
            {
                _computer.Accept(_visitor);
                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

                if (cpu != null)
                {
                    ISensor? sensor = cpu.Sensors.FirstOrDefault(s =>
                        s.SensorType == SensorType.Temperature && s.Name.Contains("Package"))
                        ?? cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);

                    if (sensor?.Value.HasValue == true)
                    {
                        var oldTemp = _currentTemp;
                        _currentTemp = sensor.Value.Value;
                        _lastUpdate = DateTime.Now;

                        // Solo notificar si cambió significativamente (> 0.5°C)
                        if (Math.Abs(_currentTemp - oldTemp) > 0.5f)
                        {
                            OnTemperatureChanged?.Invoke(_currentTemp);
                        }
                    }
                }
            }
            catch { _isAvailable = false; }
        }

        public void Dispose()
        {
            try { _computer?.Close(); } catch { }
        }
    }

    /// <summary>
    /// Servicio de gestión de procesos con optimizaciones de rendimiento
    /// </summary>
    public interface IProcessManagerService
    {
        ProcessInfo? GetForegroundProcess();
        void ApplyProfile(ProcessInfo process, PerfilConfig config, bool isManagedRule = false);
        void ApplyBackgroundState(string processName, PerfilConfig config);
        void RestoreProcess(string processName);
        void AplicarPerfilHardware(string legacyHotkey);
        long GetAllCoresMask();
    }

    public class ProcessInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public string MainWindowTitle { get; set; } = "";
        public DateTime DetectedAt { get; set; } = DateTime.Now;
    }

    public class ProcessManagerService : IProcessManagerService
    {
        private readonly ILoggerService _logger;
        private readonly ConcurrentDictionary<int, ProcessInfo> _processCache = new();
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(5);

        public ProcessManagerService(ILoggerService logger)
        {
            _logger = logger;
        }

        public ProcessInfo? GetForegroundProcess()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return null;

            // Usar cache si está fresco
            if (_processCache.TryGetValue((int)pid, out var cached) &&
                DateTime.Now - cached.DetectedAt < _cacheExpiry)
            {
                return cached;
            }

            try
            {
                var process = Process.GetProcessById((int)pid);
                var info = new ProcessInfo
                {
                    ProcessId = (int)pid,
                    ProcessName = process.ProcessName.ToLower(),
                    MainWindowTitle = process.MainWindowTitle ?? "",
                    DetectedAt = DateTime.Now
                };

                _processCache.AddOrUpdate((int)pid, info, (_, __) => info);

                // Limpiar cache vieja
                foreach (var item in _processCache.Where(x =>
                    DateTime.Now - x.Value.DetectedAt > _cacheExpiry).ToList())
                {
                    _processCache.TryRemove(item.Key, out _);
                }

                return info;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error obteniendo proceso foreground", ex);
                return null;
            }
        }

        public void ApplyProfile(ProcessInfo process, PerfilConfig config, bool isManagedRule = false)
        {
            try
            {
                using var proc = Process.GetProcessById(process.ProcessId);
                if (proc.HasExited) return;

                // EVITAR TOCAR LA PRIORIDAD DE PROCESOS NO GESTIONADOS (Ej: PC_F, Windows, etc.)
                if (config.Prioridad.HasValue)
                {
                    proc.PriorityClass = config.Prioridad.Value;
                }
                else if (isManagedRule)
                {
                    // Si el proceso es gestionado, pero su prioridad es "Sin cambios" (null),
                    // comprobamos si estaba en Idle (por haber estado en segundo plano) y lo restauramos.
                    if (proc.PriorityClass == ProcessPriorityClass.Idle)
                        proc.PriorityClass = ProcessPriorityClass.Normal;
                }

                // EVITAR TOCAR LA AFINIDAD DE PROCESOS NO GESTIONADOS
                if (config.AfinidadActiva.HasValue)
                {
                    proc.ProcessorAffinity = (IntPtr)config.AfinidadActiva.Value;
                }
                else if (isManagedRule)
                {
                    // Si es gestionado pero no tiene afinidad, asume todos los núcleos
                    proc.ProcessorAffinity = (IntPtr)GetAllCoresMask();
                }

                config.VecesUsado++;
                _logger.LogEvento(TipoEvento.ReglaAplicada,
                    $"Perfil aplicado a {process.ProcessName}",
                    $"Prioridad: {(config.Prioridad.HasValue ? config.Prioridad.ToString() : "No alterada")}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error aplicando perfil a {process.ProcessName}", ex);
            }
        }

        public void ApplyBackgroundState(string processName, PerfilConfig config)
        {
            Task.Run(() =>
            {
                try
                {
                    var processes = Process.GetProcessesByName(processName);
                    foreach (var p in processes)
                    {
                        try
                        {
                            if (config.AfinidadFondo.HasValue)
                                p.ProcessorAffinity = (IntPtr)config.AfinidadFondo.Value;
                            p.PriorityClass = ProcessPriorityClass.Idle;
                        }
                        catch { /* Ignorar procesos que ya cerraron */ }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            });
        }

        public void RestoreProcess(string processName)
        {
            Task.Run(() =>
            {
                try
                {
                    var processes = Process.GetProcessesByName(processName);
                    var allCores = GetAllCoresMask();
                    foreach (var p in processes)
                    {
                        try
                        {
                            p.PriorityClass = ProcessPriorityClass.Normal;
                            p.ProcessorAffinity = (IntPtr)allCores;
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            });
        }

        // ==============================================================
        // NUEVA LÓGICA DE PC_F
        // ==============================================================
        public void AplicarPerfilHardware(string legacyHotkey)
        {
            int perfilId = 3;

            if (legacyHotkey == "%{NUMPAD1}") perfilId = 1;
            else if (legacyHotkey == "%{NUMPAD2}") perfilId = 2;
            else if (legacyHotkey == "%{NUMPAD3}") perfilId = 3;
            else if (legacyHotkey == "%{NUMPAD4}") perfilId = 4;

            Task.Run(() =>
            {
                try
                {
                    using (NamedPipeClientStream pipeClient = new NamedPipeClientStream(".", "PlusControlPipe", PipeDirection.Out))
                    {
                        pipeClient.Connect(500);

                        using (StreamWriter writer = new StreamWriter(pipeClient))
                        {
                            writer.AutoFlush = true;
                            writer.WriteLine($"PERFIL:{perfilId}");
                        }
                    }
                }
                catch (TimeoutException)
                {
                    _logger.LogInfo($"PC_F (Músculo) no responde o está apagado. Perfil {perfilId} omitido.");
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error en la tubería PC_F", ex);
                }
            });
        }

        public long GetAllCoresMask() => (1L << Environment.ProcessorCount) - 1;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

    /// <summary>
    /// Servicio de configuración con validación y backup automático
    /// </summary>
    public interface IConfigurationService
    {
        PlusControlData Config { get; }
        void Save();
        void Load();
        void CreateBackup();
        void ExportToFile(string path);
        void ImportFromFile(string path);
        event Action<PlusControlData>? OnConfigChanged;
    }

    public class ConfigurationService : IConfigurationService
    {
        private readonly string _configPath;
        private PlusControlData _config = new();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public event Action<PlusControlData>? OnConfigChanged;
        public PlusControlData Config => _config;

        public ConfigurationService(string basePath)
        {
            _configPath = Path.Combine(basePath, "config.json");
            Load();
        }

        public void Save()
        {
            try
            {
                _config.LastModified = DateTime.Now;
                var json = JsonSerializer.Serialize(_config, _jsonOptions);
                File.WriteAllText(_configPath, json);
                OnConfigChanged?.Invoke(_config);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error guardando configuración: {ex.Message}", ex);
            }
        }

        public void Load()
        {
            if (!File.Exists(_configPath))
            {
                CreateDefaultRules();
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(_configPath);
                var data = JsonSerializer.Deserialize<PlusControlData>(json, _jsonOptions);

                if (data != null)
                {
                    MigrateConfig(data);
                    _config = data;

                    ValidateConfig();
                }
            }
            catch
            {
                _config = new PlusControlData();
                CreateDefaultRules();
            }
        }

        public void CreateBackup()
        {
            try
            {
                var backupDir = Path.Combine(Path.GetDirectoryName(_configPath)!, "backups");
                Directory.CreateDirectory(backupDir);
                var backupPath = Path.Combine(backupDir, $"config_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.Copy(_configPath, backupPath, true);
            }
            catch { }
        }

        public void ExportToFile(string path)
        {
            var json = JsonSerializer.Serialize(_config, _jsonOptions);
            File.WriteAllText(path, json);
        }

        public void ImportFromFile(string path)
        {
            CreateBackup();
            var json = File.ReadAllText(path);
            var imported = JsonSerializer.Deserialize<PlusControlData>(json, _jsonOptions);
            if (imported != null)
            {
                _config = imported;
                Save();
            }
        }

        private void MigrateConfig(PlusControlData data)
        {
            if (data.Version < 2)
            {
                FixOldHotkeys(data);
                data.Version = 2;
            }
        }

        private void FixOldHotkeys(PlusControlData data)
        {
            var hotkeyMap = new Dictionary<string, string>
            {
                {"^%1", "%{NUMPAD1}"}, {"^%2", "%{NUMPAD2}"},
                {"^%3", "%{NUMPAD3}"}, {"^%4", "%{NUMPAD4}"}
            };

            if (hotkeyMap.ContainsKey(data.AtajoDefecto))
                data.AtajoDefecto = hotkeyMap[data.AtajoDefecto];

            foreach (var rule in data.Reglas.Values.Where(r => hotkeyMap.ContainsKey(r.Atajo)))
            {
                rule.Atajo = hotkeyMap[rule.Atajo];
            }
        }

        private void ValidateConfig()
        {
            _config.TickRate = Math.Clamp(_config.TickRate, 100, 5000);
            _config.DelayGeneral = Math.Clamp(_config.DelayGeneral, 0, 10000);
            _config.ControlTermico.TempAlta = Math.Clamp(_config.ControlTermico.TempAlta, 60, 100);
            _config.ControlTermico.TempBaja = Math.Clamp(_config.ControlTermico.TempBaja, 40, 70);
        }

        private void CreateDefaultRules()
        {
            _config.Reglas["steam"] = new PerfilConfig
            {
                Atajo = "%{NUMPAD1}",
                Prioridad = ProcessPriorityClass.High,
                Notas = "Steam - Detectado automáticamente"
            };
        }
    }

    /// <summary>
    /// Servicio de estadísticas en tiempo real
    /// </summary>
    public interface IStatisticsService
    {
        void RecordTemperature(float temp);
        void RecordProfileChange(string processName, string profile);
        void RecordProcessDetected(string processName);
        EstadisticasResumen ObtenerResumen();
    }

    public class EstadisticasResumen
    {
        public float TempMax { get; set; }
        public float TempMin { get; set; }
        public float TempAvg { get; set; }
        public int TotalCambios { get; set; }
        public string ProcesoMasUsado { get; set; } = "";
        public TimeSpan TiempoActivo { get; set; }
    }

    public class StatisticsService : IStatisticsService
    {
        private readonly PlusControlData _config;
        private float _tempSum = 0f;
        private int _tempCount = 0;

        public StatisticsService(IConfigurationService configService)
        {
            _config = configService.Config;
        }

        public void RecordTemperature(float temp)
        {
            if (temp <= 0) return;

            _tempSum += temp;
            _tempCount++;

            if (temp > _config.Estadisticas.TemperaturaMaxima)
                _config.Estadisticas.TemperaturaMaxima = temp;

            if (temp < _config.Estadisticas.TemperaturaMinima)
                _config.Estadisticas.TemperaturaMinima = temp;

            _config.Estadisticas.MuestrasTemperatura = _tempCount;
            _config.Estadisticas.TemperaturaPromedio = _tempSum / _tempCount;
        }

        public void RecordProfileChange(string processName, string profile)
        {
            _config.Estadisticas.TotalCambiosPerfil++;
        }

        public void RecordProcessDetected(string processName)
        {
            if (!_config.Estadisticas.ProcesosDetectados.ContainsKey(processName))
                _config.Estadisticas.ProcesosDetectados[processName] = 0;
            _config.Estadisticas.ProcesosDetectados[processName]++;
        }

        public EstadisticasResumen ObtenerResumen()
        {
            return new EstadisticasResumen
            {
                TempMax = _config.Estadisticas.TemperaturaMaxima,
                TempMin = _config.Estadisticas.TemperaturaMinima > 999 ? 0 : _config.Estadisticas.TemperaturaMinima,
                TempAvg = _config.Estadisticas.TemperaturaPromedio,
                TotalCambios = _config.Estadisticas.TotalCambiosPerfil,
                ProcesoMasUsado = _config.Estadisticas.ProcesosDetectados
                    .OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "N/A",
                TiempoActivo = DateTime.Now - _config.Estadisticas.InicioSesion
            };
        }
    }

    #endregion

    #region ==================== INTERFAZ DE USUARIO MEJORADA ====================

    public partial class MainForm : Form
    {
        // === DEPENDENCIAS (Inyección Manual) ===
        private readonly IConfigurationService _configService;
        private readonly IHardwareMonitorService _hardwareService;
        private readonly IProcessManagerService _processService;
        private readonly ILoggerService _logger;
        private readonly IStatisticsService _statistics;

        // === ESTADO DE LA APLICACIÓN ===
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

        // === CONTROLES UI ===
        private System.Windows.Forms.Timer? _monitorTimer;
        // NOTA: Se eliminó _processTimer ya que ahora usamos FocusLoop en segundo plano
        private NotifyIcon? _trayIcon;
        private Label? _lblStatus, _lblTempStatus;
        private ListBox? _lstReglas;
        private TextBox? _txtProceso;
        private ComboBox? _cmbPerfil, _cmbPrioridad, _cmbDefaultProfile;
        private Button? _btnAdd, _btnEdit, _btnDelete, _btnBrowse, _btnAffinity;
        private CheckBox? _chkEnableTemp;
        private NumericUpDown? _numTempHigh, _numSecHigh, _numTempLow, _numSecLow;
        private Panel? _chartPanel;
        private ListBox? _lstHistory;
        private Label? _lblStats;

        // === OBJETO POOL PARA ICONOS ===
        private static readonly ObjectPool<Bitmap> BitmapPool = new ObjectPool<Bitmap>(16, () => new Bitmap(16, 16));

        // === OPCIONES ===
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

        // === DLL IMPORTS ===
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public MainForm()
        {
            var basePath = Application.StartupPath;
            _logger = new LoggerService(basePath);
            _configService = new ConfigurationService(basePath);
            _hardwareService = new HardwareMonitorService();
            _processService = new ProcessManagerService(_logger);
            _statistics = new StatisticsService(_configService);

            _hardwareService.OnTemperatureChanged += OnTemperatureChanged;
            _logger.OnNuevoEvento += OnNewLogEvent;
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
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
            }
            catch
            {
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
            }
        }

        // ==============================================================
        // SEPARACIÓN DE RELOJES Y LOOP ASÍNCRONO DE FOCO (EL FIX)
        // ==============================================================
        private void InitializeTimer()
        {
            // 1. Reloj de Hardware Térmico (Sigue la config del usuario para gráficos)
            _monitorTimer = new System.Windows.Forms.Timer
            {
                Interval = _configService.Config.TickRate > 0 ? _configService.Config.TickRate : 1000
            };
            _monitorTimer.Tick += MonitorTick;
            _monitorTimer.Start();

            // 2. Reloj de Detección de Foco (Bucle en segundo plano, no bloquea la interfaz)
            Task.Run(FocusLoop);
        }

        private async Task FocusLoop()
        {
            while (!_isExiting)
            {
                try
                {
                    var foregroundProc = _processService.GetForegroundProcess();
                    if (foregroundProc != null)
                    {
                        var currentProcess = foregroundProc.ProcessName;
                        if (currentProcess != _lastProcessName)
                        {
                            HandleProcessChangeFast(foregroundProc, currentProcess);
                        }
                    }
                }
                catch { }

                // Muestreo ultra rápido (50ms) sin bloquear la interfaz
                await Task.Delay(50);
            }
        }

        private async void MonitorTick(object? sender, EventArgs e)
        {
            // Pausar para evitar colisiones
            _monitorTimer?.Stop();

            try
            {
                // LECTURA DE HARDWARE EN SEGUNDO PLANO (Evita que la app se congele)
                await Task.Run(() => _hardwareService.Update());

                var currentTemp = _hardwareService.CurrentTemperature;
                UpdateTemperatureDisplay(currentTemp);
                _statistics.RecordTemperature(currentTemp);

                if (_configService.Config.ControlTermico.Enabled &&
                    _isStableAppConfigured && currentTemp > 0)
                {
                    ProcessThermalControl(currentTemp);
                }

                UpdateTrayIcon(currentTemp);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error en MonitorTick", ex);
            }
            finally
            {
                _monitorTimer?.Start();
            }
        }

        private void HandleProcessChangeFast(ProcessInfo process, string currentProcess)
        {
            this.InvokeIfRequired(() =>
                _lblStatus!.Text = $"Focus: [{currentProcess}] -> Evaluando...");

            if (!string.IsNullOrEmpty(_lastProcessName) &&
                _configService.Config.Reglas.ContainsKey(_lastProcessName))
            {
                _processService.ApplyBackgroundState(_lastProcessName,
                    _configService.Config.Reglas[_lastProcessName]);
            }

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

            // Ejecutar el Delay y la aplicación en un hilo independiente (Fuego y Olvido)
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token);

                    if (!token.IsCancellationRequested)
                    {
                        this.InvokeIfRequired(() => ApplyBaseProfile(process, targetHotkey, isManagedRule));
                    }
                }
                catch (TaskCanceledException) { }
            }, token);
        }

        private void ApplyBaseProfile(ProcessInfo process, string hotkey, bool isManagedRule)
        {
            PerfilConfig? config = null;

            if (_configService.Config.Reglas.ContainsKey(process.ProcessName))
            {
                config = _configService.Config.Reglas[process.ProcessName];
                _isStableAppConfigured = true;
            }

            var effectiveConfig = config ?? new PerfilConfig { Atajo = hotkey };

            _processService.ApplyProfile(process, effectiveConfig, isManagedRule);

            _currentBaseRank = HotkeyToRank(hotkey);
            _currentActualRank = _currentBaseRank;
            _heatCounter = 0;
            _coolCounter = 0;

            if (hotkey != _lastAppliedProfile)
            {
                _processService.AplicarPerfilHardware(hotkey);
                _lastAppliedProfile = hotkey;

                _logger.LogEvento(TipoEvento.CambioPerfil,
                    $"Perfil cambiado a {GetProfileName(hotkey)}",
                    $"Proceso: {process.ProcessName}",
                    _hardwareService.CurrentTemperature);

                _statistics.RecordProfileChange(process.ProcessName, hotkey);

                ShowNotification($"Perfil: {GetProfileName(hotkey)}", process.ProcessName);
            }

            // REFLEJO INMEDIATO EN LA INTERFAZ
            UpdateStatusLabel(process.ProcessName);
            UpdateTrayIcon(_hardwareService.CurrentTemperature); // Fuerza a dibujar el nuevo perfil al instante
        }
        // ==============================================================

        private void ProcessThermalControl(float temp)
        {
            var thermalCfg = _configService.Config.ControlTermico;

            if (temp >= thermalCfg.TempAlta) { _heatCounter++; _coolCounter = 0; }
            else if (temp <= thermalCfg.TempBaja) { _coolCounter++; _heatCounter = 0; }
            else { _heatCounter = 0; _coolCounter = 0; }

            if (_heatCounter >= thermalCfg.SegundosAlta)
            {
                _heatCounter = 0;
                if (_currentActualRank < 4)
                {
                    _currentActualRank++;
                    var newHotkey = RankToHotkey(_currentActualRank);
                    _processService.AplicarPerfilHardware(newHotkey);
                    _lastAppliedProfile = newHotkey;

                    _logger.LogEvento(TipoEvento.AlertaTemperatura,
                        "Perfil reducido por temperatura alta",
                        $"{temp:F1}°C >= {thermalCfg.TempAlta}°C", temp);

                    UpdateStatusLabel(_lastProcessName, " ⚠️ Por Calor");
                    ShowNotification("⚠️ Temperatura Alta", $"Reduciendo a {GetProfileName(newHotkey)}");
                }
            }

            if (_coolCounter >= thermalCfg.SegundosBaja)
            {
                _coolCounter = 0;
                if (_currentActualRank > _currentBaseRank)
                {
                    _currentActualRank--;
                    var newHotkey = RankToHotkey(_currentActualRank);
                    _processService.AplicarPerfilHardware(newHotkey);
                    _lastAppliedProfile = newHotkey;

                    _logger.LogEvento(TipoEvento.RecuperacionTemperatura,
                        "Perfil recuperado por enfriamiento",
                        $"{temp:F1}°C <= {thermalCfg.TempBaja}°C", temp);

                    UpdateStatusLabel(_lastProcessName, " ✓ Recuperado");
                    ShowNotification("✓ Temperatura Normal", $"Restaurando a {GetProfileName(newHotkey)}");
                }
            }
        }

        #region === EVENT HANDLERS ===

        private void OnTemperatureChanged(float temp) { }

        private void OnNewLogEvent(EventoHistorial evento)
        {
            this.InvokeIfRequired(() =>
            {
                if (_lstHistory != null)
                {
                    _lstHistory.Items.Insert(0, $"[{evento.Timestamp:HH:mm:ss}] {evento.Mensaje}");
                    while (_lstHistory.Items.Count > 50) _lstHistory.Items.RemoveAt(_lstHistory.Items.Count - 1);
                }
            });
        }

        private void OnConfigChanged(PlusControlData config)
        {
            this.InvokeIfRequired(() =>
            {
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
            if (!_isExiting && _configService.Config.CerrarMinimiza &&
                e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.WindowState = FormWindowState.Minimized;
                return;
            }

            _isExiting = true; // Fundamental para que el Task.Run del FocusLoop se detenga correctamente

            if (_configService.Config.RestaurarAlSalir)
            {
                foreach (var procName in _configService.Config.Reglas.Keys)
                {
                    _processService.RestoreProcess(procName);
                }
            }

            _monitorTimer?.Stop();
            _hardwareService.Dispose();
            _trayIcon?.Dispose();
            _configService.Save();
        }

        #endregion

        #region === UI UPDATES ===

        private void UpdateTemperatureDisplay(float temp)
        {
            this.InvokeIfRequired(() =>
            {
                if (_lblTempStatus == null) return;

                if (temp > 0)
                {
                    _lblTempStatus.Text = $"🌡️ {temp:F1}°C";
                    _lblTempStatus.ForeColor = temp >= _configService.Config.ControlTermico.TempAlta
                        ? Color.Salmon
                        : temp <= _configService.Config.ControlTermico.TempBaja
                            ? Color.LightSkyBlue
                            : Color.LightGray;
                }
                else
                {
                    _lblTempStatus.Text = "🌡️ N/D";
                    _lblTempStatus.ForeColor = Color.Gray;
                }

                UpdateChart(temp);
            });
        }

        private Queue<float> _tempHistory = new Queue<float>(60);
        private void UpdateChart(float temp)
        {
            if (_chartPanel == null || temp <= 0) return;

            _tempHistory.Enqueue(temp);
            if (_tempHistory.Count > 60) _tempHistory.Dequeue();

            _chartPanel.Invalidate();
        }

        private void UpdateStatusLabel(string process, string extra = "")
        {
            this.InvokeIfRequired(() =>
            {
                if (_lblStatus == null) return;

                var profileName = GetProfileName(_lastAppliedProfile);
                var status = _isStableAppConfigured ? "✓ Regla" : "○ Defecto";
                _lblStatus.Text = $"Focus: [{process}] → {profileName} | {status}{extra}";
            });
        }

        private void UpdateTrayIcon(float temp)
        {
            if (_trayIcon == null) return;

            var rankText = _currentActualRank.ToString();
            var iconColor = temp >= _configService.Config.ControlTermico.TempAlta
                ? Color.FromArgb(255, 80, 80)
                : temp <= _configService.Config.ControlTermico.TempBaja && temp > 0
                    ? Color.FromArgb(80, 200, 255)
                    : Color.White;

            var bmp = BitmapPool.Get();
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    g.Clear(Color.Transparent);
                    using (var font = new Font("Tahoma", 10, FontStyle.Bold))
                    {
                        using (var shadow = new SolidBrush(Color.Black))
                            g.DrawString(rankText, font, shadow, new PointF(1, 1));
                        using (var brush = new SolidBrush(iconColor))
                            g.DrawString(rankText, font, brush, new PointF(0, 0));
                    }
                }

                var hIcon = bmp.GetHicon();
                var newIcon = Icon.FromHandle(hIcon);
                var oldIcon = _trayIcon.Icon;

                _trayIcon.Icon = newIcon;
                _trayIcon.Text = $"PlusControl PRO | P{_currentActualRank} | {temp:F0}°C";

                if (oldIcon != null && oldIcon != SystemIcons.Application)
                {
                    DestroyIcon(oldIcon.Handle);
                    oldIcon.Dispose();
                }
            }
            finally
            {
                BitmapPool.Return(bmp);
            }
        }

        private void ShowNotification(string title, string message)
        {
            if (!_configService.Config.MostrarNotificaciones ||
                _configService.Config.ModoSilencioso) return;

            _trayIcon?.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }

        #endregion

        #region === CONFIGURACIÓN DE UI ===

        private void ConfigureDarkTheme()
        {
            var bgDark = Color.FromArgb(30, 30, 30);
            var bgPanel = Color.FromArgb(45, 45, 48);
            var textLight = Color.FromArgb(240, 240, 240);
            var accentBlue = Color.FromArgb(0, 122, 204);

            this.BackColor = bgDark;
            this.ForeColor = textLight;
            this.Size = new Size(720, 780);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Font = new Font("Segoe UI", 9.5f);

            var lblTitle = new Label
            {
                Text = "🖥️ PlusControl PRO",
                Location = new Point(20, 15),
                AutoSize = true,
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = accentBlue
            };
            this.Controls.Add(lblTitle);

            var btnSettings = new Button
            {
                Text = "⚙️",
                Location = new Point(655, 13),
                Size = new Size(35, 35),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 14),
                BackColor = bgPanel,
                ForeColor = textLight,
                Cursor = Cursors.Hand
            };
            btnSettings.FlatAppearance.BorderSize = 0;
            btnSettings.Click += BtnSettings_Click;
            this.Controls.Add(btnSettings);

            var grpThermal = new GroupBox
            {
                Text = "🌡️ Control Térmico Inteligente",
                Location = new Point(20, 55),
                Size = new Size(680, 130),
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };

            _chkEnableTemp = new CheckBox
            {
                Text = "Habilitar Control Automático",
                Location = new Point(15, 25),
                AutoSize = true,
                Checked = _configService.Config.ControlTermico.Enabled
            };
            _chkEnableTemp.CheckedChanged += (s, e) => SaveCurrentConfig();
            grpThermal.Controls.Add(_chkEnableTemp);

            AddThermalControls(grpThermal, bgPanel, textLight);
            this.Controls.Add(grpThermal);

            CreateRulesPanel(bgPanel, textLight, accentBlue);
            CreateHistoryPanel(bgPanel, textLight);
            CreateChartPanel(bgDark);
            CreateStatusBar(bgDark);
            CreateBottomButtons(accentBlue);
        }

        private void AddThermalControls(GroupBox container, Color bgPanel, Color textLight)
        {
            var cfg = _configService.Config.ControlTermico;

            container.Controls.Add(new Label { Text = "↓ Bajar si:", Location = new Point(15, 53), AutoSize = true });
            _numTempHigh = new NumericUpDown { Location = new Point(90, 50), Size = new Size(50, 23), BackColor = bgPanel, ForeColor = textLight, Value = cfg.TempAlta };
            container.Controls.Add(_numTempHigh);
            container.Controls.Add(new Label { Text = "°C por", Location = new Point(145, 53), AutoSize = true });
            _numSecHigh = new NumericUpDown { Location = new Point(185, 50), Size = new Size(45, 23), BackColor = bgPanel, ForeColor = textLight, Value = cfg.SegundosAlta };
            container.Controls.Add(_numSecHigh);
            container.Controls.Add(new Label { Text = "seg", Location = new Point(235, 53), AutoSize = true });

            container.Controls.Add(new Label { Text = "↑ Subir si:", Location = new Point(280, 53), AutoSize = true });
            _numTempLow = new NumericUpDown { Location = new Point(355, 50), Size = new Size(50, 23), BackColor = bgPanel, ForeColor = textLight, Value = cfg.TempBaja };
            container.Controls.Add(_numTempLow);
            container.Controls.Add(new Label { Text = "°C por", Location = new Point(410, 53), AutoSize = true });
            _numSecLow = new NumericUpDown { Location = new Point(450, 50), Size = new Size(45, 23), BackColor = bgPanel, ForeColor = textLight, Value = cfg.SegundosBaja };
            container.Controls.Add(_numSecLow);
            container.Controls.Add(new Label { Text = "seg", Location = new Point(500, 53), AutoSize = true });

            var lblRange = new Label
            {
                Text = $"Rango seguro: {cfg.TempBaja}°C - {cfg.TempAlta}°C",
                Location = new Point(15, 85),
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 150)
            };
            container.Controls.Add(lblRange);

            EventHandler saveHandler = (s, e) => SaveCurrentConfig();
            _numTempHigh.ValueChanged += saveHandler;
            _numSecHigh.ValueChanged += saveHandler;
            _numTempLow.ValueChanged += saveHandler;
            _numSecLow.ValueChanged += saveHandler;
        }

        private void CreateRulesPanel(Color bgPanel, Color textLight, Color accentBlue)
        {
            this.Controls.Add(new Label { Text = "📋 Reglas de Perfiles PC_F:", Location = new Point(20, 195), AutoSize = true });

            _lstReglas = new ListBox
            {
                Location = new Point(20, 220),
                Size = new Size(450, 180),
                BackColor = bgPanel,
                ForeColor = textLight,
                Font = new Font("Consolas", 9),
                BorderStyle = BorderStyle.None
            };
            RefreshRulesList();
            this.Controls.Add(_lstReglas);

            this.Controls.Add(new Label { Text = "Por defecto:", Location = new Point(485, 197), AutoSize = true, Font = new Font("Segoe UI", 8) });
            _cmbDefaultProfile = new ComboBox
            {
                Location = new Point(550, 195),
                Size = new Size(145, 25),
                BackColor = bgPanel,
                ForeColor = textLight,
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var key in ProfileOptions.Keys) _cmbDefaultProfile.Items.Add(key);
            _cmbDefaultProfile.SelectedItem = GetProfileName(_configService.Config.AtajoDefecto);
            _cmbDefaultProfile.SelectedIndexChanged += (s, e) =>
            {
                _configService.Config.AtajoDefecto = ProfileOptions[_cmbDefaultProfile.SelectedItem!.ToString()!];
                SaveCurrentConfig();
            };
            this.Controls.Add(_cmbDefaultProfile);

            int yPos = 415;
            this.Controls.Add(new Label { Text = "Proceso:", Location = new Point(20, yPos + 3), AutoSize = true });
            _txtProceso = new TextBox
            {
                Location = new Point(80, yPos),
                Size = new Size(130, 25),
                BackColor = bgPanel,
                ForeColor = textLight,
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(_txtProceso);

            _btnBrowse = new Button
            {
                Text = "🔍 Buscar...",
                Location = new Point(215, yPos - 1),
                Size = new Size(95, 27),
                FlatStyle = FlatStyle.Flat,
                BackColor = bgPanel,
                ForeColor = textLight,
                Cursor = Cursors.Hand
            };
            _btnBrowse.Click += BtnBrowse_Click;
            this.Controls.Add(_btnBrowse);

            this.Controls.Add(new Label { Text = "Perfil:", Location = new Point(315, yPos + 3), AutoSize = true });
            _cmbPerfil = new ComboBox
            {
                Location = new Point(360, yPos),
                Size = new Size(110, 25),
                BackColor = bgPanel,
                ForeColor = textLight,
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var key in ProfileOptions.Keys) _cmbPerfil.Items.Add(key);
            _cmbPerfil.SelectedIndex = 0;
            this.Controls.Add(_cmbPerfil);

            this.Controls.Add(new Label { Text = "Prioridad:", Location = new Point(20, yPos + 38), AutoSize = true });
            _cmbPrioridad = new ComboBox
            {
                Location = new Point(85, yPos + 35),
                Size = new Size(160, 25),
                BackColor = bgPanel,
                ForeColor = textLight,
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var key in PriorityOptions.Keys) _cmbPrioridad.Items.Add(key);
            _cmbPrioridad.SelectedIndex = 0;
            this.Controls.Add(_cmbPrioridad);

            _btnAffinity = new Button
            {
                Text = "⚡ Núcleos CPU",
                Location = new Point(255, yPos + 35),
                Size = new Size(120, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = bgPanel,
                ForeColor = textLight,
                Cursor = Cursors.Hand
            };
            _btnAffinity.Click += BtnAffinity_Click;
            this.Controls.Add(_btnAffinity);

            yPos += 75;
            _btnAdd = new Button
            {
                Text = "➕ Agregar",
                Location = new Point(20, yPos),
                Size = new Size(140, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = accentBlue,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _btnAdd.Click += BtnAdd_Click;
            this.Controls.Add(_btnAdd);

            _btnEdit = new Button
            {
                Text = "✏️ Editar",
                Location = new Point(170, yPos),
                Size = new Size(140, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _btnEdit.Click += BtnEdit_Click;
            this.Controls.Add(_btnEdit);

            _btnDelete = new Button
            {
                Text = "🗑️ Eliminar",
                Location = new Point(320, yPos),
                Size = new Size(140, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(190, 50, 50),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _btnDelete.Click += BtnDelete_Click;
            this.Controls.Add(_btnDelete);
        }

        private void CreateHistoryPanel(Color bgPanel, Color textLight)
        {
            this.Controls.Add(new Label { Text = "📜 Historial de Eventos:", Location = new Point(480, 220), AutoSize = true });

            _lstHistory = new ListBox
            {
                Location = new Point(480, 245),
                Size = new Size(215, 180),
                BackColor = bgPanel,
                ForeColor = textLight,
                Font = new Font("Consolas", 8),
                BorderStyle = BorderStyle.None
            };
            this.Controls.Add(_lstHistory);
        }

        private void CreateChartPanel(Color bgColor)
        {
            _chartPanel = new Panel
            {
                Location = new Point(480, 450),
                Size = new Size(215, 110),
                BackColor = Color.FromArgb(25, 25, 28),
                BorderStyle = BorderStyle.FixedSingle
            };
            _chartPanel.Paint += ChartPanel_Paint;
            this.Controls.Add(_chartPanel);

            this.Controls.Add(new Label
            {
                Text = "📈 Temperatura (últimos 60s)",
                Location = new Point(480, 430),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8)
            });
        }

        private void ChartPanel_Paint(object? sender, PaintEventArgs e)
        {
            if (_tempHistory.Count < 2) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var width = _chartPanel!.Width - 10;
            var height = _chartPanel.Height - 10;
            var points = _tempHistory.ToArray();
            var maxTemp = points.Max();
            var minTemp = points.Min();
            var range = Math.Max(maxTemp - minTemp, 1);

            using (var pen = new Pen(Color.FromArgb(0, 200, 255), 2))
            {
                var linePoints = points.Select((t, i) => new PointF(
                    5 + (i / (float)(points.Length - 1)) * width,
                    5 + height - ((t - minTemp) / range) * height
                )).ToArray();

                g.DrawLines(pen, linePoints);
            }

            using (var font = new Font("Segoe UI", 7))
            using (var brush = new SolidBrush(Color.Gray))
            {
                g.DrawString($"{maxTemp:F0}°", font, brush, 5, 5);
                g.DrawString($"{minTemp:F0}°", font, brush, 5, height - 12);
            }
        }

        private void CreateStatusBar(Color bgColor)
        {
            var statusBar = new Panel
            {
                Location = new Point(0, 700),
                Size = new Size(720, 50),
                BackColor = Color.FromArgb(20, 20, 22)
            };

            _lblTempStatus = new Label
            {
                Text = "🌡️ -- °C",
                Location = new Point(15, 15),
                AutoSize = true,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.LightGray
            };
            statusBar.Controls.Add(_lblTempStatus);

            _lblStatus = new Label
            {
                Text = "Inicializando...",
                Location = new Point(150, 18),
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.LightGray
            };
            statusBar.Controls.Add(_lblStatus);

            _lblStats = new Label
            {
                Text = "",
                Location = new Point(500, 18),
                AutoSize = true,
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.Gray
            };
            statusBar.Controls.Add(_lblStats);

            var statsTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            statsTimer.Tick += (s, e) =>
            {
                var resumen = _statistics.ObtenerResumen();
                _lblStats!.Text = $"Cambios: {resumen.TotalCambios} | Max: {resumen.TempMax:F0}°C";
            };
            statsTimer.Start();

            this.Controls.Add(statusBar);
        }

        private void CreateBottomButtons(Color accentBlue)
        {
            this.Controls.Add(new Label
            {
                Text = "✨ PlusControl PRO v2.0 by Breniak | Arquitectura Enterprise",
                Location = new Point(20, 660),
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Italic),
                ForeColor = Color.FromArgb(80, 80, 80)
            });
        }

        private void ConfigureSystemTray()
        {
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "PlusControl PRO",
                Visible = true
            };

            _trayIcon.DoubleClick += (s, e) =>
            {
                Show();
                WindowState = FormWindowState.Normal;
                ShowInTaskbar = true;
                BringToFront();
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("📊 Abrir Dashboard", null, (s, e) =>
            {
                Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; BringToFront();
            });
            menu.Items.Add("-");
            menu.Items.Add("📥 Exportar Config", null, (s, e) => ExportConfig());
            menu.Items.Add("📤 Importar Config", null, (s, e) => ImportConfig());
            menu.Items.Add("-");
            menu.Items.Add("❌ Salir", null, (s, e) => { _isExiting = true; Application.Exit(); });
            _trayIcon.ContextMenuStrip = menu;

            this.Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    ShowInTaskbar = false;
                }
            };
        }

        #endregion

        #region === HANDLERS DE BOTONES ===

        private void BtnSettings_Click(object? sender, EventArgs e)
        {
            ShowAdvancedSettings();
        }

        private void ShowAdvancedSettings()
        {
            var form = CreateSettingsForm();
            form.ShowDialog(this);
        }

        private Form CreateSettingsForm()
        {
            var form = new Form
            {
                Text = "⚙️ Ajustes Avanzados PRO",
                Size = new Size(420, 520),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(240, 240, 240)
            };

            var cfg = _configService.Config;
            int y = 20;

            form.Controls.Add(CreateSettingRow(form, "Retraso General (ms):", ref y, cfg.DelayGeneral, 0, 10000, out var numDelay));
            form.Controls.Add(CreateSettingRow(form, "Transición Rápida 1↔2 (ms):", ref y, cfg.Delay1y2, 0, 10000, out var numFast));
            form.Controls.Add(CreateSettingRow(form, "Frecuencia Escaneo Térmico (ms):", ref y, cfg.TickRate, 100, 5000, out var numTick));

            y += 10;

            form.Controls.Add(CreateCheckBox(form, "Iniciar minimizado", ref y, cfg.IniciarMinimizado, out var chkMin));
            form.Controls.Add(CreateCheckBox(form, "Cerrar (X) minimiza a bandeja", ref y, cfg.CerrarMinimiza, out var chkClose));
            form.Controls.Add(CreateCheckBox(form, "Siempre visible (TopMost)", ref y, cfg.SiempreArriba, out var chkTop));
            form.Controls.Add(CreateCheckBox(form, "Sonido beep al cambiar", ref y, cfg.ActivarBeep, out var chkBeep));
            form.Controls.Add(CreateCheckBox(form, "Modo silencioso (sin notif.)", ref y, cfg.ModoSilencioso, out var chkSilent));
            form.Controls.Add(CreateCheckBox(form, "Mostrar notificaciones toast", ref y, cfg.MostrarNotificaciones, out var chkToast));
            form.Controls.Add(CreateCheckBox(form, "Auto-detectar juegos", ref y, cfg.AutoDetectarJuegos, out var chkGames));
            form.Controls.Add(CreateCheckBox(form, "Restaurar al salir", ref y, cfg.RestaurarAlSalir, out var chkRestore));

            y += 10;
            form.Controls.Add(CreateSettingRow(form, "Opacidad ventana (%):", ref y, cfg.OpacidadVentana, 30, 100, out var numOpacity));

            var btnSave = new Button
            {
                Text = "💾 Guardar Ajustes",
                Location = new Point(110, y + 20),
                Size = new Size(180, 40),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += (s, e) =>
            {
                cfg.DelayGeneral = (int)numDelay!.Value;
                cfg.Delay1y2 = (int)numFast!.Value;
                cfg.TickRate = (int)numTick!.Value;
                cfg.IniciarMinimizado = chkMin!.Checked;
                cfg.CerrarMinimiza = chkClose.Checked;
                cfg.SiempreArriba = chkTop.Checked;
                cfg.ActivarBeep = chkBeep.Checked;
                cfg.ModoSilencioso = chkSilent.Checked;
                cfg.MostrarNotificaciones = chkToast.Checked;
                cfg.AutoDetectarJuegos = chkGames.Checked;
                cfg.RestaurarAlSalir = chkRestore.Checked;
                cfg.OpacidadVentana = (int)numOpacity!.Value;

                SaveCurrentConfig();
                form.Close();
            };

            form.Controls.Add(btnSave);
            form.TopMost = this.TopMost;
            return form;
        }

        private Label CreateSettingRow(Form form, string label, ref int y, int value, int min, int max, out NumericUpDown num)
        {
            form.Controls.Add(new Label { Text = label, Location = new Point(20, y), AutoSize = true });
            num = new NumericUpDown
            {
                Location = new Point(280, y - 3),
                Size = new Size(100, 25),
                Minimum = min,
                Maximum = max,
                Value = value,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };
            form.Controls.Add(num);
            y += 35;
            return new Label();
        }

        private CheckBox CreateCheckBox(Form form, string text, ref int y, bool checkedVal, out CheckBox chk)
        {
            chk = new CheckBox
            {
                Text = text,
                Location = new Point(20, y),
                AutoSize = true,
                Checked = checkedVal
            };
            form.Controls.Add(chk);
            y += 27;
            return chk!;
        }

        private void BtnBrowse_Click(object? sender, EventArgs e)
        {
            ShowProcessBrowser();
        }

        private void ShowProcessBrowser()
        {
            var form = new Form
            {
                Text = "🔍 Procesos Activos",
                Size = new Size(350, 480),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White
            };

            var panelSearch = new Panel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(10) };
            var txtSearch = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 11)
            };
            panelSearch.Controls.Add(txtSearch);

            var lstProcesses = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 10)
            };

            var allProcesses = Process.GetProcesses()
                .Select(p => new { Name = p.ProcessName, Memory = p.WorkingSet64 })
                .DistinctBy(p => p.Name)
                .OrderByDescending(p => p.Memory)
                .Select(p => $"{p.Name,-30} {(p.Memory / 1024 / 1024):n0} MB")
                .ToArray();

            lstProcesses.Items.AddRange(allProcesses);

            txtSearch.TextChanged += (s, e) =>
            {
                lstProcesses.Items.Clear();
                var filter = txtSearch.Text.ToLower();
                lstProcesses.Items.AddRange(
                    string.IsNullOrWhiteSpace(filter)
                        ? allProcesses
                        : allProcesses.Where(p => p.ToLower().Contains(filter)).ToArray()
                );
            };

            var btnSelect = new Button
            {
                Text = "✓ Seleccionar",
                Dock = DockStyle.Bottom,
                Height = 45,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnSelect.Click += (s, e) =>
            {
                if (lstProcesses.SelectedItem != null)
                {
                    var selected = lstProcesses.SelectedItem.ToString()?.Split(' ')[0];
                    _txtProceso!.Text = selected ?? "";
                    form.Close();
                }
            };
            lstProcesses.DoubleClick += (s, e) => btnSelect.PerformClick();

            form.Controls.Add(lstProcesses);
            form.Controls.Add(panelSearch);
            form.Controls.Add(btnSelect);
            form.Shown += (s, e) => txtSearch.Focus();
            form.TopMost = this.TopMost;
            form.ShowDialog(this);
        }

        private void BtnAffinity_Click(object? sender, EventArgs e)
        {
            ShowAffinityDialog();
        }

        private void ShowAffinityDialog()
        {
            var coreCount = Environment.ProcessorCount;
            var form = new Form
            {
                Text = "⚡ Configurar Afinidad de CPU",
                Size = new Size(450, 520),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White
            };

            form.Controls.Add(new Label { Text = "🎯 ACTIVO (Con Focus)", Location = new Point(20, 15), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
            var clbActive = new CheckedListBox
            {
                Location = new Point(20, 40),
                Size = new Size(185, 360),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None
            };

            form.Controls.Add(new Label { Text = "📱 SEGUNDO PLANO", Location = new Point(230, 15), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
            var clbBg = new CheckedListBox
            {
                Location = new Point(230, 40),
                Size = new Size(185, 360),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None
            };

            for (int i = 0; i < coreCount; i++)
            {
                var isActive = !_tempAffinityActive.HasValue || ((_tempAffinityActive.Value & (1L << i)) != 0);
                var isBg = !_tempAffinityBg.HasValue || ((_tempAffinityBg.Value & (1L << i)) != 0);
                clbActive.Items.Add($"CPU Core {i}", isActive);
                clbBg.Items.Add($"CPU Core {i}", isBg);
            }

            var btnSave = new Button
            {
                Text = "💾 Guardar Configuración",
                Location = new Point(20, 420),
                Size = new Size(395, 40),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnSave.Click += (s, e) =>
            {
                long maskActive = 0, maskBg = 0;
                for (int i = 0; i < coreCount; i++)
                {
                    if (clbActive.GetItemChecked(i)) maskActive |= (1L << i);
                    if (clbBg.GetItemChecked(i)) maskBg |= (1L << i);
                }

                _tempAffinityActive = (maskActive == ((1L << coreCount) - 1)) ? (long?)null : maskActive;
                _tempAffinityBg = (maskBg == ((1L << coreCount) - 1)) ? (long?)null : maskBg;

                _btnAffinity!.Text = (_tempAffinityActive.HasValue || _tempAffinityBg.HasValue)
                    ? "⚡ Personalizado"
                    : "⚡ Todos los núcleos";

                form.Close();
            };

            form.Controls.Add(clbActive);
            form.Controls.Add(clbBg);
            form.Controls.Add(btnSave);
            form.TopMost = this.TopMost;
            form.ShowDialog(this);
        }

        private void BtnAdd_Click(object? sender, EventArgs e)
        {
            var process = _txtProceso?.Text.Trim().ToLower();
            if (string.IsNullOrEmpty(process)) return;

            var selectedProfile = ProfileOptions[_cmbPerfil!.SelectedItem!.ToString()!];
            var selectedPriority = PriorityOptions[_cmbPrioridad!.SelectedItem!.ToString()!];

            _configService.Config.Reglas[process] = new PerfilConfig
            {
                Atajo = selectedProfile,
                Prioridad = selectedPriority,
                AfinidadActiva = _tempAffinityActive,
                AfinidadFondo = _tempAffinityBg,
                Notas = $"Creado el {DateTime.Now:yyyy-MM-dd}"
            };

            SaveCurrentConfig();
            _txtProceso?.Clear();
            _tempAffinityActive = null;
            _tempAffinityBg = null;
            _btnAffinity!.Text = "⚡ Núcleos CPU";
            RefreshRulesList();

            _logger.LogInfo($"Regla agregada: {process} -> {selectedProfile}");
        }

        private void BtnEdit_Click(object? sender, EventArgs e)
        {
            if (_lstReglas?.SelectedIndex == -1)
            {
                MessageBox.Show(this, "Selecciona una regla para editar", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedItem = _lstReglas?.SelectedItem?.ToString();
            if (selectedItem == null) return;

            var sepIndex = selectedItem.IndexOf(" → ");
            if (sepIndex <= 0) return;

            var process = selectedItem.Substring(0, sepIndex).Trim();
            if (!_configService.Config.Reglas.TryGetValue(process, out var config)) return;

            _txtProceso!.Text = process;
            var profileName = GetProfileName(config.Atajo);
            if (_cmbPerfil!.Items.Contains(profileName)) _cmbPerfil.SelectedItem = profileName;

            var priorityName = GetPriorityName(config.Prioridad);
            if (_cmbPrioridad!.Items.Contains(priorityName)) _cmbPrioridad.SelectedItem = priorityName;

            _tempAffinityActive = config.AfinidadActiva;
            _tempAffinityBg = config.AfinidadFondo;
            _btnAffinity!.Text = (_tempAffinityActive.HasValue || _tempAffinityBg.HasValue)
                ? "⚡ Personalizado"
                : "⚡ Núcleos CPU";
        }

        private void BtnDelete_Click(object? sender, EventArgs e)
        {
            if (_lstReglas?.SelectedIndex == -1) return;

            var selectedItem = _lstReglas?.SelectedItem?.ToString();
            if (selectedItem == null) return;

            var sepIndex = selectedItem.IndexOf(" → ");
            if (sepIndex <= 0) return;

            var process = selectedItem.Substring(0, sepIndex).Trim();
            if (_configService.Config.Reglas.Remove(process))
            {
                SaveCurrentConfig();
                RefreshRulesList();
                _logger.LogInfo($"Regla eliminada: {process}");
            }
        }

        private void ExportConfig()
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "JSON|*.json",
                FileName = $"pluscontrol_config_{DateTime.Now:yyyyMMdd}"
            };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                _configService.ExportToFile(dialog.FileName);
                MessageBox.Show(this, "Configuración exportada exitosamente", "Éxito", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ImportConfig()
        {
            using var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    _configService.ImportFromFile(dialog.FileName);
                    RefreshRulesList();
                    MessageBox.Show(this, "Configuración importada. Reinicie la aplicación para aplicar todos los cambios.", "Éxito", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Error importando: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        #endregion

        #region === UTILIDADES ===

        private void SaveCurrentConfig()
        {
            try
            {
                if (_cmbDefaultProfile != null)
                    _configService.Config.AtajoDefecto = ProfileOptions[_cmbDefaultProfile.SelectedItem?.ToString() ?? "BALANCED_TURBO / Perfil 3"];

                if (_chkEnableTemp != null)
                {
                    _configService.Config.ControlTermico.Enabled = _chkEnableTemp.Checked;
                    _configService.Config.ControlTermico.TempAlta = (int)(_numTempHigh?.Value ?? 80);
                    _configService.Config.ControlTermico.SegundosAlta = (int)(_numSecHigh?.Value ?? 15);
                    _configService.Config.ControlTermico.TempBaja = (int)(_numTempLow?.Value ?? 65);
                    _configService.Config.ControlTermico.SegundosBaja = (int)(_numSecLow?.Value ?? 15);
                }

                _configService.Save();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error guardando configuración", ex);
            }
        }

        private void RefreshRulesList()
        {
            _lstReglas?.Items.Clear();
            foreach (var rule in _configService.Config.Reglas)
            {
                var affText = (rule.Value.AfinidadActiva.HasValue || rule.Value.AfinidadFondo.HasValue) ? " | ⚡CPU" : "";
                var usageText = rule.Value.VecesUsado > 0 ? $" [{rule.Value.VecesUsado}x]" : "";
                _lstReglas?.Items.Add($"{rule.Key,-25} → {GetProfileName(rule.Value.Atajo),-18} | {GetPriorityName(rule.Value.Prioridad),-15}{affText}{usageText}");
            }
        }

        private string GetProfileName(string hotkey) =>
            ProfileOptions.FirstOrDefault(x => x.Value == hotkey).Key ?? "Desconocido";

        private string GetPriorityName(ProcessPriorityClass? priority) =>
            PriorityOptions.FirstOrDefault(x => x.Value == priority).Key ?? "Normal";

        private int HotkeyToRank(string hotkey) => hotkey switch
        {
            "%{NUMPAD1}" => 1,
            "%{NUMPAD2}" => 2,
            "%{NUMPAD3}" => 3,
            "%{NUMPAD4}" => 4,
            _ => 3
        };

        private string RankToHotkey(int rank) => rank switch
        {
            <= 1 => "%{NUMPAD1}",
            2 => "%{NUMPAD2}",
            3 => "%{NUMPAD3}",
            _ => "%{NUMPAD4}"
        };

        #endregion
    }

    #endregion

    #region === EXTENSIONES Y UTILIDADES ===

    public static class ControlExtensions
    {
        public static void InvokeIfRequired(this Control control, Action action)
        {
            if (control.InvokeRequired)
                control.Invoke(action);
            else
                action();
        }
    }

    public class ObjectPool<T> where T : class
    {
        private readonly Func<T> _factory;
        private readonly ConcurrentBag<T> _pool = new();
        private readonly int _maxSize;

        public ObjectPool(int maxSize, Func<T> factory)
        {
            _maxSize = maxSize;
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public T Get()
        {
            return _pool.TryTake(out var item) ? item : _factory();
        }

        public void Return(T item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            if (_pool.Count < _maxSize)
            {
                _pool.Add(item);
            }
            else
            {
                (item as IDisposable)?.Dispose();
            }
        }
    }

    #endregion

    #region === PUNTO DE ENTRADA ===

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    p.PriorityClass = ProcessPriorityClass.RealTime;
                }
            }
            catch
            {
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
            }

            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
            }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                File.AppendAllText("crash.log", $"{DateTime.Now}: {e.ExceptionObject}\n");
            };

            Application.ThreadException += (s, e) =>
            {
                File.AppendAllText("crash.log", $"{DateTime.Now}: {e.Exception}\n");
            };

            Application.Run(new MainForm());
        }
    }

    #endregion
}