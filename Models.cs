using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PlusControl.Pro
{
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

    public class PlusControlData
    {
        public int Version { get; set; } = 2;
        public DateTime LastModified { get; set; } = DateTime.Now;

        public string AtajoDefecto { get; set; } = "%{NUMPAD3}";

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

        public EstadisticasApp Estadisticas { get; set; } = new EstadisticasApp();
    }

    public class PerfilConfig
    {
        public string Atajo { get; set; } = "";

        // Prioridad cuando está en foco
        public ProcessPriorityClass? Prioridad { get; set; }

        // NUEVO: Prioridad cuando está en segundo plano (Fondo)
        public ProcessPriorityClass? PrioridadFondo { get; set; } = ProcessPriorityClass.Idle;

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
}