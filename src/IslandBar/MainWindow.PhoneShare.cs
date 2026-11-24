using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using IslandBar.Conversion;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 手機傳檔：動態島「快速設定」的「手機傳檔」、系統匣選單、或把檔案拖到動態島輪盤的「傳到手機」。
    /// 開一張小卡（QR code），手機掃了就能互傳；收到檔案時動態島跳通知，按一下開啟位置。
    /// </summary>
    public partial class MainWindow
    {
        private PhoneShare _share;
        private PhoneShareCard _shareCard;
        private static readonly string GPhone = char.ConvertFromUtf32(0xE8EA);

        private static readonly WheelAction PhoneAction = new WheelAction
        {
            Label = "手機",
            Sub = "掃 QR 傳到手機",
            Glyph = char.ConvertFromUtf32(0xE8EA)
        };

        public PhoneShare PhoneShareSession => _share;
        public PhoneShareCard PhoneShareCardWindow => _shareCard;

        /// <summary>收到的檔案存放的資料夾</summary>
        public string PhoneShareFolder =>
            !string.IsNullOrWhiteSpace(_settings.PhoneShareFolder) ? _settings.PhoneShareFolder
            : Path.Combine(DownloadWatcher.DownloadsFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "手機傳來");

        /// <summary>快速設定／系統匣：沒開就開；開著但收起來了就叫出來；正在顯示就收起來</summary>
        public void TogglePhoneShare()
        {
            if (_shareCard != null && _shareCard.IsVisible) { HidePhoneShareCard(); return; }
            OpenPhoneShare();
        }

        public void OpenPhoneShare(string[] offer = null)
        {
            if (_share == null || !_share.Running)
            {
                try { _share = PhoneShare.Start(PhoneShareFolder); }
                catch (Exception ex)
                {
                    Log.Error("PhoneShare.Start", ex);
                    ShowNotice("手機傳檔開不起來：" + ex.Message, TimeSpan.FromSeconds(5));
                    return;
                }
                HookShare(_share);
            }
            var card = EnsureShareCard();
            if (!card.IsVisible) card.Show();
            card.Reposition();
            // 動態島正在收合：動畫結束後再對齊一次
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            t.Tick += (s2, e2) => { t.Stop(); _shareCard?.Reposition(); };
            t.Start();
            if (offer != null) OfferToPhone(offer);
        }

        private void HookShare(PhoneShare s)
        {
            void Ui(Action a) => Dispatcher.BeginInvoke(a);
            s.ClientConnected += name => Ui(() =>
            {
                if (_share != s) return;
                _shareCard?.SetClient(name);
                if (_shareCard == null || !_shareCard.IsVisible) ShowHud(GPhone, null, null, $"{name} 已連上手機傳檔", TimeSpan.FromSeconds(3));
            });
            s.UploadProgress += (name, got, total) => Ui(() => { if (_share == s) _shareCard?.SetUpload(name, got, total); });
            s.UploadFailed += (name, why) => Ui(() => { if (_share == s) _shareCard?.SetUploadFailed(name, why); });
            s.FileReceived += (name, path) => Ui(() =>
            {
                if (_share != s) return;
                EnsureShareCard().AddReceivedFile(name, path);
                string saved = Path.GetFileName(path), size = "";
                try { size = " · " + FormatBytes(new FileInfo(path).Length); } catch { }
                if (!_fullscreenHidden && !_userHidden)
                {
                    ShowHud(GPhone, null, null, $"手機傳來：{saved}{size}（點這裡開啟位置）", TimeSpan.FromSeconds(5));
                    _hudClick = () => RevealInExplorer(path);
                    Peek(TimeSpan.FromSeconds(5));
                }
            });
            s.TextReceived += text => Ui(() =>
            {
                if (_share != s) return;
                _ignoreClipUntil = DateTime.UtcNow + TimeSpan.FromSeconds(1);   // 不要記進剪貼簿歷史兩次
                try { Clipboard.SetText(text); } catch { }
                EnsureShareCard().AddReceivedText(text);
                if (!_fullscreenHidden && !_userHidden)
                    ShowHud(GPhone, null, null, "手機傳來文字，已複製：" + PhoneShare.OneLine(text, 30), TimeSpan.FromSeconds(4));
            });
            s.OfferDownloaded += o => Ui(() => { if (_share == s) _shareCard?.MarkDownloaded(o); });
            s.Stopped += reason => Ui(() =>
            {
                if (_share != s) return;
                _share = null;
                try { _shareCard?.Close(); } catch { }
                _shareCard = null;
                if (reason != null) ShowNotice("手機傳檔已關閉（" + reason + "）", TimeSpan.FromSeconds(4));
            });
        }

        private PhoneShareCard EnsureShareCard()
        {
            if (_shareCard != null) return _shareCard;
            var card = new PhoneShareCard(IslandScreenRect);
            card.CloseRequested += StopPhoneShare;
            card.HideRequested += HidePhoneShareCard;
            card.FilesDropped += files => OfferToPhone(files);
            card.PickFilesRequested += () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "選擇要傳到手機的檔案" };
                if (dlg.ShowDialog() == true) OfferToPhone(dlg.FileNames);
            };
            card.ClipboardRequested += OfferClipboardToPhone;
            card.NextAddressRequested += () =>
            {
                if (_share == null) return;
                _share.RefreshAddresses();
                if (_share.Addresses.Count > 1) _share.AddressIndex = (_share.AddressIndex + 1) % _share.Addresses.Count;
                UpdateShareSession();
            };
            card.OpenFolderRequested += () =>
            {
                try
                {
                    Directory.CreateDirectory(PhoneShareFolder);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PhoneShareFolder) { UseShellExecute = true });
                }
                catch (Exception ex) { Log.Error("PhoneShare.OpenFolder", ex); }
            };
            card.RemoveOfferRequested += id => _share?.RemoveOffer(id);
            card.Closed += (s, e) => { if (_shareCard == card) _shareCard = null; };
            _shareCard = card;
            UpdateShareSession();
            if (_share?.Client != null && DateTime.UtcNow - _share.ClientSeen < TimeSpan.FromSeconds(20)) card.SetClient(_share.Client);
            return card;
        }

        private void UpdateShareSession()
        {
            if (_share == null || _shareCard == null) return;
            var addr = _share.Addresses.Count > 0 ? _share.Addresses[Math.Clamp(_share.AddressIndex, 0, _share.Addresses.Count - 1)] : null;
            _shareCard.SetSession(_share.Url, addr?.Adapter ?? "沒有網路", _share.Addresses.Count > 1, PhoneShareFolder);
            if (addr == null) _shareCard.SetWaiting("找不到網路：先連上 Wi-Fi");
        }

        public void HidePhoneShareCard()
        {
            if (_shareCard == null) return;
            _shareCard.Hide();
            if (_share != null && _share.Running)
                ShowNotice("手機傳檔還開著（快速設定 → 手機傳檔 叫出來）", TimeSpan.FromSeconds(3));
        }

        public void StopPhoneShare()
        {
            var s = _share;
            _share = null;
            s?.Stop();
            try { _shareCard?.Close(); } catch { }
            _shareCard = null;
        }

        /// <summary>把電腦上的檔案放上去讓手機下載（資料夾略過）</summary>
        public void OfferToPhone(string[] paths)
        {
            if (_share == null || !_share.Running) { OpenPhoneShare(paths); return; }
            int n = 0, folders = 0;
            foreach (var p in paths ?? Array.Empty<string>())
            {
                if (Directory.Exists(p)) { folders++; continue; }
                var o = _share.OfferFile(p);
                if (o != null) { EnsureShareCard().AddOffer(o); n++; }
            }
            var card = EnsureShareCard();
            if (!card.IsVisible) card.Show();
            if (folders > 0) ShowNotice(folders == 1 ? "資料夾不能直接傳，先壓縮成 ZIP 再拖進來" : $"{folders} 個資料夾略過了（先壓縮成 ZIP）", TimeSpan.FromSeconds(4));
        }

        private void OfferClipboardToPhone()
        {
            if (_share == null) return;
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                    if (files.Length > 0) { OfferToPhone(files); return; }
                }
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        string dir = Path.Combine(Path.GetTempPath(), "IslandBar", "share");
                        Directory.CreateDirectory(dir);
                        string path = Path.Combine(dir, $"剪貼簿圖片_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(img));
                        using (var fs = File.Create(path)) enc.Save(fs);
                        OfferToPhone(new[] { path });
                        return;
                    }
                }
                if (Clipboard.ContainsText())
                {
                    var o = _share.OfferText(Clipboard.GetText());
                    if (o != null) { EnsureShareCard().AddOffer(o); return; }
                }
                ShowNotice("剪貼簿是空的（可以複製文字、圖片或檔案）", TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                Log.Error("PhoneShare.Clipboard", ex);
                ShowNotice("讀不到剪貼簿，再試一次", TimeSpan.FromSeconds(3));
            }
        }

        private void PhoneTile_Click(object sender, RoutedEventArgs e)
        {
            SetMode(IslandMode.Compact);
            TogglePhoneShare();
        }
    }
}
