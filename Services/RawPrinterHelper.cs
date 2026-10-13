using System.Runtime.InteropServices;

namespace LaundryPrintWorker.Services;

internal static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string? pDataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, DOCINFOA di);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    /// <summary>
    /// Envía bytes ESC/POS crudos directamente a la cola de impresión de Windows (sin pasar por GDI),
    /// necesario porque las impresoras térmicas POS no interpretan RAW a través del driver gráfico estándar.
    /// </summary>
    public static void SendBytesToPrinter(string printerName, byte[] bytes, string documentName = "Ticket POS")
    {
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"No se pudo abrir la impresora '{printerName}'. Código de error de Windows: {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var docInfo = new DOCINFOA
            {
                pDocName = documentName,
                pDataType = "RAW"
            };

            if (!StartDocPrinter(hPrinter, 1, docInfo))
            {
                throw new InvalidOperationException(
                    $"No se pudo iniciar el documento en '{printerName}'. Código de error de Windows: {Marshal.GetLastWin32Error()}");
            }

            try
            {
                if (!StartPagePrinter(hPrinter))
                {
                    throw new InvalidOperationException(
                        $"No se pudo iniciar la página en '{printerName}'. Código de error de Windows: {Marshal.GetLastWin32Error()}");
                }

                try
                {
                    if (!WritePrinter(hPrinter, bytes, bytes.Length, out var written) || written != bytes.Length)
                    {
                        throw new InvalidOperationException(
                            $"No se pudieron escribir todos los bytes en '{printerName}'. Código de error de Windows: {Marshal.GetLastWin32Error()}");
                    }
                }
                finally
                {
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }
}
