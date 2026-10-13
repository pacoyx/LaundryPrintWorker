namespace LaundryPrintWorker.Models;

public class TicketSettings
{
    public string CompanyName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string Schedule { get; set; } = string.Empty;

    public string Website { get; set; } = string.Empty;

    public List<string> LegalLines { get; set; } = [];

    public string ThanksMessage { get; set; } = string.Empty;

    public string ControlCopyTitle { get; set; } = string.Empty;
}
