using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Beanfun
{
    /*
     * NOTE:
     * Existing XAML uses the class name GamePassBrowser.
     * Keep the class name for XAML compatibility, but all user-facing naming
     * and comments use the correct product name: Gama Pass.
     */
    public partial class GamePassBrowser : Window
    {
        private const string BeanfunLoginEntry =
            "https://tw.beanfun.com/beanfun_block/bflogin/default.aspx" +
            "?service_code=999999&service_region=T0";

        private bool _loginCompleted = false;
        private bool _webViewReady = false;
        private bool _completionCheckRunning = false;
        private bool _recoveryRunning = false;
        private int _heartbeatFailures = 0;
        private readonly DispatcherTimer _webViewHeartbeat = new()
        {
            Interval = TimeSpan.FromSeconds(10)
        };

        public GamePassBrowser()
        {
            /*
             * Set WebView2's profile folder BEFORE InitializeComponent().
             *
             * WebView2 may begin creating its controller while the XAML control
             * is being initialized. Setting/creating a different environment
             * only later can cause RPC_E_DISCONNECTED (0x80010108).
             */
            string userDataFolder =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Beanfun",
                    "WebView2",
                    "GamaPass"
                );

            Directory.CreateDirectory(userDataFolder);

            Environment.SetEnvironmentVariable(
                "WEBVIEW2_USER_DATA_FOLDER",
                userDataFolder
            );

            InitializeComponent();

            Loaded += OnLoaded;
            Closed += OnClosed;
            _webViewHeartbeat.Tick += WebViewHeartbeat_Tick;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _webViewHeartbeat.Stop();

            try
            {
                if (wb_Main?.CoreWebView2 != null)
                    wb_Main.CoreWebView2.ProcessFailed -= OnWebViewProcessFailed;
            }
            catch { }

            try { wb_Main?.Dispose(); } catch { }

            if (!_loginCompleted && !_recoveryRunning)
            {
                App.MainWnd.bfClient = null;
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Opacity = 0;

            try
            {
                await InitializeWebView2();

                RegisterWebViewEvents();

                _webViewReady = true;
                _heartbeatFailures = 0;
                _webViewHeartbeat.Start();

                Debug.WriteLine(
                    "[GamaPass] WebView2 ready. Version=" +
                    (wb_Main.CoreWebView2?.Environment?.BrowserVersionString ?? "unknown")
                );

                Debug.WriteLine(
                    "[GamaPass] Starting from official beanfun login entry: " +
                    BeanfunLoginEntry
                );

                wb_Main.CoreWebView2.Navigate(BeanfunLoginEntry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[GamaPass] WebView2 initialization failed: " + ex
                );

                Opacity = 1;

                string extra = "";

                if (
                    ex.HResult == unchecked((int)0x80010108)
                    || ex.Message.IndexOf(
                        "RPC_E_DISCONNECTED",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0
                )
                {
                    extra =
                        "\r\n\r\nWebView2 執行程序在初始化時中斷。" +
                        "\r\n請先完全關閉登入器後重新開啟。" +
                        "\r\n若仍發生，請確認 Microsoft Edge WebView2 Runtime 已安裝/更新。";
                }

                MessageBox.Show(
                    "Gama Pass 登入視窗初始化失敗。\r\n\r\n"
                    + ex.Message
                    + extra,
                    "Gama Pass",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                Close();
            }
        }

        private async Task InitializeWebView2()
        {
            /*
             * IMPORTANT:
             * Do not always create a CoreWebView2Environment manually.
             *
             * The old launcher normally lets the WPF WebView2 control create
             * its environment itself. We keep that path for normal operation.
             *
             * A custom environment is used only when GPU disabling is requested.
             */
            bool disableGpu =
                bool.Parse(
                    ConfigAppSettings.GetValue(
                        "disableHardwareAcceleration",
                        "false"
                    )
                );

            if (!disableGpu)
            {
                await wb_Main.EnsureCoreWebView2Async();
                return;
            }

            string userDataFolder =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Beanfun",
                    "WebView2",
                    "GamaPass"
                );

            var options =
                new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--disable-gpu --disable-gpu-compositing"
                };

            CoreWebView2Environment env =
                await CoreWebView2Environment.CreateAsync(
                    null,
                    userDataFolder,
                    options
                );

            await wb_Main.EnsureCoreWebView2Async(env);
        }

        private void RegisterWebViewEvents()
        {
            wb_Main.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            wb_Main.CoreWebView2.NavigationStarting += OnCoreNavigationStarting;
            wb_Main.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            wb_Main.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;
        }

        private void OnNewWindowRequested(
            object sender,
            CoreWebView2NewWindowRequestedEventArgs e
        )
        {
            try
            {
                /*
                 * Keep normal http(s) navigation inside the same login context.
                 * Do not intentionally transfer the session to another browser.
                 */
                if (
                    !string.IsNullOrWhiteSpace(e.Uri)
                    && (
                        e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    wb_Main.CoreWebView2.Navigate(e.Uri);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GamaPass] NewWindowRequested failed: " + ex.Message);
            }
        }

        private void OnCoreNavigationStarting(
            object sender,
            CoreWebView2NavigationStartingEventArgs e
        )
        {
            Debug.WriteLine("[GamaPass] NavigationStarting: " + e.Uri);

            Title =
                Application.Current.TryFindResource("GamePassLogin") as string
                ?? "Gama Pass Login";
        }

        private async void OnNavigationCompleted(
            object sender,
            CoreWebView2NavigationCompletedEventArgs e
        )
        {
            if (!_webViewReady || _loginCompleted)
                return;

            string url = wb_Main.Source?.ToString() ?? "";

            Debug.WriteLine(
                $"[GamaPass] NavigationCompleted Success={e.IsSuccess} URL={url}"
            );

            if (!e.IsSuccess)
            {
                Debug.WriteLine(
                    "[GamaPass] WebErrorStatus=" + e.WebErrorStatus
                );

                /*
                 * A transient redirect/navigation error should not immediately
                 * destroy the login session. The user can still continue if the
                 * page recovers.
                 */
                Opacity = 1;
                return;
            }

            /*
             * The old code created pSKey in BeanfunClient and opened Login/Index
             * directly. The fixed flow reaches Login/Index naturally in the
             * SAME WebView2 context.
             */
            if (
                url.IndexOf(
                    "login.beanfun.com/Login/Index",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0
            )
            {
                Opacity = 1;

                await TryActivateGamaPass();
            }
            else if (Opacity == 0)
            {
                /*
                 * If beanfun changes its intermediate route, do not leave the
                 * window permanently invisible.
                 */
                if (
                    url.IndexOf("login.beanfun.com", StringComparison.OrdinalIgnoreCase) >= 0
                    || url.IndexOf("tw.newlogin.beanfun.com", StringComparison.OrdinalIgnoreCase) >= 0
                )
                {
                    Opacity = 1;
                }
            }

            /*
             * Do not depend on return.aspx / SendLogin / index.aspx names.
             * After EVERY successful beanfun navigation, simply test whether
             * bfWebToken now exists.
             */
            if (
                url.IndexOf("beanfun.com", StringComparison.OrdinalIgnoreCase) >= 0
            )
            {
                await TryCompleteLogin();
            }
        }

        private async Task TryActivateGamaPass()
        {
            if (_loginCompleted)
                return;

            try
            {
                /*
                 * Do not permanently mark the action as completed until a
                 * matching element was actually found and clicked.
                 *
                 * Multiple selectors are included because beanfun has changed
                 * the Login/Index markup over time.
                 */
                const string script = @"
(function () {
    const selectors = [
        'a.use-gama-pass',
        '.use-gama-pass',
        '[data-login-type=""gamapass""]',
        '[data-login-type=""gama-pass""]'
    ];

    for (const selector of selectors) {
        const el = document.querySelector(selector);
        if (el) {
            el.click();
            return 'clicked:' + selector;
        }
    }

    const all = Array.from(document.querySelectorAll('a,button,[role=""button""]'));
    const byText = all.find(el => {
        const text = (el.innerText || el.textContent || '').trim().toLowerCase();
        return text.includes('gama pass') || text.includes('gamapass');
    });

    if (byText) {
        byText.click();
        return 'clicked:text';
    }

    return 'not-found';
})();";

                /*
                 * Login/Index may still be finishing JS initialization on the
                 * first NavigationCompleted event.
                 */
                for (int attempt = 1; attempt <= 10; attempt++)
                {
                    if (_loginCompleted)
                        return;

                    string result =
                        await wb_Main.CoreWebView2.ExecuteScriptAsync(script);

                    Debug.WriteLine(
                        $"[GamaPass] activate attempt={attempt}, result={result}"
                    );

                    if (
                        result != null
                        && result.IndexOf(
                            "clicked:",
                            StringComparison.OrdinalIgnoreCase
                        ) >= 0
                    )
                    {
                        return;
                    }

                    await Task.Delay(300);
                }

                /*
                 * If automatic activation fails, leave Login/Index visible.
                 * The user can still click the official Gama Pass control
                 * manually instead of failing the whole login flow.
                 */
                Debug.WriteLine(
                    "[GamaPass] Gama Pass control was not found; waiting for manual interaction."
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[GamaPass] TryActivateGamaPass failed: " + ex.Message
                );
            }
        }

        private async Task TryCompleteLogin()
        {
            if (
                _loginCompleted
                || _completionCheckRunning
                || wb_Main.CoreWebView2 == null
            )
            {
                return;
            }

            _completionCheckRunning = true;

            try
            {
                var twCookies =
                    await wb_Main.CoreWebView2.CookieManager.GetCookiesAsync(
                        "https://tw.beanfun.com"
                    );

                string webToken = null;

                foreach (var cookie in twCookies)
                {
                    if (
                        string.Equals(
                            cookie.Name,
                            "bfWebToken",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        webToken = cookie.Value;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(webToken))
                    return;

                /*
                 * bfWebToken exists: login is complete.
                 *
                 * Copy cookies from every domain involved in the current login
                 * flow back into BeanfunClient's CookieContainer.
                 */
                var loginCookies =
                    await wb_Main.CoreWebView2.CookieManager.GetCookiesAsync(
                        "https://login.beanfun.com"
                    );

                var newLoginCookies =
                    await wb_Main.CoreWebView2.CookieManager.GetCookiesAsync(
                        "https://tw.newlogin.beanfun.com"
                    );

                var allCookies =
                    new System.Collections.Generic.List<Cookie>();

                ConvertCookies(twCookies, allCookies);
                ConvertCookies(loginCookies, allCookies);
                ConvertCookies(newLoginCookies, allCookies);

                Debug.WriteLine(
                    $"[GamaPass] Login complete. CookieCount={allCookies.Count}"
                );

                _loginCompleted = true;

                /*
                 * Call MainWindow before Close().
                 * This avoids Closed observing an incomplete state if any future
                 * code changes the event timing.
                 */
                App.MainWnd.GamePassLoginCompleted(
                    webToken,
                    allCookies
                );

                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[GamaPass] TryCompleteLogin failed: " + ex
                );
            }
            finally
            {
                _completionCheckRunning = false;
            }
        }

        private static void ConvertCookies(
            System.Collections.Generic.IReadOnlyList<CoreWebView2Cookie> source,
            System.Collections.Generic.List<Cookie> target
        )
        {
            foreach (CoreWebView2Cookie wv2Cookie in source)
            {
                try
                {
                    string domain =
                        (wv2Cookie.Domain ?? "")
                        .Trim()
                        .TrimStart('.');

                    if (string.IsNullOrWhiteSpace(domain))
                        continue;

                    Cookie cookie =
                        new Cookie(
                            wv2Cookie.Name,
                            wv2Cookie.Value,
                            string.IsNullOrEmpty(wv2Cookie.Path) ? "/" : wv2Cookie.Path,
                            domain
                        );

                    cookie.Secure = wv2Cookie.IsSecure;
                    cookie.HttpOnly = wv2Cookie.IsHttpOnly;

                    target.Add(cookie);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[GamaPass] Cookie conversion skipped: " + ex.Message
                    );
                }
            }
        }

        private void OnWebViewProcessFailed(
            object sender,
            CoreWebView2ProcessFailedEventArgs e
        )
        {
            string kind = e.ProcessFailedKind.ToString();
            Debug.WriteLine("[GamaPass] WebView2 ProcessFailed: " + kind);

            // Renderer/GPU 子程序崩潰時，CoreWebView2 本身通常仍有效，
            // 先嘗試 Reload，避免不必要地丟失登入 Cookie。
            if (
                kind.IndexOf("Render", StringComparison.OrdinalIgnoreCase) >= 0
                || kind.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0
            )
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        wb_Main.CoreWebView2?.Reload();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[GamaPass] Reload after process failure failed: " + ex.Message);
                    }

                    _ = RecoverWebViewAsync("WebView2 " + kind + " 異常終止");
                }));
                return;
            }

            // Browser process / Utility 等較嚴重失敗時重建整個 WebView2 Window。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _ = RecoverWebViewAsync("WebView2 " + kind + " 異常終止");
            }));
        }

        private async void WebViewHeartbeat_Tick(object? sender, EventArgs e)
        {
            if (!_webViewReady || _loginCompleted || _recoveryRunning || wb_Main.CoreWebView2 == null)
                return;

            try
            {
                Task<string> ping = wb_Main.CoreWebView2.ExecuteScriptAsync("1");
                Task completed = await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(5)));

                if (completed == ping)
                {
                    await ping; // propagate WebView2 exceptions
                    _heartbeatFailures = 0;
                    return;
                }

                _heartbeatFailures++;
                Debug.WriteLine($"[GamaPass] WebView2 heartbeat timeout #{_heartbeatFailures}");
            }
            catch (Exception ex)
            {
                _heartbeatFailures++;
                Debug.WriteLine($"[GamaPass] WebView2 heartbeat failed #{_heartbeatFailures}: {ex.Message}");
            }

            // 連續兩次（約 20 秒）無法執行最簡單的 JS，視為卡死/白屏。
            if (_heartbeatFailures >= 2)
                await RecoverWebViewAsync("WebView2 長時間無回應（白屏偵測）");
        }

        private async Task RecoverWebViewAsync(string reason)
        {
            if (_recoveryRunning || _loginCompleted)
                return;

            _recoveryRunning = true;
            _webViewHeartbeat.Stop();
            Debug.WriteLine("[GamaPass] Recovering WebView2: " + reason);

            try
            {
                // 不使用 Process.Kill(msedgewebview2)：WebView2 Runtime 可能同時服務
                // 其他視窗甚至其他應用程式。關閉/Dispose 目前 controller 後，
                // WebView2 Runtime 會自行清理屬於這個 controller 的子程序。
                try { wb_Main?.Dispose(); } catch { }

                App.MainWnd.bfClient = new BeanfunClient();

                var replacement = new GamePassBrowser
                {
                    Owner = Owner
                };

                replacement.Show();
                await Task.Yield();
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GamaPass] WebView2 recovery failed: " + ex);
                _recoveryRunning = false;

                MessageBox.Show(
                    "Gama Pass 的 WebView2 發生異常，且自動重建失敗。\r\n\r\n"
                    + ex.Message
                    + "\r\n\r\n請關閉登入視窗後重新嘗試。",
                    "Gama Pass",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
        }

        /*
         * Retained because the XAML may already wire NavigationStarting to this
         * method. User-facing naming is corrected to Gama Pass.
         */
        private void wb_Main_NavigationStarting(
            object sender,
            CoreWebView2NavigationStartingEventArgs e
        )
        {
            Title =
                Application.Current.TryFindResource("GamePassLogin") as string
                ?? "Gama Pass Login";
        }
    }
}
