using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Beanfun
{
    public partial class MapleTools : Window
    {
        public MapleTools()
        {
            InitializeComponent();
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void btn_MapleKit_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://maple-kit.com/",
                UseShellExecute = true,
            });
        }

        private void btn_GameMaintenance_Click(object sender, RoutedEventArgs e)
        {
            new GameMaintenanceWindow { Owner = this }.ShowDialog();
        }

        private void btn_Recycling_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(
                TryFindResource("MsgRecycling") as string,
                "",
                MessageBoxButton.YesNo
            );

            if (result != MessageBoxResult.Yes)
                return;

            DirectoryInfo gameDir = new DirectoryInfo(
                Path.GetDirectoryName(App.MainWnd.settingPage.t_GamePath.Text)
            );

            string[] dirList = new string[]
            {
                "blob_storage",
                "GPUCache",
                "VideoDecodeStats",
                "XignCode",
            };

            foreach (string dir in dirList)
            {
                if (!Directory.Exists($"{gameDir.FullName}\\{dir}"))
                    continue;
                try
                {
                    Directory.Delete($"{gameDir.FullName}\\{dir}", true);
                }
                catch { }
            }

            // 清理更新失敗的緩存
            foreach (DirectoryInfo di in gameDir.GetDirectories())
            {
                try
                {
                    if (di.Name.EndsWith(".$$$"))
                        di.Delete(true);
                }
                catch { }
            }

            // 清理報錯的檔案和多餘dll
            foreach (FileInfo fi in gameDir.GetFiles())
            {
                try
                {
                    if (
                        fi.Name.ToLower().EndsWith(".dmp")
                        || fi.Name.ToLower().Equals("localeemulator.dll")
                        || fi.Name.ToLower().Equals("loaderdll.dll")
                    )
                        fi.Delete();
                }
                catch { }
            }

            MessageBox.Show(TryFindResource("MsgRecyclingDone") as string);
        }
    }
}
