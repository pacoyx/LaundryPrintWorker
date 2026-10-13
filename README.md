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
    "WorkerKey": "BN123-456-789",
    "SignalRHubUrl": "https://api.midominio.com/printhub",
    "ApiBaseUrl": "https://api.midominio.com"
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

## Publicar e instalar como servicio de Windows

```bash
dotnet publish -c Release -o ./publish
```

Luego, desde una consola con permisos de administrador:

```powershell
sc create LaundryPrintWorker binPath="C:\ruta\publish\LaundryPrintWorker.exe"
sc start LaundryPrintWorker
```

Para desinstalarlo:

```powershell
sc stop LaundryPrintWorker
sc delete LaundryPrintWorker
```

## Dependencias principales

- [ESCPOS_NET](https://www.nuget.org/packages/ESCPOS_NET) — generación de comandos ESC/POS para impresoras térmicas.
- `Microsoft.AspNetCore.SignalR.Client` — cliente de SignalR para recibir notificaciones en tiempo real.
- `Microsoft.Extensions.Hosting.WindowsServices` — permite ejecutar el worker como servicio de Windows.
- `System.Drawing.Common` — soporte para impresión gráfica/raw en Windows.

## Notas de seguridad

- No subas `appsettings.Production.json` ni ningún archivo con credenciales/URLs internas reales; están excluidos vía `.gitignore`.
- El `WorkerKey` actúa como credencial del worker frente al backend; trátalo como secreto.
