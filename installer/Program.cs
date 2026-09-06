using System;
using System.Windows.Forms;

namespace PermadB.Installer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool silent = false;
        foreach (var arg in args)
        {
            if (arg.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
                break;
            }
        }

        if (silent)
        {
            try
            {
                InstallerEngine.Install(
                    InstallerEngine.GetDefaultInstallDir(),
                    desktopShortcut: true,
                    startMenuShortcut: true,
                    startWithWindows: true,
                    launchNow: true
                );
            }
            catch { }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
    }
}
