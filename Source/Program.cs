using System;
using System.Windows.Forms;

namespace DL1Assistant
{
    public static class Program
    {
        [STAThread]
        public static void Main() { Run(AppDomain.CurrentDomain.BaseDirectory); }

        [STAThread]
        public static void Run(string rootDir)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(rootDir));
        }
    }
}
