PlusControl PRO 🧠 v2.0

PlusControl PRO es una solución avanzada de gestión térmica, prioridades y rendimiento para sistemas Windows. Actúa como el "cerebro" central de tu ecosistema de hardware, monitoreando constantemente el sistema y tomando decisiones inteligentes al milisegundo basadas en la ventana activa, la temperatura y reglas personalizadas.

✨ Novedades en la v2.0 (The Performance Update)

Esta versión incluye una reescritura total del motor de detección y una interfaz completamente nueva:

Soporte Multi-Subproceso (Arquitectura Chromium): Detecta y aplica reglas automáticamente a aplicaciones complejas como Microsoft Edge, Discord o Chrome, alterando la prioridad y afinidad de todos sus subprocesos de forma simultánea.

Afinidad de Núcleos (CPU Core Pinning): Decide exactamente qué núcleos del procesador usarán tus aplicaciones. Configura reglas separadas para cuando el proceso está en foco (activo) y cuando pasa a segundo plano.

Prioridad Dinámica: PlusControl PRO ahora baja los recursos de los programas minimizados (Priority: Idle) y le da toda la potencia de la CPU a tu juego o aplicación en pantalla.

Interfaz Moderna de Pestañas y Tarjetas: Adiós al texto plano. Nueva interfaz fluida con tarjetas (Cards) diseñadas a medida que extraen automáticamente los íconos originales de tus .exe.

Motor Asíncrono Ultra-Rápido: El monitoreo térmico y el escáner de ventanas (foco) han sido desacoplados. Ahora el programa reacciona a los cambios de ventana en un bucle de 50ms sin congelar la interfaz ni interferir con la lectura de hardware.

⚙️ Funcionalidades Principales

Gestión Inteligente de Perfiles: Cambio automático entre modos (MAX_PERFORMANCE, GAMING, BALANCED, BATTERY) a través de atajos directos al hardware.

Monitoreo en Tiempo Real: Integración profunda con LibreHardwareMonitor para lecturas precisas de temperatura del paquete (Package Temp).

Orquestador Master-Slave: Se comunica dinámicamente mediante Named Pipes con el motor de bajo nivel PC_F (Slave) para ajustar estados del procesador (voltajes, turbo, multiplicadores) sin intervención del usuario.

Protección Térmica (Thermal Throttling Seguro): Algoritmos de seguridad configurables que fuerzan perfiles de enfriamiento si se detectan temperaturas críticas por encima del tiempo establecido, y restauran el rendimiento al enfriarse.

Modo Gamer / Silencioso: Ejecución desde la bandeja del sistema con notificaciones nativas de Windows configurables.

🏗 Arquitectura del Ecosistema

El sistema se divide en dos componentes trabajando en simbiosis:

PlusControl PRO (El Cerebro - Master): Interfaz gráfica, detector de foco (Foreground App), lógica de reglas, asignación de núcleos, prioridad de Windows y control térmico. Envía comandos de perfil.

PC_F (El Músculo - Slave): Servicio invisible (o minimizado) que intercepta los comandos de PlusControl y escribe directamente en los registros MSR del procesador (Ring 0) a través de WinRing0x64.sys.



🔧 Instalación y Uso

Clona este repositorio en tu equipo local.

Abre la solución en Visual Studio 2022.

Asegúrate de tener instalado el SDK de .NET correspondiente.

Compila el proyecto en Release (x64).

IMPORTANTE: Ambos programas (PlusControl PRO y PC_F) deben ejecutarse con Privilegios de Administrador para poder leer los sensores de temperatura, modificar afinidades de CPU restringidas y escribir en los registros del procesador.

Engineered by Breniak — Rendimiento absoluto bajo control.