using System;
using System.Windows.Forms;

namespace VisionFlow
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new VisionFlow.Ui.FlowEditorForm());
        }
    }
}
