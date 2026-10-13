using System.Linq;
using System.Text;
using LaundryPrintWorker.Models;
using ESCPOS_NET.Emitters;
using Microsoft.Extensions.Options;

namespace LaundryPrintWorker.Services;

public class TicketPrinterService
{
    private const int LineWidth = 48;
    private const int LineWidthFontB = 64;
    private const int ColCant = 6;
    private const int ColDesc = 26;
    private const int ColPunit = 7;
    private const int ColTotal = 9;

    private readonly ILogger<TicketPrinterService> _logger;
    private readonly TicketSettings _settings;

    public TicketPrinterService(
        ILogger<TicketPrinterService> logger,
        IOptions<TicketSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    public async Task PrintAsync(PrintJobDto job, string printerName)
    {
        _logger.LogInformation("Imprimiendo comprobante {Document}", job.NumeroGuia);
        PrintTicket(job, printerName);
        await Task.CompletedTask;
        _logger.LogInformation("Impresión terminada");
    }

    private void PrintTicket(PrintJobDto job, string printerName)
    {
        var e = new EPSON();

        var bytes = new List<byte>();
        bytes.AddRange(e.Initialize());
        bytes.AddRange(e.CodePage(CodePage.WPC1252));

        PrintMainTicket(e, bytes, job);

        if (job.ImprimirCopia)
        {
            bytes.AddRange(e.FeedLines(3));
            bytes.AddRange(e.PartialCutAfterFeed(1));
            PrintControlCopy(e, bytes, job);
        }

        bytes.AddRange(e.FeedLines(5));
        bytes.AddRange(e.PartialCutAfterFeed(3));

        RawPrinterHelper.SendBytesToPrinter(printerName, bytes.ToArray(), $"Guia {job.SerieGuia}-{job.NumeroGuia}");
    }

    private void PrintMainTicket(EPSON e, List<byte> bytes, PrintJobDto job)
    {
        bytes.AddRange(e.CenterAlign());

        bytes.AddRange(e.SetStyles(PrintStyle.Bold | PrintStyle.DoubleWidth | PrintStyle.DoubleHeight));
        bytes.AddRange(e.PrintLine(_settings.CompanyName));
        bytes.AddRange(e.SetStyles(PrintStyle.None));

        bytes.AddRange(e.SetStyles(PrintStyle.FontB));
        bytes.AddRange(e.PrintLine(_settings.Address));
        bytes.AddRange(e.PrintLine(_settings.Phone));
        bytes.AddRange(e.PrintLine(_settings.Schedule));
        bytes.AddRange(e.PrintLine(_settings.Website));
        bytes.AddRange(e.SetStyles(PrintStyle.None));
        bytes.AddRange(e.PrintLine(""));

        bytes.AddRange(e.LeftAlign());
        bytes.AddRange(e.Print("GUIA_N°: "));
        bytes.AddRange(e.SetStyles(PrintStyle.Bold));
        bytes.AddRange(e.Print($"{job.SerieGuia}-{job.NumeroGuia}"));
        bytes.AddRange(e.SetStyles(PrintStyle.None));
        bytes.AddRange(e.PrintLine($"  FECHA: {job.FechaOperacion:dd/MM/yyyy HH:mm:ss}"));
        bytes.AddRange(e.PrintLine($"CLIENTE: {job.Cliente} / {FormatPhone(job.TelefonoContacto)}"));
        bytes.AddRange(e.PrintLine($"ENTREGA:  {job.FechaHoraEntrega:dd/MM/yyyy HH:mm:ss}"));
        bytes.AddRange(e.PrintLine($"CAJA:     {job.Usuario}"));
        bytes.AddRange(e.PrintLine(""));

        PrintItemsAndTotals(e, bytes, job);

        if (!string.IsNullOrWhiteSpace(job.Observaciones))
        {
            bytes.AddRange(e.PrintLine(""));
            bytes.AddRange(e.PrintLine("OBSERVACIONES"));
            bytes.AddRange(e.PrintLine(job.Observaciones));
        }

        if (!string.IsNullOrWhiteSpace(job.MensajeAlertas))
        {
            bytes.AddRange(e.PrintLine(""));
            bytes.AddRange(e.PrintLine("ALERTAS"));
            bytes.AddRange(e.PrintLine(job.MensajeAlertas));
        }

        bytes.AddRange(e.PrintLine(""));
        bytes.AddRange(e.SetStyles(PrintStyle.FontB));
        foreach (var legalLine in _settings.LegalLines)
        {
            var boldPrefix = GetBoldLegalPrefix(legalLine);
            var wrappedLines = WrapText(legalLine, LineWidthFontB).ToList();

            for (var i = 0; i < wrappedLines.Count; i++)
            {
                var wrapped = wrappedLines[i];

                if (i == 0 && boldPrefix != null && wrapped.Length >= boldPrefix.Length)
                {
                    bytes.AddRange(e.SetStyles(PrintStyle.FontB | PrintStyle.Bold));
                    bytes.AddRange(e.Print(boldPrefix));
                    bytes.AddRange(e.SetStyles(PrintStyle.FontB));
                    bytes.AddRange(e.PrintLine(wrapped[boldPrefix.Length..]));
                }
                else
                {
                    bytes.AddRange(e.PrintLine(wrapped));
                }
            }
        }
        bytes.AddRange(e.SetStyles(PrintStyle.None));

        bytes.AddRange(e.PrintLine(new string('.', LineWidth)));
        bytes.AddRange(e.CenterAlign());
        bytes.AddRange(e.PrintLine(_settings.ThanksMessage));
    }

    private void PrintControlCopy(EPSON e, List<byte> bytes, PrintJobDto job)
    {
        PrintControlHeader(e, bytes, job, includeTable: true);

        bytes.AddRange(e.PrintLine(""));
        bytes.AddRange(e.CenterAlign());
        bytes.AddRange(e.PrintLine(job.Usuario));
        bytes.AddRange(e.PrintLine(""));

        PrintControlHeader(e, bytes, job, includeTable: false);

        bytes.AddRange(e.PrintLine(""));
        bytes.AddRange(e.CenterAlign());
        bytes.AddRange(e.SetStyles(PrintStyle.DoubleWidth | PrintStyle.DoubleHeight));
        bytes.AddRange(e.PrintLine(job.Cliente.ToUpperInvariant()));
        bytes.AddRange(e.SetStyles(PrintStyle.None));
    }

    private void PrintControlHeader(EPSON e, List<byte> bytes, PrintJobDto job, bool includeTable)
    {
        bytes.AddRange(e.CenterAlign());
        bytes.AddRange(e.PrintLine(new string('-', LineWidth)));
        bytes.AddRange(e.PrintLine(_settings.ControlCopyTitle));
        bytes.AddRange(e.PrintLine(new string('-', LineWidth)));
        bytes.AddRange(e.PrintLine(""));

        bytes.AddRange(e.LeftAlign());
        bytes.AddRange(e.SetStyles(PrintStyle.Bold | PrintStyle.DoubleWidth | PrintStyle.DoubleHeight));
        bytes.AddRange(e.Print(job.NumeroGuia));
        bytes.AddRange(e.SetStyles(PrintStyle.None));

        var fecha = job.FechaOperacion.ToString("dd/MM/yyyy HH:mm:ss");
        var padding = Math.Max(1, LineWidth - (job.NumeroGuia.Length * 2) - fecha.Length);
        bytes.AddRange(e.PrintLine(new string(' ', padding) + fecha));

        if (includeTable)
        {
            bytes.AddRange(e.PrintLine($"{job.Cliente} / {FormatPhone(job.TelefonoContacto)}"));
            bytes.AddRange(e.PrintLine(""));
            PrintItemsAndTotals(e, bytes, job);
        }
    }

    private void PrintItemsAndTotals(EPSON e, List<byte> bytes, PrintJobDto job)
    {
        bytes.AddRange(e.LeftAlign());
        bytes.AddRange(e.PrintLine(new string('-', LineWidth)));
        bytes.AddRange(e.PrintLine(FormatRow("Cant.", "Descripcion", "P.unit", "Total")));
        bytes.AddRange(e.PrintLine(new string('-', LineWidth)));

        for (var i = 0; i < job.Detalles.Count; i++)
        {
            var item = job.Detalles[i];
            var (firstLine, remainder) = WrapDescription(item.ProductName, ColDesc);

            bytes.AddRange(e.PrintLine(FormatRow(
                item.Cant.ToString("N2"),
                firstLine,
                item.Precio.ToString("N2"),
                item.Total.ToString("N2"))));

            if (!string.IsNullOrEmpty(remainder))
            {
                bytes.AddRange(e.PrintLine(
                    $"{"",-ColCant}{remainder,-ColDesc}"));
            }

            if (!string.IsNullOrWhiteSpace(item.Observaciones))
            {
                bytes.AddRange(e.PrintLine($"Obs: {item.Observaciones}"));
            }

            if (i < job.Detalles.Count - 1)
            {
                bytes.AddRange(e.SetLineSpacingInDots(8));
                bytes.AddRange(e.PrintLine(new string('-', LineWidth)));
                bytes.AddRange(e.ResetLineSpacing());
            }
        }

        bytes.AddRange(e.PrintLine(new string('-', LineWidth)));

        bytes.AddRange(e.RightAlign());
        bytes.AddRange(e.PrintLine($"Total:S/ {job.Total,8:N2}"));
        var tipoPagoLabel = GetTipoPagoLabel(job.TipoPago);
        var tipoPagoPrefix = string.IsNullOrEmpty(tipoPagoLabel) ? "" : $"{tipoPagoLabel}| ";
        bytes.AddRange(e.PrintLine($"{tipoPagoPrefix}A Cuenta:S/ {job.Acuenta,8:N2}"));
        bytes.AddRange(e.PrintLine($"Saldo:S/ {job.Saldo,8:N2}"));

        var showCancelado = job.Saldo == 0;
        var showDelivery = job.EsDelivery;

        if (showCancelado || showDelivery)
        {
            var leftText = showDelivery ? "----- DELIVERY -----" : string.Empty;
            var rightText = showCancelado ? "*** CANCELADO ***" : string.Empty;
            var padding = Math.Max(0, LineWidth - leftText.Length - rightText.Length);

            bytes.AddRange(e.LeftAlign());

            if (showDelivery)
            {
                bytes.AddRange(e.SetStyles(PrintStyle.Bold | PrintStyle.DoubleHeight));
                bytes.AddRange(e.Print(leftText));
                bytes.AddRange(e.SetStyles(PrintStyle.None));
            }

            bytes.AddRange(e.Print(new string(' ', padding)));

            if (showCancelado)
            {
                bytes.AddRange(e.SetStyles(PrintStyle.Bold | PrintStyle.DoubleHeight));
                bytes.AddRange(e.Print(rightText));
                bytes.AddRange(e.SetStyles(PrintStyle.None));
            }

            bytes.AddRange(e.PrintLine(""));
        }

        bytes.AddRange(e.LeftAlign());
    }

    private static string FormatPhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return phone;
        }

        var digits = new StringBuilder();
        foreach (var c in phone)
        {
            if (char.IsDigit(c))
            {
                digits.Append(c);
            }
        }

        if (digits.Length == 0)
        {
            return phone;
        }

        var result = new StringBuilder();
        for (var i = 0; i < digits.Length; i += 3)
        {
            if (result.Length > 0)
            {
                result.Append('-');
            }

            result.Append(digits, i, Math.Min(3, digits.Length - i));
        }

        return result.ToString();
    }

    private static readonly string[] BoldLegalPhrases = ["Despues de 90 dias", "Despues de 48 horas"];

    private static string? GetBoldLegalPrefix(string legalLine)
    {
        foreach (var phrase in BoldLegalPhrases)
        {
            if (legalLine.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return phrase;
            }
        }

        return null;
    }

    private static string GetTipoPagoLabel(string tipoPago)
    {
        return tipoPago switch
        {
            "SP" => string.Empty,
            "TA" => "Tarjeta",
            "QR" => "Yape",
            "EF" => "Efectivo",
            _ => tipoPago
        };
    }

    private static string FormatRow(string cant, string desc, string punit, string total)
    {
        return $"{cant,-ColCant}{desc,-ColDesc}{punit,ColPunit}{total,ColTotal}";
    }

    private static (string firstLine, string remainder) WrapDescription(string description, int width)
    {
        if (description.Length <= width)
        {
            return (description, string.Empty);
        }

        var breakAt = description.LastIndexOf(' ', Math.Min(width, description.Length - 1));
        if (breakAt <= 0)
        {
            breakAt = width;
        }

        return (description[..breakAt].TrimEnd(), description[breakAt..].TrimStart());
    }

    private static IEnumerable<string> WrapText(string text, int width)
    {
        var remaining = text;
        while (remaining.Length > width)
        {
            var breakAt = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
            if (breakAt <= 0)
            {
                breakAt = width;
            }

            yield return remaining[..breakAt].TrimEnd();
            remaining = remaining[breakAt..].TrimStart();
        }

        yield return remaining;
    }
}
