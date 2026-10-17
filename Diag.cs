namespace SipSoftphone;

public static class Diag
{
    public static Action<string> Sink = static msg => Console.WriteLine(msg);

    public static void WriteLine(string msg) => Sink(msg);
}
