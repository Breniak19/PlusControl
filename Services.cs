using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace PlusControl.Pro
{
    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) { foreach (var hw in computer.Hardware) hw.Accept(this); }
        public void VisitHardware(IHardware hardware) { try { hardware.Update(); } catch { } foreach (var sub in hardware.SubHardware) sub.Accept(this); }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

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
            catch { _isAvailable = false; }
        }

        public void Update()
        {
            if (_computer == null || !_isAvailable) return;
            if (DateTime.Now - _lastUpdate < _cacheDuration) return;

            try
            {
                _computer.Accept(_visitor);
                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

                if (cpu != null)
                {
                    ISensor? sensor = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Package"))
                                      ?? cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);

                    if (sensor?.Value.HasValue == true)
                    {
                        var oldTemp = _currentTemp;
                        _currentTemp = sensor.Value.Value;
                        _lastUpdate = DateTime.Now;

                        if (Math.Abs(_currentTemp - oldTemp) > 0.5f)
                            OnTemperatureChanged?.Invoke(_currentTemp);
                    }
                }
            }
            catch { _isAvailable = false; }
        }

        public void Dispose() { try { _computer?.Close(); } catch { } }
    }

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
        private readonly ConcurrentDictionary<int, ProcessInfo> _processCache = new();
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(5);

        public ProcessInfo? GetForegroundProcess()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return null;

            if (_processCache.TryGetValue((int)pid, out var cached) && DateTime.Now - cached.DetectedAt < _cacheExpiry)
                return cached;

            try
            {
                var process = Process.GetProcessById((int)pid);
                var info = new ProcessInfo { ProcessId = (int)pid, ProcessName = process.ProcessName.ToLower(), MainWindowTitle = process.MainWindowTitle ?? "", DetectedAt = DateTime.Now };
                _processCache.AddOrUpdate((int)pid, info, (_, __) => info);

                foreach (var item in _processCache.Where(x => DateTime.Now - x.Value.DetectedAt > _cacheExpiry).ToList())
                    _processCache.TryRemove(item.Key, out _);

                return info;
            }
            catch { return null; }
        }

        public void ApplyProfile(ProcessInfo process, PerfilConfig config, bool isManagedRule = false)
        {
            Task.Run(() =>
            {
                try
                {
                    // FIX MÚLTIPLES PROCESOS (Chromium/Edge/Discord)
                    var processes = Process.GetProcessesByName(process.ProcessName);
                    bool anyApplied = false;

                    foreach (var proc in processes)
                    {
                        try
                        {
                            if (proc.HasExited) continue;

                            if (config.Prioridad.HasValue)
                                proc.PriorityClass = config.Prioridad.Value;
                            else if (isManagedRule && proc.PriorityClass == ProcessPriorityClass.Idle)
                                proc.PriorityClass = ProcessPriorityClass.Normal;

                            if (config.AfinidadActiva.HasValue)
                                proc.ProcessorAffinity = (IntPtr)config.AfinidadActiva.Value;
                            else if (isManagedRule)
                                proc.ProcessorAffinity = (IntPtr)GetAllCoresMask();

                            anyApplied = true;
                        }
                        catch { /* Ignorar bloqueos de sistema en subprocesos aislados */ }
                        finally { proc.Dispose(); } // Limpieza RAM
                    }

                    if (anyApplied) config.VecesUsado++;
                }
                catch { }
            });
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

                            // APLICA LA PRIORIDAD DE FONDO CONFIGURADA (O Idle por defecto)
                            p.PriorityClass = config.PrioridadFondo ?? ProcessPriorityClass.Idle;
                        }
                        catch { }
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

        public void AplicarPerfilHardware(string legacyHotkey)
        {
            int perfilId = legacyHotkey switch { "%{NUMPAD1}" => 1, "%{NUMPAD2}" => 2, "%{NUMPAD4}" => 4, _ => 3 };

            Task.Run(() =>
            {
                try
                {
                    using (NamedPipeClientStream pipeClient = new NamedPipeClientStream(".", "PlusControlPipe", PipeDirection.Out))
                    {
                        pipeClient.Connect(500);
                        using (StreamWriter writer = new StreamWriter(pipeClient) { AutoFlush = true })
                        {
                            writer.WriteLine($"PERFIL:{perfilId}");
                        }
                    }
                }
                catch { /* Ignorar errores de conexión al PC_F */ }
            });
        }

        public long GetAllCoresMask() => (1L << Environment.ProcessorCount) - 1;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

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
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public event Action<PlusControlData>? OnConfigChanged;
        public PlusControlData Config => _config;

        public ConfigurationService(string basePath) { _configPath = Path.Combine(basePath, "config.json"); Load(); }

        public void Save()
        {
            try
            {
                _config.LastModified = DateTime.Now;
                File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, _jsonOptions));
                OnConfigChanged?.Invoke(_config);
            }
            catch { }
        }

        public void Load()
        {
            if (!File.Exists(_configPath)) { _config.Reglas["steam"] = new PerfilConfig { Atajo = "%{NUMPAD1}", Prioridad = ProcessPriorityClass.High }; Save(); return; }
            try { var data = JsonSerializer.Deserialize<PlusControlData>(File.ReadAllText(_configPath), _jsonOptions); if (data != null) _config = data; }
            catch { _config = new PlusControlData(); }
        }

        public void CreateBackup() { try { var dir = Path.Combine(Path.GetDirectoryName(_configPath)!, "backups"); Directory.CreateDirectory(dir); File.Copy(_configPath, Path.Combine(dir, $"config_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json"), true); } catch { } }
        public void ExportToFile(string path) => File.WriteAllText(path, JsonSerializer.Serialize(_config, _jsonOptions));
        public void ImportFromFile(string path) { CreateBackup(); var imported = JsonSerializer.Deserialize<PlusControlData>(File.ReadAllText(path), _jsonOptions); if (imported != null) { _config = imported; Save(); } }
    }

    public interface IStatisticsService
    {
        void RecordTemperature(float temp);
        void RecordProfileChange(string processName, string profile);
        void RecordProcessDetected(string processName);
        EstadisticasResumen ObtenerResumen();
    }

    public class StatisticsService : IStatisticsService
    {
        private readonly PlusControlData _config;
        private float _tempSum = 0f;
        private int _tempCount = 0;

        public StatisticsService(IConfigurationService configService) { _config = configService.Config; }

        public void RecordTemperature(float temp)
        {
            if (temp <= 0) return;
            _tempSum += temp; _tempCount++;
            if (temp > _config.Estadisticas.TemperaturaMaxima) _config.Estadisticas.TemperaturaMaxima = temp;
            if (temp < _config.Estadisticas.TemperaturaMinima) _config.Estadisticas.TemperaturaMinima = temp;
            _config.Estadisticas.MuestrasTemperatura = _tempCount;
            _config.Estadisticas.TemperaturaPromedio = _tempSum / _tempCount;
        }

        public void RecordProfileChange(string processName, string profile) => _config.Estadisticas.TotalCambiosPerfil++;
        public void RecordProcessDetected(string processName)
        {
            if (!_config.Estadisticas.ProcesosDetectados.ContainsKey(processName)) _config.Estadisticas.ProcesosDetectados[processName] = 0;
            _config.Estadisticas.ProcesosDetectados[processName]++;
        }

        public EstadisticasResumen ObtenerResumen() => new EstadisticasResumen
        {
            TempMax = _config.Estadisticas.TemperaturaMaxima,
            TempMin = _config.Estadisticas.TemperaturaMinima > 999 ? 0 : _config.Estadisticas.TemperaturaMinima,
            TempAvg = _config.Estadisticas.TemperaturaPromedio,
            TotalCambios = _config.Estadisticas.TotalCambiosPerfil,
            ProcesoMasUsado = _config.Estadisticas.ProcesosDetectados.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "N/A",
            TiempoActivo = DateTime.Now - _config.Estadisticas.InicioSesion
        };
    }
}