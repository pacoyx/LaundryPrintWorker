namespace LaundryPrintWorker.Models;

public class PrintJobDetailResponse
{
    public int ProductId { get; set; }

    public string ProductName { get; set; } = string.Empty;

    public decimal Cant { get; set; }

    public decimal Precio { get; set; }

    public decimal Total { get; set; }

    public string Observaciones { get; set; } = string.Empty;

    public string Ubicacion { get; set; } = string.Empty;

    public string Identificador { get; set; } = string.Empty;
}

public class PrintJobDto
{
    public long PrintQueueId { get; set; }

    public int WorkGuideId { get; set; }

    public string SerieGuia { get; set; } = string.Empty;

    public string NumeroGuia { get; set; } = string.Empty;

    public DateTime FechaOperacion { get; set; }

    public DateTime FechaHoraEntrega { get; set; }

    public string Cliente { get; set; } = string.Empty;

    public string TelefonoContacto { get; set; } = string.Empty;

    public string DireccionContacto { get; set; } = string.Empty;

    public string TipoPago { get; set; } = string.Empty;

    public string DescripcionPago { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public decimal Acuenta { get; set; }

    public decimal Saldo { get; set; }

    public string Observaciones { get; set; } = string.Empty;

    public string MensajeAlertas { get; set; } = string.Empty;

    public int PrinterId { get; set; }

    public string Usuario { get; set; } = string.Empty;

    public bool EsDelivery { get; set; }

    public bool ImprimirCopia { get; set; }

    public List<PrintJobDetailResponse> Detalles { get; set; } = [];
}

public class PrintNotificationDto
{
    public long QueueId { get; set; }
    public int PrinterId { get; set; }
    public string? PrinterName { get; set; }
}