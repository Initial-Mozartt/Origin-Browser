using System;
using System.Windows.Forms;

namespace OriginBrowser;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 1. Show the splash screen instantly
        using (var splash = new SplashForm())
        {
            splash.Show();
            Application.DoEvents();   // let the splash paint

            // 2. Run the main browser form – it will close the splash when ready
            Application.Run(new BrowserForm(splash));
        }
    }
}