using System;
using System.Windows.Forms;

namespace OriginBrowser;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Enable modern visual styles and high DPI for WinForms
        ApplicationConfiguration.Initialize();
        Application.Run(new BrowserForm());
    }
}