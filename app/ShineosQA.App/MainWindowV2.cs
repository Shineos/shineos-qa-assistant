// ShineosQA.App (v2) - C#バックエンド(llama.cpp直接実行)の UI を WebView2 で表示するデスクトップアプリ
// - 起動: バックエンドの /health 待ち → http://127.0.0.1:8300 を表示
//   （バックエンドの起動自体はランチャ launch.vbs が行う。本アプリはプロセスを起動しない）
// - 終了: バックエンドを停止（Job Object により llama-server 子プロセスも道連れになる）
// - ローディング中はスピナー、初回起動時は3ステップガイドを表示
// - ビルド: build.ps1（.NET Framework 4.x csc 使用・SDK 不要）
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ShineosQA
{
    public class MainWindow : Window
    {
        const int DefaultPort = 8300;

        readonly string AppUrl;
        readonly string HealthUrl;
        readonly int Port;
        readonly string appDir;

        readonly WebView2 webView = new WebView2();
        readonly Grid loadingPanel;
        readonly Grid guidePanel;
        Grid kbGuidePanel; // C#5相当のcscのためNull許容参照型は使えない（ctorで必ず初期化）
        readonly System.Windows.Shapes.Path spinner;
        readonly TextBlock overlayTitle;
        readonly TextBlock overlayMessage;
        readonly Button retryButton;
        readonly string firstRunFile;
        readonly string appLogFile;
        readonly string userDataDir;
        bool closing;

        public MainWindow()
        {
            // config.json からポートを読む（ラッパーはバックエンドと同じフォルダに配置）
            appDir = AppDomain.CurrentDomain.BaseDirectory;
            int port = DefaultPort;
            try
            {
                string cfgPath = Path.Combine(appDir, "config.json");
                if (File.Exists(cfgPath))
                {
                    var m = Regex.Match(File.ReadAllText(cfgPath), "\"port\"\\s*:\\s*(\\d+)");
                    if (m.Success) port = int.Parse(m.Groups[1].Value);
                }
            }
            catch { }
            Port = port;
            // ユーザーごとのデータフォルダ（初回ガイドのフラグ・アプリログ・WebView2プロファイル）
            userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShineosQA");
            try { Directory.CreateDirectory(userDataDir); } catch { }
            firstRunFile = Path.Combine(userDataDir, "first_run.txt");
            appLogFile = Path.Combine(userDataDir, "app.log");
            AppUrl = "http://127.0.0.1:" + Port + "/";
            HealthUrl = "http://127.0.0.1:" + Port + "/health";

            Title = "社内知恵袋";
            Width = 1200;
            Height = 800;
            MinWidth = 800;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;

            string ico = Path.Combine(appDir, "assets", "app.ico");
            try
            {
                if (File.Exists(ico))
                    Icon = new IconBitmapDecoder(new Uri(Path.GetFullPath(ico)), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad).Frames[0];
            }
            catch { }

            var root = new Grid();

            // ローディングパネル（WebView2 は HWND ベースのため WPF 要素を重ねられない。
            // ローディング中は WebView2 を非表示にして表示を切り替える）
            loadingPanel = new Grid { Background = Brushes.White, Visibility = Visibility.Visible };
            var center = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            spinner = new System.Windows.Shapes.Path
            {
                Width = 56,
                Height = 56,
                Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0xA3, 0x7F)),
                StrokeThickness = 5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 28,2.5 A 25.5,25.5 0 1 1 2.5,28"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };
            var spin = new RotateTransform(0);
            spinner.RenderTransform = spin;
            spinner.RenderTransformOrigin = new Point(0.5, 0.5);
            spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1000)) { RepeatBehavior = RepeatBehavior.Forever });

            overlayTitle = new TextBlock
            {
                Text = "社内知恵袋",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            overlayMessage = new TextBlock
            {
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            retryButton = new Button
            {
                Content = "再試行",
                FontSize = 15,
                Padding = new Thickness(30, 6, 30, 6),
                Margin = new Thickness(0, 26, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            retryButton.Click += async (s, e) => { retryButton.Visibility = Visibility.Collapsed; ShowLoading("確認しています..."); await Startup(); };

            center.Children.Add(spinner);
            center.Children.Add(overlayTitle);
            center.Children.Add(overlayMessage);
            center.Children.Add(retryButton);
            loadingPanel.Children.Add(center);

            // 初回起動時の「はじめにガイド」（v2 UIに合わせた3ステップ）
            guidePanel = new Grid { Background = Brushes.White, Visibility = Visibility.Collapsed };
            var guideCenter = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 620,
                Margin = new Thickness(40, 0, 40, 0)
            };
            guideCenter.Children.Add(new TextBlock
            {
                Text = "社内知恵袋へようこそ",
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            });
            guideCenter.Children.Add(new TextBlock
            {
                Text = "社内の規定・マニュアルから、根拠つきで回答する社内Q&Aツールです。",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 28)
            });
            AddGuideStep(guideCenter, "1", "質問する",
                "画面下の入力欄に「経費精算の手順は？」のように入力して送信します。\n回答には根拠（文書名・該当箇所）が付きます。");
            AddGuideStep(guideCenter, "2", "資料を追加する",
                "画面下部の📎ボタンでファイルをその場で登録できるほか、「ナレッジ」タブから\nPDF・Word・Markdown をまとめて追加できます。");
            AddGuideStep(guideCenter, "3", "モデルを切り替える",
                "画面下のモデルボタン（⚡/🎯/🏆）で回答モデルを切り替えられます。\n速度重視は1.7B、精度重視は4B・30B（未導入ならその場からダウンロード）。");
            guideCenter.Children.Add(new TextBlock
            {
                Text = "※ Web 検索は最初は OFF になっています。ON にすると、入力した質問が外部の検索サービスに送信されます。社内情報を質問するときは OFF のままにしてください。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x5A, 0x00)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            });
            var guideCheckbox = new CheckBox
            {
                Content = "次回からこの案内を表示しない",
                FontSize = 13,
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))
            };
            guideCenter.Children.Add(guideCheckbox);
            var guideButton = new Button
            {
                Content = "はじめる",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(48, 10, 48, 10),
                Margin = new Thickness(0, 30, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0xA3, 0x7F)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            guideButton.Click += (s, e) =>
            {
                if (guideCheckbox.IsChecked == true)
                {
                    try { File.WriteAllText(firstRunFile, "1"); } catch { }
                }
                HideGuide();
                MaybeShowKnowledgeGuide(); // ウェルカム直後は同じ流れでナレッジ登録案内へ
            };
            guideCenter.Children.Add(guideButton);
            guidePanel.Children.Add(guideCenter);

            // インストール（更新）直後の「ナレッジ登録案内」。インストールマーカーの時刻で
            // 新しいインストールを検知し、1回だけ表示する（チェックで今回のインストールでは非表示）
            kbGuidePanel = new Grid { Background = Brushes.White, Visibility = Visibility.Collapsed };
            var kbCenter = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560,
                Margin = new Thickness(40, 0, 40, 0)
            };
            kbCenter.Children.Add(new TextBlock
            {
                Text = "ナレッジを登録しましょう",
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });
            kbCenter.Children.Add(new TextBlock
            {
                Text = "社内の規定・マニュアルを登録すると、その文書に基づいて根拠付きで回答できます。\n" +
                       "PDF・Word・Markdown・テキストに対応。ナレッジタブにドラッグ＆ドロップするだけです。\n" +
                       "登録しなくても、同梱のサンプル質問で動作確認はできます。",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 24)
            });
            var kbCheckbox = new CheckBox
            {
                Content = "次回から表示しない",
                FontSize = 13,
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))
            };
            var kbButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 0)
            };
            var kbPrimary = new Button
            {
                Content = "ナレッジ登録へ進む",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(28, 9, 28, 9),
                Margin = new Thickness(0, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0xA3, 0x7F)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            var kbLater = new Button
            {
                Content = "あとで",
                FontSize = 15,
                Padding = new Thickness(28, 9, 28, 9),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
            };
            kbPrimary.Click += (s, e) =>
            {
                if (kbCheckbox.IsChecked == true) MarkKnowledgeGuideSeen();
                HideKnowledgeGuide();
                pendingKnowledgeTab = true;
                TryNavigateToKnowledgeTab(); // WebViewロード済みなら即遷移、未ロードならNavigationCompletedで
            };
            kbLater.Click += (s, e) =>
            {
                if (kbCheckbox.IsChecked == true) MarkKnowledgeGuideSeen();
                HideKnowledgeGuide();
            };
            kbButtons.Children.Add(kbPrimary);
            kbButtons.Children.Add(kbLater);
            kbCenter.Children.Add(kbCheckbox);
            kbCenter.Children.Add(kbButtons);
            kbGuidePanel.Children.Add(kbCenter);

            webView.Visibility = Visibility.Collapsed;
            root.Children.Add(loadingPanel);
            root.Children.Add(guidePanel);
            root.Children.Add(kbGuidePanel);
            root.Children.Add(webView);
            Content = root;

            Loaded += async (s, e) => { try { await Startup(); } catch (Exception ex) { Log("startup exception: " + ex.Message); ShowError("起動できませんでした。\n\nしばらく待ってから「再試行」を押してください。\n解決しない場合は、管理者にご相談ください。", true); } };
            Closed += (s, e) => StopBackend();
        }

        void ShowLoading(string msg)
        {
            spinner.Visibility = Visibility.Visible;
            retryButton.Visibility = Visibility.Collapsed;
            overlayMessage.Text = msg;
            overlayMessage.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            loadingPanel.Visibility = Visibility.Visible;
            webView.Visibility = Visibility.Collapsed;
        }

        void ShowError(string msg, bool showRetry)
        {
            spinner.Visibility = Visibility.Collapsed;
            retryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
            overlayMessage.Text = msg;
            overlayMessage.Foreground = new SolidColorBrush(Colors.DarkRed);
            loadingPanel.Visibility = Visibility.Visible;
            webView.Visibility = Visibility.Collapsed;
        }

        void HideLoading()
        {
            webView.Visibility = Visibility.Visible;
            loadingPanel.Visibility = Visibility.Collapsed;
        }

        void ShowGuide()
        {
            loadingPanel.Visibility = Visibility.Collapsed;
            guidePanel.Visibility = Visibility.Visible;
            webView.Visibility = Visibility.Collapsed;
        }

        void HideGuide()
        {
            guidePanel.Visibility = Visibility.Collapsed;
            webView.Visibility = Visibility.Visible;
        }

        // ---- ナレッジ登録案内（インストール/更新のたび1回） ----

        string KbMarkerPath() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "install.completed"); }
        string KbSeenPath() { return Path.Combine(userDataDir, "kb_guide_seen.txt"); }
        bool pendingKnowledgeTab;

        /// <summary>「今回のインストール」を一意に識別するキー。
        /// Innoインストールでは install.completed の更新時刻、MSIX ではラッパーexeのバージョン
        /// （MSIXにマーカーは無いため。バージョンが変わる＝更新で、案内が再度表示される）</summary>
        string KbInstallKey()
        {
            try
            {
                if (File.Exists(KbMarkerPath())) return "t:" + File.GetLastWriteTimeUtc(KbMarkerPath()).Ticks;
            }
            catch { }
            return "v:" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        }

        /// <summary>今回のインストールに対して案内未表示ならtrue</summary>
        bool ShouldShowKnowledgeGuide()
        {
            try
            {
                var key = KbInstallKey();
                var seen = File.Exists(KbSeenPath()) ? File.ReadAllText(KbSeenPath()).Trim() : "";
                return seen != key;
            }
            catch { return false; }
        }

        void MarkKnowledgeGuideSeen()
        {
            try { File.WriteAllText(KbSeenPath(), KbInstallKey()); } catch { }
        }

        void MaybeShowKnowledgeGuide()
        {
            if (!ShouldShowKnowledgeGuide()) return;
            Log("showing knowledge registration guide");
            loadingPanel.Visibility = Visibility.Collapsed;
            guidePanel.Visibility = Visibility.Collapsed;
            kbGuidePanel.Visibility = Visibility.Visible;
            webView.Visibility = Visibility.Collapsed;
        }

        void HideKnowledgeGuide()
        {
            kbGuidePanel.Visibility = Visibility.Collapsed;
            webView.Visibility = Visibility.Visible;
        }

        /// <summary>Web UIのナレッジタブへ切替（WebViewロード済みの場合のみ実行）</summary>
        async void TryNavigateToKnowledgeTab()
        {
            try
            {
                if (webView.CoreWebView2 == null) return; // 未ロードなら NavigationCompleted で再試行
                await webView.ExecuteScriptAsync("var t=document.querySelector('[data-tab=\"knowledge\"]'); if(t) t.click();");
                pendingKnowledgeTab = false;
            }
            catch (Exception ex) { Log("navigate knowledge tab failed: " + ex.Message); }
        }

        void AddGuideStep(StackPanel parent, string number, string title, string desc)
        {
            var step = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            header.Children.Add(new TextBlock
            {
                Text = number,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0xA3, 0x7F)),
                Padding = new Thickness(12, 2, 12, 2),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                VerticalAlignment = VerticalAlignment.Center
            });
            step.Children.Add(header);
            step.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 13.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(37, 0, 0, 0)
            });
            parent.Children.Add(step);
        }

        void Log(string msg)
        {
            try { File.AppendAllText(appLogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine); }
            catch { }
        }

        // 指定ポートのリスナーを所有するPID（IP Helper API・外部プロセス起動なし）
        int GetPortOwnerPid(int port)
        {
            try
            {
                const int AF_INET = 2;
                const int TCP_TABLE_OWNER_PID_LISTENER = 3;
                const int ERROR_INSUFFICIENT_BUFFER = 122;
                int size = 0;
                uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (rc != 0 && rc != ERROR_INSUFFICIENT_BUFFER) return 0;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return 0;
                    int count = Marshal.ReadInt32(buf, 0);
                    IntPtr row = new IntPtr(buf.ToInt64() + 4);
                    for (int i = 0; i < count; i++)
                    {
                        int state = Marshal.ReadInt32(row, 0);
                        int localPort = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);
                        int pid = Marshal.ReadInt32(row, 20);
                        if (state == 2 /*LISTEN*/ && localPort == port) return pid;
                        row = new IntPtr(row.ToInt64() + 24);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return 0;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int tableSize, bool order, int ipVersion, int tableClass, int reserved);

        // URLを既定のブラウザで開くWin32 API（引数は定数のみを渡す）
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr ShellExecute(IntPtr hwnd, string verb, string file, string parameters, string directory, int showCmd);

        void OpenInDefaultBrowser()
        {
            ShellExecute(IntPtr.Zero, "open", "https://shineos.com/", null, null, 5 /* SW_SHOW */);
        }

        void OpenInDefaultBrowser(string url)
        {
            // NewWindowRequestedからは実際のリンク先（例: shineos.com/contact/）をそのまま開く
            ShellExecute(IntPtr.Zero, "open", url, null, null, 5 /* SW_SHOW */);
        }

        bool WaitForHealth(int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(HealthUrl);
                    req.Timeout = 3000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.StatusCode == HttpStatusCode.OK) return true;
                    }
                }
                catch { }
                Thread.Sleep(1000);
            }
            return false;
        }

        /// <summary>バックエンドを非表示で起動する（起動引数・作業ディレクトリは固定値のみ）。
        /// MSIXでは本exeがパッケージのエントリのため、vbsランチャに代わる自己起動経路</summary>
        void StartBackendHidden()
        {
            try
            {
                string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ShineosQA.Backend.exe");
                if (!File.Exists(exe)) { Log("backend exe not found: " + exe); return; }
                var r = StartProcessHidden(exe, "--config config.json", AppDomain.CurrentDomain.BaseDirectory);
                Log("started backend (ShellExecute result=" + r + ")");
            }
            catch (Exception ex) { Log("start backend failed: " + ex.Message); }
        }

        static IntPtr StartProcessHidden(string file, string parameters, string directory)
        {
            return ShellExecute(IntPtr.Zero, "open", file, parameters, directory, 0 /* SW_HIDE */);
        }

        async Task Startup()
        {
            ShowLoading("社内知恵袋 を起動しています...");

            // バックエンドは通常 launch.vbs が起動済み。未起動ならラッパー自身が起動する
            // （MSIXパッケージでは launch.vbs を経由せず本exeが直接エントリになるため必須。
            //   3秒待っても応答が無い場合のみ起動し、既存起動との二重起動を避ける）
            if (!await Task.Run(() => WaitForHealth(3)))
            {
                StartBackendHidden();
            }
            bool running = await Task.Run(() => WaitForHealth(120));
            if (!running)
            {
                int owner = GetPortOwnerPid(Port);
                if (owner != 0)
                {
                    Log("port " + Port + " owned by foreign pid=" + owner);
                    ShowError("起動できませんでした。\n\n別のアプリがこのツールの通信先（ポート " + Port + "）を使用しています。\nそのアプリを終了してから、スタートメニューの「社内知恵袋」をもう一度開いてください。", false);
                }
                else
                {
                    ShowError("AIエンジンの起動を確認できませんでした。\n\nしばらく待ってから、スタートメニューの「社内知恵袋」をもう一度開いてください。\n解決しない場合は、管理者にご相談ください。", false);
                }
                return;
            }
            Log("backend healthy on port " + Port);

            // 初回起動時は「はじめにガイド」、インストール直後は「ナレッジ登録案内」を表示
            if (File.Exists(firstRunFile)) { HideLoading(); MaybeShowKnowledgeGuide(); }
            else ShowGuide(); // ウェルカムの「はじめる」から MaybeShowKnowledgeGuide へ続く

            // WebView2 のユーザーデータフォルダは %APPDATA% 配下に明示指定する
            // （インストール先直下は書き込み不可の場合があるため）
            string wvDataDir = Path.Combine(userDataDir, "WebView2");
            try { Directory.CreateDirectory(wvDataDir); } catch { }
            try
            {
                var wvEnv = await CoreWebView2Environment.CreateAsync(null, wvDataDir);
                await webView.EnsureCoreWebView2Async(wvEnv);
                // 一般ユーザー向けデスクトップアプリとして、ブラウザ風の右クリックメニューと
                // DevTools（F12/検査）は無効化する（誤操作・内部構造の露出を防ぐ）
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                // ヘッダーの「powered by Shineos」・お問い合わせリンクは既定のブラウザで開く。
                // 開けるのは公式サイト（shineos.com / www.shineos.com）のみで、リンク先URLそのまま
                // （ページ側からの差し込みは受け付けない）。ホワイトリスト外の target=_blank は
                // WebView2 既定の挙動に任せる
                webView.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    try
                    {
                        string host = new Uri(e.Uri).Host;
                        if (host == "shineos.com" || host == "www.shineos.com")
                        {
                            e.Handled = true;
                            OpenInDefaultBrowser(e.Uri);
                        }
                    }
                    catch (Exception ex) { Log("open external link failed: " + ex.Message); }
                };
                // ナレッジ登録案内の「進む」をWebViewロード前に押した場合、ロード完了後にタブ遷移する
                webView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    if (pendingKnowledgeTab) TryNavigateToKnowledgeTab();
                };
            }
            catch (Exception ex)
            {
                Log("webview2 init failed: " + ex.Message);
                ShowError("画面コンポーネント（WebView2）を初期化できませんでした。\n\nWindows Update で OS を最新にしてから再度お試しください。", false);
                return;
            }
            webView.Source = new Uri(AppUrl);
        }

        // ウィンドウを閉じたらバックエンドも停止する（llama-server は Job Object で道連れ）
        void StopBackend()
        {
            if (closing) return;
            closing = true;
            try
            {
                foreach (var p in Process.GetProcessesByName("ShineosQA.Backend"))
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                }
            }
            catch { }
        }
    }

    public static class Program
    {
        static Mutex singleInstance;

        [STAThread]
        public static void Main()
        {
            // 二重起動防止: 既に起動している場合は何もせず終了する
            bool created;
            singleInstance = new Mutex(true, "ShineosQA-v2-SingleInstance", out created);
            if (!created) return;
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.Run(new MainWindow());
            }
            finally { singleInstance.ReleaseMutex(); }
        }
    }
}
