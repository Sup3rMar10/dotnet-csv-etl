using System.Text;

namespace CsvEtl.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var utf8 = new UTF8Encoding(false);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8, 65536) { AutoFlush = false, NewLine = "\n" };
        using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" };
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8, true);
        int code = CliApp.Run(args, stdin, stdout, stderr);
        try { stdout.Flush(); }
        catch (IOException) { return code == 0 ? 3 : code; }
        return code;
    }
}
