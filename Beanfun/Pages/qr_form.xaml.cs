using System;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Beanfun
{
    /// <summary>
    /// qr_form.xaml 的交互逻辑
    /// </summary>
    public partial class qr_form : Page
    {
        /*
         * =========================================================
         * GamaPlay Deeplink Redirect 網址
         * =========================================================
         *
         * 原始：
         * gameplapp://gameplhost/deeplink?type=1&action=web&code=...
         *
         * 轉換後：
         * https://bac925.github.io/gamaplay/#BASE64
         */
        private const string GamaPlayRedirectUrl =
            "https://bac925.github.io/gamaplay/";

        public qr_form()
        {
            InitializeComponent();
        }

        private void btn_Refresh_QRCode_Click(object sender, RoutedEventArgs e)
        {
            App.MainWnd.refreshQRCode();
        }

        private void btn_Refresh_QRCode_MouseEnter(object sender, MouseEventArgs e)
        {
            if (qr_Tip.Visibility == Visibility.Collapsed)
            {
                DockPanel.SetDock(btn_Refresh_QRCode, Dock.Left);
                qr_Tip.Visibility = Visibility.Visible;
            }
        }

        private void qr_Tip_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(
                new ProcessStartInfo(
                    "https://tw.beanfun.com/bfevent/bfApp/Page20160930/PC/index.html"
                )
                {
                    UseShellExecute = true,
                }
            );
        }

        private void TextBlock_MouseLeave(object sender, MouseEventArgs e)
        {
            if (qr_Tip.Visibility == Visibility.Visible)
            {
                DockPanel.SetDock(btn_Refresh_QRCode, Dock.Top);
                qr_Tip.Visibility = Visibility.Collapsed;
            }
        }

        /*
         * =========================================================
         * 複製 GamaPlay Deeplink
         * =========================================================
         *
         * 原本：
         *
         * WindowsAPI.CopyText(qrcodeClass.deeplink);
         *
         * 現在：
         *
         * gameplapp://...
         *
         *      ↓ UTF-8
         *
         * byte[]
         *
         *      ↓ Base64
         *
         * Z2FtZXBsYXBwOi8v...
         *
         *      ↓
         *
         * https://bac925.github.io/gamaplay/#Z2FtZXBsYXBwOi8v...
         *
         * 最後把 HTTPS 分享網址複製到剪貼簿。
         * =========================================================
         */
        private void btn_CopyDeeplink_Click(object sender, RoutedEventArgs e)
        {
            var qrcodeClass = App.MainWnd.qrcodeClass;

            /*
             * Deeplink 尚未取得
             */
            if (qrcodeClass == null || string.IsNullOrWhiteSpace(qrcodeClass.deeplink))
            {
                MessageBox.Show(
                    Application.Current.TryFindResource("CopyDeeplinkNotReady") as string
                        ?? "Deeplink 尚未準備完成"
                );

                return;
            }

            try
            {
                /*
                 * 取得原始 GamaPlay Deeplink。
                 *
                 * 例如：
                 *
                 * gameplapp://gameplhost/deeplink
                 * ?type=1
                 * &action=web
                 * &code=xxxx%2Bxxxx%2Fxxxx%3D%3D
                 */
                string deeplink = qrcodeClass.deeplink.Trim();

                /*
                 * =================================================
                 * Deeplink 格式驗證
                 * =================================================
                 *
                 * 避免意外把其他 URL / Scheme
                 * 包裝進我們的 Redirect 網址。
                 */
                if (
                    !deeplink.StartsWith(
                        "gameplapp://gameplhost/deeplink?",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    MessageBox.Show(
                        "取得的 GamaPlay Deeplink 格式不正確。\n\n"
                            + deeplink,
                        "Deeplink 錯誤",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );

                    return;
                }

                /*
                 * =================================================
                 * UTF-8 → Base64
                 * =================================================
                 *
                 * 非常重要：
                 *
                 * 不要：
                 *
                 * Uri.EscapeDataString()
                 * Uri.UnescapeDataString()
                 * UrlEncode()
                 * UrlDecode()
                 *
                 * 因為 code 裡面原本可能包含：
                 *
                 * %2B
                 * %2F
                 * %3D
                 *
                 * 我們需要原封不動保留。
                 */

                byte[] deeplinkBytes =
                    Encoding.UTF8.GetBytes(deeplink);

                string base64 =
                    Convert.ToBase64String(deeplinkBytes);

                /*
                 * =================================================
                 * 建立 GitHub Pages 分享網址
                 * =================================================
                 *
                 * 結果：
                 *
                 * https://bac925.github.io/gamaplay/#BASE64
                 *
                 * 注意：
                 *
                 * 使用的是 # Fragment，
                 * 不是 ?url= Query String。
                 */

                string shareUrl =
                    GamaPlayRedirectUrl
                    + "#"
                    + base64;

                /*
                 * 複製的是轉換完成後的 HTTPS 網址。
                 */
                WindowsAPI.CopyText(shareUrl);

                /*
                 * Debug Output
                 *
                 * Debug Build 時可以在 Visual Studio Output
                 * 看到原始與轉換後網址。
                 */
                Debug.WriteLine(
                    "[CopyDeeplink] Original:"
                );

                Debug.WriteLine(
                    deeplink
                );

                Debug.WriteLine(
                    "[CopyDeeplink] Share URL:"
                );

                Debug.WriteLine(
                    shareUrl
                );

                /*
                 * 成功提示
                 */
                MessageBox.Show(
                    Application.Current.TryFindResource("CopyDeeplinkSuccess") as string
                        ?? "Deeplink 分享網址已複製！"
                );
            }
            catch (Exception ex)
            {
                /*
                 * Debug 時保留完整錯誤。
                 */
                Debug.WriteLine(
                    "[CopyDeeplink] Failed:"
                );

                Debug.WriteLine(
                    ex.ToString()
                );

                MessageBox.Show(
                    Application.Current.TryFindResource("CopyFailed") as string
                        ?? "複製失敗"
                );
            }
        }

        private void btn_back_Click(object sender, RoutedEventArgs e)
        {
            App.LoginMethod = (int)LoginMethod.Regular;
            App.MainWnd.loginMethodChanged();
        }

        private void btn_GamePass_Click(object sender, RoutedEventArgs e)
        {
            btn_GamePass.IsEnabled = false;

            try
            {
                var browser = App.MainWnd.OpenGamePassLogin();

                browser.Closed += (s, args) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        btn_GamePass.IsEnabled = true;
                    });
                };
            }
            catch (Exception ex)
            {
                btn_GamePass.IsEnabled = true;

                MessageBox.Show(
                    "Gama Pass 登入視窗開啟失敗。\r\n\r\n" + ex.Message,
                    "Gama Pass",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void btn_StartGame_Click(object sender, RoutedEventArgs e)
        {
            App.MainWnd.runGame();
        }

        private void CopyQRCode_Click(object sender, RoutedEventArgs e)
        {
            if (qr_image.Source is BitmapSource bmp)
            {
                bool ok = false;

                try
                {
                    var encoder = new PngBitmapEncoder();

                    encoder.Frames.Add(
                        BitmapFrame.Create(bmp)
                    );

                    using (var stream = new System.IO.MemoryStream())
                    {
                        encoder.Save(stream);

                        stream.Position = 0;

                        var bitmap =
                            new System.Drawing.Bitmap(stream);

                        System.Windows.Forms.Clipboard.SetImage(
                            bitmap
                        );

                        ok = true;
                    }
                }
                catch
                {
                }

                ShowToast(
                    Application.Current.TryFindResource(
                        ok
                            ? "CopyQRCodeSuccess"
                            : "CopyFailed"
                    ) as string
                        ?? (
                            ok
                                ? "QR Code copied!"
                                : "Copy failed"
                        ),
                    ok
                );
            }
        }

        private Window _enlargeWnd;

        public void CloseEnlargeWindow()
        {
            if (_enlargeWnd != null)
            {
                _enlargeWnd.Close();
                _enlargeWnd = null;
            }
        }

        private void EnlargeQRCode_Click(object sender, RoutedEventArgs e)
        {
            if (qr_image.Source == null)
            {
                return;
            }

            CloseEnlargeWindow();

            _enlargeWnd = new Window
            {
                Title = "QR Code",
                Width = 350,
                Height = 350,
                WindowStartupLocation =
                    WindowStartupLocation.CenterOwner,
                Owner =
                    Window.GetWindow(this),
                ResizeMode =
                    ResizeMode.CanResize,
                Content =
                    new Image
                    {
                        Source =
                            qr_image.Source,
                        Stretch =
                            System.Windows.Media.Stretch.Uniform,
                    },
            };

            _enlargeWnd.Closed +=
                (s, _) =>
                {
                    _enlargeWnd = null;
                };

            _enlargeWnd.Show();
        }

        private void ShowToast(
            string message,
            bool success = true
        )
        {
            toastText.Text =
                (success ? "✓ " : "")
                + message;

            toastBorder.Background =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)
                        System.Windows.Media.ColorConverter.ConvertFromString(
                            success
                                ? "#CC2E7D32"
                                : "#CC333333"
                        )
                );

            toastBorder.Visibility =
                Visibility.Visible;

            var timer =
                new System.Windows.Threading.DispatcherTimer
                {
                    Interval =
                        TimeSpan.FromSeconds(2),
                };

            timer.Tick +=
                (s, _) =>
                {
                    timer.Stop();

                    toastBorder.Visibility =
                        Visibility.Collapsed;
                };

            timer.Start();
        }
    }
}