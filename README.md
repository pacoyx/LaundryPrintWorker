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
  "Seq": {
    "Url": "<CAMBIAR-POR-LA-URL-DEL-SEQ-DEL-BACKEND-O-DEJAR-VACIO-PARA-DESACTIVAR>",
    "ApiKey": "<CAMBIAR-POR-EL-API-KEY-DE-INGESTA-DE-SEQ>"
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
| `Seq.Url` | URL del servidor Seq para centralizar logs (ej. `http://161.35.234.43:8787`, el mismo que usa el backend). Si se deja vacío, el worker simplemente no envía nada a Seq -no falla ni se cae por eso. |
| `Seq.ApiKey` | API key de ingesta de ese Seq. Opcional si el servidor acepta ingesta sin autenticación. |
| `TicketSettings.*` | Datos de la empresa y textos legales impresos en el ticket. |

## Logs

Además del Event Log de Windows (donde siguen apareciendo Warnings/Errors como antes), el worker ahora escribe logs en dos lugares más:

- **Archivo local**: `logs/log-AAAAMMDD.txt`, junto al `.exe` (rota todos los días, guarda los últimos 14 días). Se puede abrir directo con el Bloc de notas en la PC donde está instalado, sin necesidad de red ni de nada más -útil si Seq no es alcanzable justo quieres ver qué pasó.
- **Seq** (si se configuró `Seq.Url`): mismo servidor centralizado que ya usa el backend, para ver los logs de todos los workers y del backend en un solo lugar, con filtros y búsqueda.

### Protección contra cuelgues (timeouts)

Las llamadas a Windows (`WritePrinter` y similares, usadas para imprimir) son nativas y no se pueden cancelar de verdad si se traban (impresora sin papel, atascada, desconectada). Por eso:

- **Imprimir un ticket tiene un límite de 20 segundos.** Si no termina en ese plazo, se registra un error (visible en el log/Seq) y el worker sigue funcionando normal para el siguiente trabajo, en vez de quedarse colgado para siempre.
- **El Heartbeat a SignalR tiene un límite de 10 segundos** por el mismo motivo (conexiones "zombie" en redes inestables).
- **Las llamadas HTTP al backend tienen un límite de 30 segundos** (antes no tenían límite explícito, usaban el default de .NET de 100 segundos).

Si el worker se sigue colgando después de esto, revisa los logs (archivo o Seq) justo antes del cuelgue -ahora debería quedar registrado el motivo exacto en vez de quedar a ciegas.

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

## Hoja de ruta: imprimir desde tablet/celular sin instalar nada (impresora de red + appliance tipo Raspberry Pi)

> **Estado: planeado, todavía no implementado.** Esta sección documenta la discusión y el plan acordado para retomarlo más adelante -no describe comportamiento actual del código.

### Contexto / por qué

Hoy este worker solo puede correr en una PC Windows con la impresora instalada localmente (USB + driver de Windows). Eso tiene dos problemas si el sistema se usa desde una tablet/celular, o si se quiere comercializar como SaaS:

1. **Tablet/celular no pueden correr este worker.** Sin él, la única forma de imprimir desde esos dispositivos es el preview HTML del navegador (`window.print()`), que da un ticket feo y desperdicia papel -no usa ESC/POS real.
2. **No escala como SaaS.** Instalar el worker hoy significa ir a cada PC del cliente y editar `appsettings.json` a mano (ver sección "Configuración" más arriba). Insostenible si el sistema crece a muchos clientes/sucursales.

**Importante, ya evaluado:** el backend vive en un servidor en internet, y la impresora (aunque tenga WiFi/Ethernet) tiene una IP privada de la LAN de la sucursal. El backend **no puede** mandarle bytes directo a esa IP -no es ruteable desde internet sin abrir puertos (inseguro/frágil, no se adoptó). Por eso sigue haciendo falta *algún* dispositivo físico en la LAN de la sucursal que sirva de puente entre el backend (en la nube) y la impresora (en la LAN) -la pregunta no es "¿cómo eliminamos ese dispositivo?" sino "¿cómo lo hacemos invisible para el cliente?".

**Referencia real de la industria:** así es como lo resuelve Odoo. Su "IoT Box" es una Raspberry Pi preconfigurada que Odoo vende/envía; el cliente solo la enchufa a la red, y el navegador (desde cualquier laptop/tablet en esa misma LAN) le habla directo a la IoT Box para imprimir -nunca instala nada. Es la misma idea que seguimos acá, solo que empaquetada como producto en vez de un instalador manual.

Otras alternativas que se evaluaron y se descartaron o pospusieron (quedan documentadas por si se revisan después):
- **WebUSB / Web Bluetooth desde el navegador**: solo funciona en Chrome/Android, no en iOS/Safari -no sirve si se quiere soportar iPad.
- **App nativa (Capacitor) + SDK Bluetooth del fabricante**: funciona con impresoras sin red (USB/Bluetooth), pero obliga a empaquetar/firmar/actualizar una app nativa aparte. Se deja como alternativa futura si hace falta soportar impresoras sin WiFi/Ethernet.
- **Star CloudPRNT / Epson ePOS-Print**: la impresora misma consulta a un servidor en la nube por HTTP saliente, eliminando el dispositivo puente por completo. Atractivo, pero ata el catálogo de impresoras soportadas a esos fabricantes específicos. Vale la pena reconsiderar si en el futuro enviar hardware (la Pi) a cada cliente se vuelve el cuello de botella.

### Plan en 3 capas

1. **Capa 1 - transporte de red (siguiente paso, empezar por acá):** reemplazar el envío de bytes vía `winspool` (Windows) por un socket TCP directo a la impresora, para poder correr este worker en Linux/Raspberry Pi.
2. **Capa 2 - emparejamiento sin tocar config a mano:** que instalar sea "enchufar + un clic en la app", no editar `appsettings.json` por sucursal.
3. **Capa 3 - entrega de hardware:** imagen de Raspberry Pi OS preconfigurada y lista para flashear.

### Capa 1 en detalle (lo próximo a implementar)

**Pendiente antes de poder probar en la práctica:**
- Conseguir una impresora térmica 80mm con WiFi o Ethernet (puerto raw **9100**, protocolo JetDirect -el que soportan casi todas las térmicas de red).
- Conseguir una Raspberry Pi (4 o 5, 64-bit OS) para correr el worker fuera de Windows.

**Cambios de código que hacen falta:**

1. **Backend (`24dex-backend-comercial`)**:
   - Agregar a `Printers` un campo de tipo de conexión (ej. `ConnectionType`: `UsbWindowsDriver` | `NetworkRaw`) y `IpAddress` / `Port` (nullable, solo aplican si es `NetworkRaw`). Requiere migración EF Core.
   - Exponer esos campos en los DTOs que ya consume el worker (`GetPrinterConnectionStatus`, el endpoint de discovery).

2. **Este worker (`LaundryPrintWorker`)**:
   - Nueva clase `Services/NetworkPrinterClient.cs`: abre un `TcpClient` a `ip:puerto` (default 9100) y escribe los mismos bytes ESC/POS que hoy genera `TicketPrinterService`. Debe replicar el mismo patrón de timeout que ya existe ahí (hilo aparte + `Task.WhenAny` contra `Task.Delay`) para no colgar el worker si la impresora no responde.
   - `TicketPrinterService.PrintAsync` / `PrintTestAsync` deben recibir el tipo de conexión (o el `Printer` completo) y elegir entre `RawPrinterHelper.SendBytesToPrinter` (como hoy, solo Windows) o `NetworkPrinterClient.SendAsync` (nuevo, cross-platform).
   - `DiscoverPrintersAsync` hoy depende de `PrinterSettings.InstalledPrinters` (API de Windows) -no aplica a impresoras de red. Para la Capa 1 no es bloqueante: alcanza con ingresar la IP manualmente una vez; el auto-descubrimiento en la LAN queda como mejora futura.
   - El resto (conexión SignalR, `PrintApiClient`, cola de impresión, heartbeat, reconexión ante fallas) **no cambia nada** -ya es cross-platform tal cual está.

**Cómo validarlo cuando se retome:**
1. Conectar la impresora de red a la misma LAN que la Raspberry Pi (o que la PC de pruebas) y anotar su IP fija.
2. Confirmar que responde en el puerto 9100 (`Test-NetConnection <ip> -Port 9100` en Windows, o `nc -zv <ip> 9100` en Linux).
3. Implementar `NetworkPrinterClient` y el branching en `TicketPrinterService`.
4. Aplicar la migración del backend y registrar manualmente un `Printer` de prueba con `ConnectionType = NetworkRaw`.
5. Publicar el worker para ARM64 (`dotnet publish -c Release -r linux-arm64 --self-contained true`), copiarlo a la Pi, correrlo como servicio `systemd` (ver sección "Cómo correrlo..." más arriba, pero con el equivalente Linux en vez de `sc.exe`), y confirmar que se registra en el Hub igual que un worker Windows.
6. Usar el botón "Probar impresión" que ya existe en la pantalla de Emisión del front para mandar un ticket de prueba y confirmar que llega por red.

### Capas 2 y 3 (recién después de validar la Capa 1)

- **Capa 2**: al primer arranque, la Pi genera un código de emparejamiento corto y lo imprime como ticket (ya que tiene la impresora ahí, es gratis mostrarlo así). En el front se agrega una pantalla "Vincular impresora" donde el admin de la sucursal ingresa ese código; el backend asocia `WorkerNode ↔ Sucursal` en ese momento. Elimina la necesidad de editar `appsettings.json` a mano por cliente.
- **Capa 3**: imagen de Raspberry Pi OS con el worker ya instalado como servicio, lista para flashear y enviar al cliente -instalar pasa a ser "enchufar corriente y red".

## Notas de seguridad

- No subas `appsettings.Production.json` ni ningún archivo con credenciales/URLs internas reales; están excluidos vía `.gitignore`.
- El `WorkerKey` actúa como credencial del worker frente al backend; trátalo como secreto.

## Licencia

Software propietario de Lavanderia Kyo. Todos los derechos reservados — ver [LICENSE](./LICENSE).
