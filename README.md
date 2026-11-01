# LaundryPrintWorker

Worker Service para Windows (.NET 9) que escucha trabajos de impresión de tickets en tiempo real vía **SignalR** y los envía a impresoras térmicas/locales instaladas en el equipo, usando comandos **ESC/POS**.

Forma parte del sistema de lavandería: el backend publica trabajos de impresión en una cola, este worker se conecta al Hub de SignalR, recibe el aviso, descarga el detalle del ticket por HTTP, lo formatea y lo imprime en la impresora indicada.

## Arquitectura

```
Program.cs                 -> Composición de servicios (Generic Host)
Worker.cs                   -> BackgroundService: ciclo de vida, conexión SignalR, heartbeat
Services/
  SignalRConnectionService  -> Construye y expone la conexión al Hub (HubConnection)
  PrintApiClient             -> Cliente HTTP: obtiene trabajos, marca completado/error, reporta impresoras
  TicketPrinterService       -> Formatea el ticket (ESCPOS_NET) y lo envía a la impresora
  RawPrinterHelper            -> Envío de bytes "raw" a la impresora vía Win32 (winspool)
Models/
  PrintWorkerOptions         -> Configuración de conexión (WorkerKey, SignalRHubUrl, ApiBaseUrl)
  PrintJobDto                -> Datos del trabajo de impresión recibido desde la API
  TicketSettings              -> Datos de cabecera/pie del ticket (empresa, dirección, mensajes legales)
```

### Flujo de trabajo

1. Al iniciar, el worker se conecta al Hub de SignalR (`SignalRHubUrl`) y se registra con `WorkerKey` + nombre del equipo (`RegisterPrinter`).
2. Descubre las impresoras instaladas localmente (excluyendo impresoras virtuales como "Microsoft Print to PDF", "OneNote", "Fax", etc.) y las reporta al backend (`DiscoverPrintersAsync`).
3. Permanece a la espera del evento `PrintAvailable`. Al recibirlo:
   - Descarga el detalle del trabajo (`GET api/printjobs/{queueId}`).
   - Formatea e imprime el ticket en la impresora indicada.
   - Marca el trabajo como completado (`POST api/printjobs/{queueId}/completed`) o como error (`POST api/printjobs/{queueId}/error`) si algo falla.
4. Envía un `Heartbeat` cada 30 segundos mientras la conexión esté activa.

### Comportamiento ante fallas de conexión

El worker está diseñado para **nunca caerse por falta de internet o por el backend caído**, ni al iniciar ni mientras corre:

- **Conexión inicial / registro:** si al arrancar no logra conectarse al Hub o registrarse (`RegisterPrinter`), no se cae — queda reintentando cada 15 segundos de forma indefinida hasta lograrlo. Útil para el caso típico de que la PC arranque antes de que el router/internet esté listo.
- **Conexión ya establecida:** usa `WithAutomaticReconnect()` de SignalR, pero esta función solo reintenta unas pocas veces (0s, 2s, 10s, 30s) y luego se rinde para siempre si no logra reconectar. Para cubrir ese caso, el worker escucha el evento `Closed` y, si la conexión se cierra definitivamente, vuelve a ejecutar el mismo ciclo de registro indefinido desde cero (igual que al arrancar).
- **PC suspendida (sleep/hibernación):** cuando Windows suspende el equipo, el socket de SignalR muere y la reconexión automática puede no notarlo a tiempo. El worker escucha el evento del sistema operativo `SystemEvents.PowerModeChanged` y, apenas la PC despierta, fuerza una reconexión y vuelve a reportar las impresoras instaladas — sin esperar a que falle un Heartbeat para darse cuenta.
- **Sincronización de impresoras y Heartbeat:** si fallan (ej. backend caído momentáneamente), solo se registra un error en el log; el worker sigue corriendo y lo vuelve a intentar en el siguiente ciclo, sin reiniciarse.

Es decir, el servicio de Windows **debería quedarse siempre en estado `RUNNING`**, incluso sin internet — simplemente no va a poder recibir ni imprimir trabajos hasta que la conexión vuelva, y lo retomará solo, sin que alguien tenga que reiniciarlo manualmente.

> **Recomendación de todas formas:** en la configuración de energía de Windows de la PC donde corre este worker, pon "Poner el equipo en suspensión" en **Nunca** (el monitor/pantalla sí se puede apagar normalmente, eso no afecta al servicio). Suspender el equipo completo corta la red y puede dejar la impresora USB en estado de ahorro de energía; la reconexión automática ayuda, pero evitar la suspensión del equipo es más confiable para un equipo de impresión que debe estar siempre disponible.

## Requisitos

- Windows 10/11 (usa APIs de impresión de Windows vía `System.Drawing.Printing` y `winspool`).
- [.NET SDK 9.0](https://dotnet.microsoft.com/download) o superior.
- Una o más impresoras instaladas en el equipo (idealmente térmicas, compatibles ESC/POS).
- Acceso de red al backend (API REST + Hub de SignalR).

## Configuración

La configuración se define en `appsettings.json` (y se puede sobreescribir por ambiente, por ejemplo `appsettings.Development.json`):

```json
{
  "WorkerSettings": {
    "WorkerKey": "<CAMBIAR-POR-EL-WORKERKEY-DE-ESTE-EQUIPO>",
    "SignalRHubUrl": "<CAMBIAR-POR-LA-URL-DEL-HUB-DEL-BACKEND>",
    "ApiBaseUrl": "<CAMBIAR-POR-LA-URL-BASE-DEL-BACKEND>"
  },
  "TicketSettings": {
    "CompanyName": "Lavanderia Kyo",
    "Address": "...",
    "Phone": "...",
    "Schedule": "...",
    "Website": "...",
    "LegalLines": ["..."],
    "ThanksMessage": "GRACIAS POR SU PREFERENCIA",
    "ControlCopyTitle": "CONTROL ADMINISTRATIVO"
  }
}
```

| Clave | Descripción |
|---|---|
| `WorkerSettings.WorkerKey` | Identificador único de este worker/sucursal, usado para registrarse en el Hub. |
| `WorkerSettings.SignalRHubUrl` | URL del Hub de SignalR que notifica nuevos trabajos de impresión. |
| `WorkerSettings.ApiBaseUrl` | URL base de la API REST usada para consultar y actualizar trabajos de impresión. |
| `TicketSettings.*` | Datos de la empresa y textos legales impresos en el ticket. |

> Los valores reales de producción (URLs, `WorkerKey`, etc.) no deben commitearse. Usa `appsettings.Development.json` para desarrollo local y variables de entorno o `appsettings.Production.json` (ignorado por git) para producción.

## Cómo ejecutar en desarrollo

```bash
dotnet restore
dotnet build
dotnet run
```

El worker se conectará usando los valores combinados de `appsettings.json` + `appsettings.Development.json` (perfil `Development`).

## Publicar

Para llevar el worker a otra PC (ej. una impresora de sucursal) sin depender de que esa máquina tenga el .NET Runtime instalado, publicar como **autocontenido en un solo archivo**:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

Esto genera `./publish/LaundryPrintWorker.exe` (incluye el runtime de .NET embebido) junto con `appsettings.json`. Copia toda la carpeta `./publish` a la PC destino (USB, red, etc.) y ajusta ahí los valores reales de `WorkerSettings` antes de arrancarlo (ver sección "Configuración").

Alternativa más liviana, pero requiere .NET 9 Runtime ya instalado en la PC destino:

```bash
dotnet publish -c Release -o ./publish
```

## Cómo correrlo en la PC destino: ejecutable directo vs. servicio de Windows

**Opción 1 — Doble clic al `.exe`:** corre en una ventana de consola visible. Útil solo para probar que conecta bien al backend (revisar que no tire errores de configuración). Si se cierra la ventana, se cierra sesión de Windows, o la PC se reinicia, el worker deja de escuchar trabajos de impresión.

**Opción 2 — Instalarlo como servicio de Windows (recomendado para dejarlo en producción):** corre en segundo plano, sin depender de una sesión iniciada, y puede configurarse para arrancar solo con la PC.

Desde una consola de **PowerShell como administrador**, parado en la carpeta donde quedó el `.exe`:

```powershell
sc.exe create LaundryPrintWorker binPath= "C:\ruta\publish\LaundryPrintWorker.exe" start= auto
sc.exe start LaundryPrintWorker
sc.exe query LaundryPrintWorker
```

> **Importante:** usa siempre `sc.exe`, no `sc` a secas — en PowerShell, `sc` es un alias de `Set-Content` (no el comando de servicios de Windows), y lo va a interpretar mal (`binpath=...` como si fuera un parámetro de archivo). También respeta el espacio después del `=` en cada opción (`binPath= "valor"`); sin ese espacio `sc.exe` no lo parsea bien.

El flag `start= auto` es necesario para que Windows lo levante solo en cada arranque — sin él, `sc.exe create` lo deja en modo manual.

Para desinstalarlo:

```powershell
sc.exe stop LaundryPrintWorker
sc.exe delete LaundryPrintWorker
```

### Hacer que se reinicie solo si se cae

Como defensa adicional a la resiliencia de reconexión del propio worker (ver sección siguiente), puedes configurar que Windows reinicie el servicio automáticamente si llega a caerse por cualquier otra razón no contemplada:

```powershell
sc.exe failure LaundryPrintWorker reset= 86400 actions= restart/60000/restart/60000/restart/60000
```

### Solución de problemas

Si el servicio queda en `STOPPED` después de `sc.exe start`, revisa el Visor de Eventos de Windows para ver la excepción real:

```powershell
Get-WinEvent -LogName Application -MaxEvents 30 | Where-Object { $_.Message -like "*LaundryPrintWorker*" -or $_.Message -like "*Worker*" } | Sort-Object TimeCreated | Format-List TimeCreated, LevelDisplayName, Message
```

Causa común: el `appsettings.json` que quedó junto al `.exe` todavía tiene los valores de ejemplo (placeholders `<CAMBIAR-...>`) en vez de los datos reales del backend/equipo — revisa ese archivo antes de asumir que es un problema de red o del código.

Si después de corregir `WorkerSettings:WorkerKey` y reiniciar el servicio los logs siguen mostrando la clave vieja, verifica en ese orden:
1. Que estés editando el `appsettings.json` **sin sufijo** (el que siempre se carga) y no solo el `appsettings.Development.json` — este último solo se usa si la variable de entorno `DOTNET_ENVIRONMENT=Development` está seteada, lo cual no es el caso por defecto.
2. Que el proceso realmente haya reiniciado (`Get-Process LaundryPrintWorker | Select StartTime` — compara con la hora del reinicio) y no sea un proceso viejo que quedó colgado.
3. Que no estés mirando una entrada vieja del Visor de Eventos: filtra por fecha reciente, no solo por los últimos N eventos (`Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='LaundryPrintWorker'; StartTime=(Get-Date).AddMinutes(-15)}`).
4. **Al volver a publicar (`dotnet publish`)**, el `appsettings.json` del repo se regenera con los valores placeholder — copia solo el `.exe` (y `.pdb` si quieres símbolos de depuración) a la PC destino, nunca sobreescribas el `appsettings.json` que ya tiene los valores reales de esa PC.

## Dependencias principales

- [ESCPOS_NET](https://www.nuget.org/packages/ESCPOS_NET) — generación de comandos ESC/POS para impresoras térmicas.
- `Microsoft.AspNetCore.SignalR.Client` — cliente de SignalR para recibir notificaciones en tiempo real.
- `Microsoft.Extensions.Hosting.WindowsServices` — permite ejecutar el worker como servicio de Windows.
- `System.Drawing.Common` — soporte para impresión gráfica/raw en Windows.

## Notas de seguridad

- No subas `appsettings.Production.json` ni ningún archivo con credenciales/URLs internas reales; están excluidos vía `.gitignore`.
- El `WorkerKey` actúa como credencial del worker frente al backend; trátalo como secreto.

## Licencia

Software propietario de Lavanderia Kyo. Todos los derechos reservados — ver [LICENSE](./LICENSE).
