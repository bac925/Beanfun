using System;
using System.Windows;
using System.Windows.Controls;

namespace Beanfun
{
    /// <summary>
    /// Gama Pass login page.
    ///
    /// The class/file names are kept as gamepass_form for compatibility with
    /// the existing XAML project structure.
    /// </summary>
    public partial class gamepass_form : Page
    {
        public gamepass_form()
        {
            InitializeComponent();
        }

        private void btn_OpenGamePass_Click(object sender, RoutedEventArgs e)
        {
            btn_OpenGamePass.IsEnabled = false;

            try
            {
                /*
                 * IMPORTANT:
                 *
                 * Do not call BeanfunClient.GetSessionkey() here.
                 *
                 * The old implementation created the beanfun session in
                 * WebClient, extracted pSKey, then opened Login/Index in a
                 * different WebView2 context. That split the login state across
                 * two cookie/session containers and can now break redirects.
                 *
                 * GamePassBrowser now starts WebView2 from the official beanfun
                 * entry URL so the entire Gama Pass flow remains in one browser
                 * context.
                 */
                App.MainWnd.bfClient = new BeanfunClient();

                var browser = new GamePassBrowser();

                /*
                 * The button is re-enabled when this window closes so the user
                 * cannot accidentally create multiple simultaneous login
                 * windows from repeated clicks.
                 */
                browser.Closed += (s, args) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        btn_OpenGamePass.IsEnabled = true;
                    });
                };

                browser.Show();
            }
            catch (Exception ex)
            {
                btn_OpenGamePass.IsEnabled = true;

                MessageBox.Show(
                    "Gama Pass 登入視窗開啟失敗。\r\n\r\n" + ex.Message,
                    "Gama Pass",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void btn_back_Click(object sender, RoutedEventArgs e)
        {
            App.LoginMethod = (int)LoginMethod.Regular;
            App.MainWnd.loginMethodChanged();
        }
    }
}
