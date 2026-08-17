namespace GameShelf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var store = new DataStore(AppPaths.FromExecutable());
            Application.Run(new MainForm(store));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GameShelf", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
