# PlusControl Pro 🧠

**PlusControl Pro** es una solución avanzada de gestión térmica y de rendimiento para sistemas Windows. Actúa como el cerebro del ecosistema, monitoreando constantemente los sensores de hardware y tomando decisiones inteligentes basadas en perfiles personalizados.

## ✨ Funcionalidades Principales
- **Gestión Inteligente de Perfiles:** Cambio automático entre modos (ECO, Balanceado, Gaming) según la carga de trabajo o la aplicación en foco.
- **Monitoreo en Tiempo Real:** Integración con `LibreHardwareMonitor` para obtener lecturas precisas de temperatura, carga y voltajes.
- **Orquestador Master-Slave:** Controla dinámicamente al motor **PC_F** para ajustar los estados del procesador sin intervención del usuario.
- **Interfaz Moderna:** UI oscura optimizada, intuitiva y ligera, diseñada para entusiastas del hardware.
- **Protección Térmica:** Algoritmos de seguridad que fuerzan perfiles de enfriamiento si se detectan temperaturas críticas.

## 🏗 Arquitectura
El sistema se divide en dos componentes:
1. **PlusControl (Master):** Analiza y decide.
2. **PC_F (Slave):** Ejecuta los cambios a nivel de registro de procesador.

## 🔧 Instalación
1. Clona el repositorio.
2. Compila en Visual Studio 2022 (.NET Core).
3. Ejecuta con privilegios de Administrador (requerido para el control de registros MSR).

---
*Engineered by Breniak - Rendimiento bajo control.*
