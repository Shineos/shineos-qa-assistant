// ShineosQA.App - Open WebUI を WebView2 でラップするデスクトップアプリ
// - 起動: サービス起動確認 → /health 待ち → http://localhost:8080 を表示
// - 終了: サービスを停止（閉じたら localhost:8080 も閉じる）
// - ローディング中は中央にスピナー付きメッセージを表示
//   ※ WebView2 は HWND ベースのため WPF 要素を上に重ねられない。
//     ローディング中は WebView2 を非表示にし、完了時に表示を切り替える。
// - ポート8080が他アプリに占有されている場合は誤表示せず中央にエラー表示
//   （サービスの子プロセス（python）が8080を持つ場合は「自サービス」と判定）
// - ビルド: build.ps1（.NET Framework 4.x csc 使用・SDK 不要）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ShineosQA
{
    public class MainWindow : Window
    {
        const string ServiceName = "ShineosQA";

        readonly string AppUrl;
        readonly string HealthUrl;
        readonly int Port;

        readonly WebView2 webView = new WebView2();
        readonly Grid loadingPanel;
        readonly Grid guidePanel;
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
            // 使用ポートを {app}\port.txt から読む（無ければ 8080）
            int port = 8080;
            try
            {
                string pf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "port.txt");
                if (File.Exists(pf))
                {
                    string s = File.ReadAllText(pf).Trim();
                    int p;
                    if (int.TryParse(s, out p) && p > 0) port = p;
                }
            }
            catch { }
            Port = port;
            // ユーザーごとのデータフォルダ（Program Files 配下は一般ユーザーが書き込めないため）
            // 初回起動ガイドのフラグ・アプリログをここに保存する
            userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShineosQA");
            try { Directory.CreateDirectory(userDataDir); } catch { }
            firstRunFile = Path.Combine(userDataDir, "first_run.txt");
            appLogFile = Path.Combine(userDataDir, "app.log");
            // 127.0.0.1 を明示指定する（localhost は ::1（IPv6）が優先解決され、
            // Open WebUI（IPv4 のみで LISTEN）に接続できないため）
            AppUrl = "http://127.0.0.1:" + Port + "/?lang=ja-JP";
            HealthUrl = "http://127.0.0.1:" + Port + "/health";

            Title = "社内知恵袋";
            Width = 1200;
            Height = 800;
            MinWidth = 800;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;

            string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "assets", "app.ico");
            try
            {
                if (File.Exists(ico))
                    Icon = new IconBitmapDecoder(new Uri(Path.GetFullPath(ico)), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad).Frames[0];
            }
            catch { }

            var root = new Grid();

            // ローディングパネル（WebView2 は HWND ベースのため WPF 要素を
            // 上に重ねられない。ローディング中は WebView2 を非表示にして
            // 表示を切り替える方式にする）
            loadingPanel = new Grid
            {
                Background = Brushes.White,
                Visibility = Visibility.Visible
            };
            var center = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Material Design 風のリングスピナー（270° の円弧を中心で自転）
            spinner = new System.Windows.Shapes.Path
            {
                Width = 56,
                Height = 56,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2B, 0x5C, 0xE3)),
                StrokeThickness = 5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 28,2.5 A 25.5,25.5 0 1 1 2.5,28"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };
            var spin = new RotateTransform(0);
            spinner.RenderTransform = spin;
            // 回転中心をリング中心（要素中央）に指定し、自転させる
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
            retryButton.Click += async (s, e) => { retryButton.Visibility = Visibility.Collapsed; ShowLoading("起動しています..."); await Startup(); };

            center.Children.Add(spinner);
            center.Children.Add(overlayTitle);
            center.Children.Add(overlayMessage);
            center.Children.Add(retryButton);
            loadingPanel.Children.Add(center);

            // 初回起動時の「はじめにガイド」（非エンジニア向け・3ステップ）
            // 初回起動時のみ表示し、「はじめる」で閉じる。以降は表示しない
            guidePanel = new Grid
            {
                Background = Brushes.White,
                Visibility = Visibility.Collapsed
            };
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
                "画面左の「ナレッジ」メニューから、PDF やマニュアルをドラッグ＆ドロップで追加できます。");
            AddGuideStep(guideCenter, "3", "モデルを切り替える",
                "画面右上のモデル選択で「経費精算ガイド」「ITヘルプデスク」に切り替えられます。");
            // Web 検索の注意（社内情報の外部送信を防ぐための初回警告）
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
            // 次回からガイドを表示しないためのチェックボックス（チェック時のみフラグを作成）
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
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x5C, 0xE3)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            guideButton.Click += (s, e) =>
            {
                // チェック時のみ初回起動フラグを記録（次回からガイドを表示しない）
                if (guideCheckbox.IsChecked == true)
                {
                    try { File.WriteAllText(firstRunFile, "1"); } catch { }
                }
                HideGuide();
            };
            guideCenter.Children.Add(guideButton);
            guidePanel.Children.Add(guideCenter);

            // WebView2 は初期状態で非表示（ローディング完了後に表示）
            webView.Visibility = Visibility.Collapsed;

            root.Children.Add(loadingPanel);
            root.Children.Add(guidePanel);
            root.Children.Add(webView);
            Content = root;

            Loaded += async (s, e) => { try { await Startup(); } catch (Exception ex) { Log("startup exception: " + ex.Message); ShowError("起動できませんでした。\n\nしばらく待ってから「再試行」を押してください。\n解決しない場合は、管理者にご相談ください。", true); } };
            Closed += (s, e) => StopService();
            Application.Current.SessionEnding += (s, e) => { closing = true; };
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

        // 初回起動ガイド（非エンジニア向けの3ステップ説明）を表示する
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

        void AddGuideStep(StackPanel parent, string number, string title, string desc)
        {
            var step = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            header.Children.Add(new TextBlock
            {
                Text = number,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x5C, 0xE3)),
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
            try
            {
                // Program Files 配下は一般ユーザーが書き込めないため %APPDATA%\ShineosQA に保存
                File.AppendAllText(appLogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        bool IsServiceRunning()
        {
            // sc.exe の代わりに ServiceController で状態を確認する（外部プロセスを起動しない v1.0.60）
            try
            {
                using (var sc = new ServiceController(ServiceName))
                    return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        int GetServicePid()
        {
            // ServiceController は PID を返さないため WMI で取得する
            try
            {
                var searcher = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE Name='" + ServiceName + "'");
                foreach (ManagementBaseObject obj in searcher.Get())
                    return Convert.ToInt32(obj["ProcessId"]);
            }
            catch { }
            return 0;
        }

        int GetParentPid(int pid)
        {
            try
            {
                var searcher = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid);
                foreach (ManagementBaseObject obj in searcher.Get())
                    return Convert.ToInt32(obj["ParentProcessId"]);
            }
            catch { }
            return 0;
        }

        // pid の祖先チェーン（最大5世代）に svcPid が含まれるか
        bool IsDescendantOf(int pid, int svcPid)
        {
            int cur = pid;
            for (int i = 0; i < 5 && cur != 0; i++)
            {
                if (cur == svcPid) return true;
                cur = GetParentPid(cur);
            }
            return false;
        }

        int GetPortOwnerPid(int port)
        {
            // netstat.exe を起動せず、IP Helper API でリスナーポートの所有 PID を取得する
            // （外部プロセスを起動しないため高速かつ安全。v1.0.60）
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
                    // MIB_TCPROW_OWNER_PID: state(4) localAddr(4) localPort(4) remoteAddr(4) remotePort(4) pid(4) = 24バイト
                    // localPort はネットワークバイトオーダーで格納されているため、byte8<<8|byte9 が実ポート値になる
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

        // sc.exe の代わりに ServiceController でサービスを制御する（外部プロセスを起動しない v1.0.60）
        void ScStop()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                }
            }
            catch { }
        }

        void ScStart()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch { }
        }

        // 前回セッションの残骸を掃除する（v1.0.57）。
        // アプリ強制終了等で閉じる処理が走らなかった場合に備え、起動時に2通りを対処:
        //  (1) サービス停止中なのに自前（venv 配下）の python が生きている → kill 試行
        //      （SYSTEM 権限で動く python は非管理者から kill 不可のため、失敗時は
        //        従来どおりポート占有エラーを表示して案内する）
        //  (2) サービス稼働中なのにポートの持ち主がサービス配下でない（WebUI ハング等）
        //      → サービス停止→起動で回復（サービス制御権はインストーラが利用者に付与済み）
        void SweepOrphanWebUi()
        {
            try
            {
                string venvDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "venv")) + Path.DirectorySeparatorChar;
                bool svcRunning = IsServiceRunning();

                if (svcRunning)
                {
                    int owner = GetPortOwnerPid(Port);
                    int svcPid = GetServicePid();
                    if (owner != 0 && !IsDescendantOf(owner, svcPid))
                    {
                        Log("sweep: port owner pid=" + owner + " is not under service (svc=" + svcPid + ") - restarting service");
                        ScStop();
                        System.Threading.Thread.Sleep(5000);
                        svcRunning = false;
                    }
                    else return; // 正常稼働中（PC起動時の自動起動含む）は掃除不要
                }

                var orphans = new List<Process>();
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name='python.exe' OR Name='pythonw.exe'"))
                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    string path = obj["ExecutablePath"] as string;
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!path.StartsWith(venvDir, StringComparison.OrdinalIgnoreCase)) continue;
                    try { orphans.Add(Process.GetProcessById(Convert.ToInt32(obj["ProcessId"]))); } catch { }
                }
                foreach (var p in orphans)
                {
                    try
                    {
                        Log("sweep: killing orphan webui python pid=" + p.Id);
                        p.Kill();
                    }
                    catch (Exception ex) { Log("sweep: kill failed pid=" + p.Id + " (" + ex.Message + ")"); }
                }
                if (orphans.Count > 0) System.Threading.Thread.Sleep(2000);
            }
            catch (Exception ex) { Log("sweep error: " + ex.Message); }
        }

        bool ServiceExists()
        {
            // sc.exe の代わりに ServiceController で存在確認する（v1.0.60）
            try
            {
                using (var sc = new ServiceController(ServiceName))
                    return sc.Status != 0;
            }
            catch { return false; }
        }

        // Open WebUI へのパッチ適用状態を自己診断する（v1.0.52）。
        // パッチが外れていると RAG（社内文書検索）が静かに止まり、根拠の無い回答の
        // 原因になるため、起動時に検査して壊れていたら利用者に明示する。
        // v1.0.60: python サブプロセスを起動せず、C# から site-packages を直接読んで検査する。
        // { 相対パス, マーカー文字列, 説明 }
        static readonly string[][] PatchMarkers = new[]
        {
            new[] { @"utils\models.py",     "OLLAMA_HIDDEN_MODELS",                              "モデル一覧の最適化パッチ（ベースモデル非表示）" },
            new[] { @"utils\middleware.py", "[Shineos patch] model knowledge -> metadata.files", "ナレッジRAG注入パッチ（検索結果の自動注入）" },
            new[] { @"utils\middleware.py", "[Shineos patch] 空クエリ対策",                      "検索語フォールバックパッチ（空クエリ対策）" },
            new[] { @"utils\middleware.py", "[Shineos patch] no-match guard",                    "no-match guard（創作回答防止）" },
            new[] { @"env.py",              "[Shineos patch] no auto (Open WebUI) suffix",       "名称表示パッチ（(Open WebUI) 重複付加の除去）" },
            new[] { @"retrieval\loaders\main.py", "[Shineos patch] offline pptx loader (python-pptx)", "PPTX読込パッチ（python-pptxで直接読込）" },
            new[] { @"routers\knowledge.py", "[Shineos patch] knowledge: friendly rejection for non-text files", "登録不可形式の日本語エラーパッチ" },
            new[] { @"routers\ollama.py",     "[Shineos patch] history truncation (keep last 12)", "履歴打ち切りパッチ（長い会話のプリフィル遅延対策）" },
        };

        void CheckPatchHealth()
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "venv", "Lib", "site-packages", "open_webui"));
                if (!Directory.Exists(root)) { Log("patch health: open_webui not found"); return; }
                foreach (var m in PatchMarkers)
                {
                    string path = Path.Combine(root, m[0].Replace('\\', Path.DirectorySeparatorChar));
                    string content = File.Exists(path) ? File.ReadAllText(path) : "";
                    if (!content.Contains(m[1]))
                    {
                        Log("patch health check FAILED: " + m[2]);
                        System.Windows.MessageBox.Show(
                            "内部コンポーネント（Open WebUI）の状態が正常ではありません。\n" +
                            "社内文書を参照した回答ができない可能性があります。\n\n" +
                            "デスクトップの「ShineosQA-repair」を実行するか、\n" +
                            "社内知恵袋を再インストールしてください。\n" +
                            "解決しない場合は https://shineos.com/contact/ までご連絡ください。",
                            "社内知恵袋 - 注意", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                Log("patch health check OK");
            }
            catch (Exception ex) { Log("patch health check error: " + ex.Message); }
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
                        if (resp.StatusCode == HttpStatusCode.OK)
                            return true;
                    }
                }
                catch { }
                Thread.Sleep(2000);
            }
            return false;
        }

        async Task Startup()
        {
            ShowLoading("社内知恵袋 を起動しています...");

            if (!ServiceExists())
            {
                ShowError("社内知恵袋 がインストールされていません。\n\nインストーラ（ShineosQA-Setup.exe）を実行してください。", false);
                return;
            }

            // 前回の残骸（孤児化した WebUI python）を掃除してからポート判定する（v1.0.57）
            SweepOrphanWebUi();

            // ポート占有チェック（サービス停止中なら 8080 を持つのは必ず別アプリ）
            // 占有されている場合はサービスを起動せずエラー表示する
            int svcPid = GetServicePid();
            int owner = GetPortOwnerPid(Port);
            Log("port " + Port + " owner pid=" + owner + " (service pid=" + svcPid + ")");
            if (owner != 0 && !(svcPid != 0 && IsDescendantOf(owner, svcPid)))
            {
                ShowError("起動できませんでした。\n\n別のアプリがこのツールの通信先を使用しています。\nそのアプリを終了してから「再試行」を押してください。", true);
                return;
            }

            if (!IsServiceRunning())
            {
                ShowLoading("サービスを起動しています...");
                ScStart();
            }

            svcPid = 0;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                svcPid = GetServicePid();
                if (svcPid != 0) break;
                Thread.Sleep(1000);
            }
            Log("service pid=" + svcPid);

            ShowLoading("Open WebUI の起動を待っています（初回は数分かかることがあります）...");
            bool ok = await Task.Run(() => WaitForHealth(240));
            if (!ok)
            {
                ShowError("起動できませんでした。\n\nしばらく待ってから「再試行」を押してください。\n解決しない場合は、管理者にご相談ください。", true);
                return;
            }

            owner = GetPortOwnerPid(Port);
            svcPid = GetServicePid();
            Log("final port owner pid=" + owner + " (service pid=" + svcPid + ", descendant=" + (owner != 0 && IsDescendantOf(owner, svcPid)) + ")");

            if (owner != 0 && !IsDescendantOf(owner, svcPid))
            {
                ShowError("起動できませんでした。\n\n別のアプリがこのツールの通信先を使用しています。\nそのアプリを終了してから「再試行」を押してください。", true);
                return;
            }

            // 初回起動時は「はじめにガイド」を表示（WebView2 は裏で読み込みを進める）
            Log("first_run check: " + firstRunFile + " exists=" + File.Exists(firstRunFile));
            if (File.Exists(firstRunFile))
                HideLoading();
            else
                ShowGuide();
            // パッチ健全性チェック（RAGが静かに壊れているのを防ぐ: v1.0.52）
            CheckPatchHealth();
            // WebView2 のユーザーデータフォルダを明示指定する
            // （既定では実行ファイルの隣（Program Files 配下）に作成しようとして
            //   アクセス拒否 E_ACCESSDENIED になるため、%APPDATA%\ShineosQA\WebView2 を使う）
            string wvDataDir = Path.Combine(userDataDir, "WebView2");
            try { Directory.CreateDirectory(wvDataDir); } catch { }
            var wvEnv = await CoreWebView2Environment.CreateAsync(null, wvDataDir);
            await webView.EnsureCoreWebView2Async(wvEnv);
            // 「新機能（What's New）」ダイアログを非表示にする（v1.0.49）
            // 【原因】Open WebUI 0.11 はユーザー設定 settings.showChangelog（既定 true）が有効で、
            //   保存済みバージョン ≠ 現在バージョンのときにモーダルを開く。
            //   旧 inject の localStorage キー（whatsNewSeenVersion 等）は 0.11 では無関係で、
            //   自動クリックも ja-JP ボタン文言「OK、始めましょう！」に一致せず閉じられていなかった。
            // 【対策】(1) ドキュメント生成時（アプリJSより前）に settings.showChangelog=false を書き込み、
            //   モーダルが開かないようにする。(2) 万一表示されても自動で閉じる。
            string changelogOffScript =
                "if(window.top===window){try{" +
                "var s={};try{s=JSON.parse(localStorage.getItem('settings')||'{}')}catch(e){}" +
                "if(s.showChangelog!==false){s.showChangelog=false;localStorage.setItem('settings',JSON.stringify(s));}" +
                "}catch(e){}}";
            try
            {
                webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(changelogOffScript);
            }
            catch { }
            webView.NavigationCompleted += async (s, e) =>
            {
                try
                {
                    await webView.ExecuteScriptAsync(
                        "(function(){" +
                        // v1.0.56: ヘッダー（サイドバーのブランド）を2行化:
                        // 1行目=社内知恵袋、2行目=小さめの powered by Open WebUI（帰属表示）
                        "function brandSubtitle(){" +
                        "var as=document.querySelectorAll('a[href=\"/\"],a[href=\"'+location.origin+'/\"]');" +
                        "for(var i=0;i<as.length;i++){var a=as[i];var t=(a.textContent||'').trim();" +
                        "if(t==='社内知恵袋'&&!a.getAttribute('data-shineos-sub')){" +
                        "a.setAttribute('data-shineos-sub','1');" +
                        "a.innerHTML='社内知恵袋<br><span style=\"font-size:10px;font-weight:400;opacity:.55;letter-spacing:.02em\">powered by Open WebUI</span>';" +
                        "break;}}}" +
                        "function closeDlg(){" +
                        "var bs=document.querySelectorAll('button');for(var i=0;i<bs.length;i++){var b=bs[i];var t=(b.textContent||'').trim();var a=b.getAttribute('aria-label')||'';" +
                        "if(t==='Got it'||t==='OK'||t==='Close'||t==='閉じる'||t==='了解'||t==='OK、始めましょう！'||t==='Okay, Let\\'s Go!'||a==='Close'){b.click();}}" +
                        "var ms=document.querySelectorAll('[role=\"dialog\"][class*=\"modal\"], [role=\"dialog\"][class*=\"Modal\"]');for(var j=0;j<ms.length;j++){var m=ms[j];if(m&&m.parentNode&&((m.textContent||'').indexOf('What')>-1||(m.textContent||'').indexOf('新機能')>-1)){var bs2=m.querySelectorAll('button');for(var k=0;k<bs2.length;k++){bs2[k].click();break;}}}" +
                        "}" +
                        "brandSubtitle();closeDlg();" +
                        "var ob=new MutationObserver(function(){brandSubtitle();closeDlg();});ob.observe(document.body,{childList:true,subtree:true});setTimeout(function(){ob.disconnect();},60000);" +
                        "})();");
                }
                catch { }
            };
            webView.Source = new Uri(AppUrl);
        }

        void StopService()
        {
            if (closing) return;
            closing = true;
            try { ScStop(); } catch { }
            // アプリ終了時に Ollama のロード済みモデルをアンロードしてメモリを解放する
            // （Open WebUI は停止するが Ollama は常駐のため、モデルだけ明示的に解放する）
            try
            {
                var listReq = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:11434/api/ps");
                listReq.Timeout = 3000;
                using (var listResp = (HttpWebResponse)listReq.GetResponse())
                using (var reader = new StreamReader(listResp.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    foreach (System.Text.RegularExpressions.Match m in
                        System.Text.RegularExpressions.Regex.Matches(json, "\"name\"\\s*:\\s*\"([^\"]+)\""))
                    {
                        try
                        {
                            string model = m.Groups[1].Value;
                            string body = "{\"model\":\"" + model + "\",\"keep_alive\":0}";
                            var unload = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:11434/api/generate");
                            unload.Method = "POST";
                            unload.ContentType = "application/json";
                            unload.Timeout = 5000;
                            byte[] data = System.Text.Encoding.UTF8.GetBytes(body);
                            unload.ContentLength = data.Length;
                            using (var stream = unload.GetRequestStream()) stream.Write(data, 0, data.Length);
                            using (var resp = (HttpWebResponse)unload.GetResponse()) { }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MainWindow());
        }
    }
}
