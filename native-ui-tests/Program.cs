namespace VideoDownloader.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try { UiScenario.Run();return 0; }
        catch(Exception error)
        {
            string? result=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(result))File.WriteAllText(result,"FAIL: "+error);
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
