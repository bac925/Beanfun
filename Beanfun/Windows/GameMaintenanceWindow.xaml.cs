using Beanfun.GameMaintenance;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Beanfun
{
    public partial class GameMaintenanceWindow : Window
    {
        private readonly TmsManifestService _service = new();
        private readonly TmsPatchService _patch = new();
        private CancellationTokenSource? _cts;
        private TmsProductInfo? _product;
        private List<VerifyResult> _results = new();
        private int _localVersion;
        private int _officialVersion;
        private bool _returnToDetailsOnCancel;
        private string _activeOperation = "";

        public GameMaintenanceWindow()
        {
            InitializeComponent();
            GamePathText.Text = TmsManifestService.GetGameRoot(App.MainWnd.settingPage.t_GamePath.Text);
            DetectLocalVersion();
            Loaded += async (_, _) => await RefreshOfficialInfoAsync();
        }

        private void DetectLocalVersion()
        {
            try
            {
                string baseWz = System.IO.Path.Combine(GamePathText.Text, "Data", "Base", "Base.wz");
                if (!System.IO.File.Exists(baseWz)) baseWz = System.IO.Path.Combine(GamePathText.Text, "Base.wz");
                if (!System.IO.File.Exists(baseWz)) { _localVersion = 0; LocalVersionText.Text = "找不到 Base.wz"; return; }
                _localVersion = WzVersionReader.ReadVersion(baseWz);
                LocalVersionText.Text = _localVersion > 0 ? $"V{_localVersion}" : "未知格式";
            }
            catch (Exception ex) { _localVersion = 0; LocalVersionText.Text = "偵測失敗"; Debug.WriteLine(ex); }
        }

        private async Task RefreshOfficialInfoAsync()
        {
            if (!ValidateRoot(false)) return;
            try
            {
                _product = await _service.GetProductInfoAsync(CancellationToken.None);
                OfficialVersionText.Text = _product.Version;
                FileCountText.Text = _product.Files.Count.ToString("N0");
                _officialVersion = ParseVersion(_product.Version);
                UpdateInfoText.Text = _localVersion > 0 && _officialVersion > _localVersion
                    ? $"有可用更新：V{_localVersion} → V{_officialVersion}"
                    : _localVersion == _officialVersion && _localVersion > 0 ? $"目前已是 V{_officialVersion}；仍會檢查 ExePatch.dat hotfix。" : "無法比較版本，可使用完整檔案檢查。";
            }
            catch (Exception ex) { UpdateInfoText.Text = "官方版本資訊取得失敗：" + ex.Message; }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateRoot()) return;
            if (GameRunning()) { WarnGameRunning(); return; }
            DetectLocalVersion();
            await RefreshOfficialInfoAsync();
            if (_product == null || _localVersion <= 0 || _officialVersion <= 0) { MessageBox.Show("無法取得本機或官方版本。", "遊戲更新"); return; }
            if (_localVersion > _officialVersion) { MessageBox.Show("本機版本高於官方版本，為避免降版不會執行更新。", "遊戲更新", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (MessageBox.Show($"即將使用 Beanfun 官方 Patch/CDN 更新。\n\n本機：V{_localVersion}\n官方：V{_officialVersion}\n\n更新完成後會自動進行完整 SHA-256 驗證並修復異常檔案。\n請勿在更新期間啟動遊戲。\n\n確定開始？", "遊戲更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            ShowPatcherView("更新遊戲");
            await RunBusyAsync(async ct =>
            {
                var pp = new Progress<PatchProgress>(p =>
                {
                    Progress.Value = p.Total > 0 ? p.Current * 100.0 / p.Total : 0;
                    StatusText.Text = string.IsNullOrEmpty(p.Detail) ? p.Phase : $"{p.Phase}：{p.Detail}";
                });
                await _patch.UpdateAsync(GamePathText.Text, _localVersion, _officialVersion, _product, pp, ct);
                DetectLocalVersion();
                StatusText.Text = "版本更新完成，正在依官方 Manifest 做完整 SHA-256 驗證…";
                var vp = new Progress<VerifyProgress>(p => { Progress.Value = p.Total == 0 ? 0 : p.Current * 100.0 / p.Total; StatusText.Text = $"更新後驗證 {p.Current:N0}/{p.Total:N0}：{p.Path}"; });
                _results = await _service.VerifyAsync(GamePathText.Text, _product, vp, ct);
                if (_results.Any(x => x.NeedsRepair))
                {
                    StatusText.Text = "發現異常檔案，正在從官方完整客戶端修復…";
                    await _service.RepairAsync(GamePathText.Text, _product, _results, vp, ct);
                    _results = await _service.VerifyAsync(GamePathText.Text, _product, vp, ct);
                }
                ShowResults(); DetectLocalVersion();
                StatusText.Text = _results.Any(x => x.NeedsRepair) ? "更新完成，但仍有異常檔案，請查看清單。" : "更新與完整驗證完成。";
                MessageBox.Show(StatusText.Text, "遊戲更新", MessageBoxButton.OK, _results.Any(x => x.NeedsRepair) ? MessageBoxImage.Warning : MessageBoxImage.Information);
            });
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateRoot()) return; DetectLocalVersion();
            ShowPatcherView("檢查檔案");
            await RunBusyAsync(async ct =>
            {
                StatusText.Text = "正在取得 Beanfun 官方檔案清單…"; _product = await _service.GetProductInfoAsync(ct);
                OfficialVersionText.Text = _product.Version; FileCountText.Text = _product.Files.Count.ToString("N0"); _officialVersion = ParseVersion(_product.Version);
                var progress = new Progress<VerifyProgress>(p => { Progress.Value = p.Total == 0 ? 0 : p.Current * 100.0 / p.Total; StatusText.Text = $"正在檢查 {p.Current:N0}/{p.Total:N0}：{p.Path}"; });
                _results = await _service.VerifyAsync(GamePathText.Text, _product, progress, ct);
                ShowResults();
                int repairCount = _results.Where(x => x.NeedsRepair).Select(x => x.File.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (repairCount == 0)
                {
                    StatusText.Text = "檢查完成，沒有需要修復的檔案。";
                    return;
                }

                StatusText.Text = $"檢查完成：發現 {repairCount:N0} 個異常檔案。";
                var answer = MessageBox.Show(this,
                    $"發現 {repairCount:N0} 個異常檔案，需要下載 {repairCount:N0} 個檔案進行修復。\n\n現在從官方伺服器下載並修復嗎？",
                    "檔案檢查結果", MessageBoxButton.YesNo, MessageBoxImage.Question);
                ct.ThrowIfCancellationRequested();
                if (answer != MessageBoxResult.Yes)
                {
                    StatusText.Text = $"已略過修復；{repairCount:N0} 個異常檔案可在詳細介面查看。";
                    ShowDetailsView();
                    return;
                }
                if (GameRunning()) { WarnGameRunning(); ShowDetailsView(); return; }
                _activeOperation = "修復檔案";
                Progress.Value = 0;
                var repairProgress = new Progress<VerifyProgress>(p => { Progress.Value = p.Total == 0 ? 0 : p.Current * 100.0 / p.Total; StatusText.Text = $"正在修復 {p.Current:N0}/{p.Total:N0}：{p.Path}"; });
                await _service.RepairAsync(GamePathText.Text, _product, _results, repairProgress, ct);
                StatusText.Text = "下載完成，正在重新驗證檔案…";
                Progress.Value = 0;
                _results = await _service.VerifyAsync(GamePathText.Text, _product, progress, ct);
                ShowResults();
                StatusText.Text = _results.Any(x => x.NeedsRepair) ? "修復後仍有異常檔案，請查看詳細介面。" : "修復完成，已通過重新驗證。";
            });
        }

        private async void Repair_Click(object sender, RoutedEventArgs e)
        {
            if (_product == null || !_results.Any(x => x.NeedsRepair)) return;
            if (GameRunning()) { WarnGameRunning(); return; }
            if (MessageBox.Show("將從 Beanfun 官方 CDN 下載異常檔案，SHA-256 正確後才替換原檔。\n\n確定開始修復？", "遊戲檔案修復", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ShowPatcherView("修復檔案");
            await RunBusyAsync(async ct =>
            {
                var progress = new Progress<VerifyProgress>(p => { Progress.Value = p.Total == 0 ? 0 : p.Current * 100.0 / p.Total; StatusText.Text = $"正在修復 {p.Current:N0}/{p.Total:N0}：{p.Path}"; });
                await _service.RepairAsync(GamePathText.Text, _product, _results, progress, ct); _results = await _service.VerifyAsync(GamePathText.Text, _product, progress, ct); ShowResults();
                StatusText.Text = _results.Any(x => x.NeedsRepair) ? "仍有異常檔案，請查看清單。" : "修復完成，已通過重新驗證。";
            });
        }

        private async Task RunBusyAsync(Func<CancellationToken, Task> action)
        {
            _cts = new CancellationTokenSource(); ScanButton.IsEnabled = RepairButton.IsEnabled = UpdateButton.IsEnabled = false; CancelButton.IsEnabled = true; Progress.Value = 0;
            try { await action(_cts.Token); }
            catch (OperationCanceledException) { StatusText.Text = "操作已取消。"; }
            catch (Exception ex) { StatusText.Text = "操作失敗。"; Debug.WriteLine(ex); MessageBox.Show(ex.ToString(), "遊戲檔案管理", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                _cts.Dispose(); _cts = null;
                ScanButton.IsEnabled = UpdateButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                RepairButton.IsEnabled = _results.Any(x => x.NeedsRepair);
                if (_returnToDetailsOnCancel) ShowDetailsView();
            }
        }

        private void ShowResults()
        {
            ResultGrid.ItemsSource = _results.Where(x => x.State != VerifyState.Ok).Select(x => new { StateText = x.State switch { VerifyState.Missing => "遺失", VerifyState.SizeMismatch => "大小異常", VerifyState.HashMismatch => "SHA-256異常", VerifyState.Error => "錯誤", VerifyState.Skipped => "略過", _ => "正常" }, Path = x.File.Path, x.Detail }).ToList();
            int ok = _results.Count(x => x.State == VerifyState.Ok), bad = _results.Count(x => x.NeedsRepair), err = _results.Count(x => x.State == VerifyState.Error);
            SummaryText.Text = $"正常 {ok:N0}　需修復 {bad:N0}　錯誤 {err:N0}"; RepairButton.IsEnabled = bad > 0 && _cts == null;
        }

        private void ShowPatcherView(string operation)
        {
            _returnToDetailsOnCancel = false;
            _activeOperation = operation;
            DetailsView.Visibility = Visibility.Collapsed;
            Width = 626; Height = 583;
            PatcherView.Visibility = Visibility.Visible;
            UpdateProgressFill();
        }

        private void ShowDetailsView()
        {
            PatcherView.Visibility = Visibility.Collapsed;
            Width = 820; Height = 610;
            DetailsView.Visibility = Visibility.Visible;
            _returnToDetailsOnCancel = false;
            _activeOperation = "";
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                var answer = MessageBox.Show(this,
                    $"目前正在{_activeOperation}。確定要終止嗎？\n\n已完成的下載或檢查結果可能需要重新執行。",
                    "確認終止", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                if (_cts == null) { ShowDetailsView(); return; }
                _returnToDetailsOnCancel = true;
                _cts.Cancel();
                StatusText.Text = "正在取消操作…";
            }
            else ShowDetailsView();
        }

        private void DetailHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && e.GetPosition(this).X < ActualWidth - 45)
                DragMove();
        }

        private void PatcherBackground_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void Progress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => UpdateProgressFill();

        private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateProgressFill();

        private void UpdateProgressFill()
        {
            // Progress.Value 保留給既有的更新／驗證流程；直接計算填滿寬度，避免 WPF 範本重複顯示圖塊。
            if (ProgressTrack == null || ProgressFill == null || Progress == null) return;
            double ratio = Math.Clamp(Progress.Value / 100.0, 0, 1);
            ProgressFill.Width = Math.Max(0, (ProgressTrack.ActualWidth - 4) * ratio);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_cts == null) return;
            _cts.Cancel();
            e.Cancel = true;
            StatusText.Text = "正在取消操作，完成後即可關閉視窗…";
        }

        private bool ValidateRoot(bool popup = true)
        {
            bool ok = !string.IsNullOrWhiteSpace(GamePathText.Text) && System.IO.Directory.Exists(GamePathText.Text);
            if (!ok && popup) MessageBox.Show("目前設定的楓之谷遊戲路徑不存在，請先回到設定頁指定正確路徑。", "遊戲檔案管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return ok;
        }
        private static int ParseVersion(string s) { var d = new string((s ?? "").Where(char.IsDigit).ToArray()); return int.TryParse(d, out int v) ? v : 0; }
        private static bool GameRunning() => Process.GetProcessesByName("MapleStory").Length > 0;
        private static void WarnGameRunning() => MessageBox.Show("偵測到 MapleStory 正在執行。請先關閉遊戲後再進行更新或修復。", "遊戲檔案管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    }
}
