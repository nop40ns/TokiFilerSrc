using ABI.System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using ExplorerKit.Core.Common;
using ExplorerKit.Core.Interfaces;
using ExplorerKit.MVVM.Avalonia.Messages;
using ExplorerKit.MVVM.Avalonia.ViewModels;
using ExplorerKit.MVVM.Interfaces;
using ExplorerKit.MVVM.Messages;
using ExplorerKit.MVVM.ViewModels;
using ExplorerKit.Native.Windows.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using TokiFiler.Avalonia.ViewModels;
using Windows.Storage;
using static Microsoft.WindowsAPICodePack.Shell.PropertySystem.SystemProperties.System;
using Dispatcher = global::Avalonia.Threading.Dispatcher;

namespace TokiFiler.Avalonia.UserControls;

/// <summary>
/// FileView.xaml の相互作用ロジック
/// </summary>
public partial class FileView : UserControl
{
    //private readonly INativeDragDropService _dragDropService = new WindowsDragDropService();

    public void RefreshList()
    {
        //var view = CollectionViewSource.GetDefaultView(LV.ItemsSource);
        //view.Refresh();


    }

    static FileView()
    {
        // ダブルクリック (DoubleTappedEvent) を LBI_DoubleTapped に紐付ける
        InputElement.DoubleTappedEvent.AddClassHandler<ListBoxItem>((item, args) =>
        {
            // 💡 自分の画面(FileView)の中にある ListBoxItem かどうかを判定してルーティング
            var fileView = item.FindAncestorOfType<FileView>();
            if (fileView != null) fileView.LBI_DoubleTapped(item, args);
        });



        // マウスダウン (PointerPressedEvent) を LBI_PointerPressed に紐付ける
        // ※第3引数に RoutingStrategies.Tunnel を指定すると、内側の要素に邪魔されず確実にPressedを奪えます
        InputElement.PointerPressedEvent.AddClassHandler<ListBoxItem>((item, args) =>
        {
            var fileView = item.FindAncestorOfType<FileView>();
            if (fileView != null) fileView.LBI_PointerPressed(item, args);
        }, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);

        InputElement.KeyDownEvent.AddClassHandler<TextBox>((textBox, args) =>
        {
            // リネーム用のTextBox（ClassesにRenameTextBoxStyleを持っているもの）だけを対象にする
            if (textBox.Classes.Contains("RenameTextBoxStyle"))
            {
                var fileView = textBox.FindAncestorOfType<FileView>();
                if (fileView != null) fileView.RenameTextBox_KeyDown(textBox, args);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // 💡 画面内のすべての TextBox の LostFocus イベントをキャッチする
        InputElement.LostFocusEvent.AddClassHandler<TextBox>((textBox, args) =>
        {
            if (textBox.Classes.Contains("RenameTextBoxStyle"))
            {
                var fileView = textBox.FindAncestorOfType<FileView>();
                if (fileView != null) fileView.RenameTextBox_LostFocus(textBox, args);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);


        InputElement.PointerPressedEvent.AddClassHandler<TextBlock>((textBlock, args) =>
        {
            // 名前が「RenameTextBlock」であるものだけをピンポイントで狙い撃ちします
            if (textBlock.Name == "RenameTextBlock")
            {
                var fileView = textBlock.FindAncestorOfType<FileView>();
                if (fileView != null) fileView.RenameTextBlock_PointerPressed(textBlock, args);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);



    }

    public FileView()
    {
        InitializeComponent();

        Debug.WriteLine($"★[C#] FileViewコンストラクタ通過: {this.GetHashCode()}:{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}");


        // メッセージの購読

        this.Loaded += (s, e) =>
        {
            Debug.WriteLine($"★[C#] FileView_Loaded開始: {this.GetHashCode()}:{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}");

            // 既存のフォルダ読み込み処理など...

            Debug.WriteLine($"★[C#] FileView_Loaded終了: {this.GetHashCode()}:{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}");

        };



        WeakReferenceMessenger.Default.Register<FileView, FilePropertyMessage>(this, (r, m) =>
        {
            var vm = this.DataContext;


        });
        WeakReferenceMessenger.Default.Register<FileView, RefreshView>(this, async (r, m) =>
        {
            if (DataContext is FileViewModelAvalonia vm)
            {
                var exp = (ExplorerViewModel)m.vm;

                if (exp == null || vm.Explorer == exp)
                {

                    await vm.Explorer.Refresh();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }


        });




    }

    private void AddressBar_SearchStart(object sender, RoutedEventArgs e)
    {
        var vm = this.DataContext as FileViewModelAvalonia; // または Explorer を持つ親
        vm?.Explorer?.CommandExec.FileFind.DoExecute(vm.Explorer);
    }



    private void OnItemKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {

        throw (new NotImplementedException("OnItemKeyDown"));
        //        FileViewItemBehavior.OnKeyDown(sender, e);
    }


    private bool IsAlt = false;
    private bool IsShift = false;
    private bool IsCtrl = false;

    private void MainGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Route != RoutingStrategies.Tunnel) return;

        if (AddressBar.IsEditMode == true) return;

        if (AddressBar.IsSearchMode == true) return;


        if (TextBoxFilter.IsFocused == true)
        {
            return; // フィルターボックスにフォーカスがあるときは、ショートカットを処理しない

        }



        var vm = DataContext as FileViewModelAvalonia;
        if (vm.Explorer.FileList.IsEditing) return;

        IsCtrl = false;
        IsShift = false;
        IsAlt = false;

        // 1. キーコンビネーションを文字列に変換
        string keyCombo = "";
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            keyCombo += "Ctrl+";
            IsCtrl = true;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            keyCombo += "Shift+";
            IsShift = true;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            keyCombo += "Alt+";
            IsAlt = true;
        }

        // 文字列として連結（例: "Ctrl+C"）
        keyCombo += e.Key.ToString();

        Debug.WriteLine(@$"Key:{keyCombo}");

        // 2. サービスを実行

        var exolorerVm = vm?.Explorer; // ExplorerViewModel を取得

        if (vm != null && exolorerVm != null
            && exolorerVm.CommandExec.ExecuteByKey(keyCombo, exolorerVm))
        {
            e.Handled = true; // コマンドが見つかって実行されたらイベントを完了
        }
    }


    private void MainGrid_PreviewTextInput(object sender, TextInputEventArgs e)
    {


        if (TextBoxFilter.IsFocused == true)
        {
            return; // フィルターボックスにフォーカスがあるときは、ショートカットを処理しない

        }

        if (AddressBar.IsEditMode == true) return;

        if (AddressBar.IsSearchMode == true) return;
        // 2. サービスを実行
        var vm = this.DataContext as FileViewModelAvalonia; // または Explorer を持つ親



        if (vm.Explorer.FileList.IsEditing == true)
        {
            return;
        }

        var exolorerVm = vm?.Explorer; // ExplorerViewModel を取得

        string keyCombo = "";
        if (IsCtrl)
        {
            keyCombo += "Ctrl+";
        }
        if (IsShift)
        {
            keyCombo += "Shift+";
        }
        if (IsAlt)
        {
            keyCombo += "Alt+";
        }


        // 文字列として連結（例: "Ctrl+C"）
        keyCombo += e.Text.ToString();

        if (vm != null && exolorerVm != null
            && exolorerVm.CommandExec.ExecuteByKey(e.Text, exolorerVm))
        {
            e.Handled = true; // コマンドが見つかって実行されたらイベントを完了
        }
    }

    private void MainGrid_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.Route != RoutingStrategies.Tunnel) return;

        throw new InvalidOperationException("MainGrid_PointerPressed");

        //    var vm = DataContext as FileViewModel;
        //if (vm.Explorer.FileList.IsEditing) return;



        //*******************************
        //!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
        //*******************************

        //        FileViewItemBehavior.OnGridClick(sender, e);
    }


    private void LV_GotFocus(object sender, RoutedEventArgs e)
    {
        //var window = Window.GetWindow(this);
        //if (window != null)
        //{
        //    var handle = new WindowInteropHelper(window).Handle;
        //    ImeControlHelper.SetImeDisabled(handle);
        //}
    }


    private void LBI_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        // 💡 WPF の ListBoxItem に相当
        if (sender is not ListBoxItem item) return;

        // 💡 ご自身の定義されている Avalonia 用の ViewModel 型にキャストしてください
        if (item.DataContext is not AvaloniaFileItemViewModel file) return;

        // 💡 WPF の ItemsControlFromItemContainer は、Avalonia では FindAncestorOfType を使ってスマートに遡れます
        var listBox = item.FindAncestorOfType<ListBox>();

        // 💡 こちらも実際の画面全体の ViewModel 型（FileViewModelAvaloniaなど）に合わせてください
        var vm = listBox?.DataContext as FileViewModelAvalonia;

        // イベントをここで消費し、WPFの時と同様に後ろへの染み出しを防ぎます
        e.Handled = true;

        // 💡 WPF の Dispatcher.BeginInvoke(..., DispatcherPriority.Input) に相当する
        // Avalonia で最も標準的な「UIスレッドでの後回し非同期実行」の書き方です
        Dispatcher.UIThread.Post(() =>
        {
            // ViewModel のファイルオープンコマンドを実行します
            if (vm?.FileOpenCommand != null && vm.FileOpenCommand.CanExecute(file))
            {
                vm.FileOpenCommand.Execute(file);
            }
        }, DispatcherPriority.Input); // 💡 優先順位（Priority）もそのまま引き継げます

    }



    // A. アイテムドラッグ＆ドロップ用
    private bool _isReadyToDrag;
    private Point _dragStartPoint;
    private ListBoxItem? _dragTargetItem;

    // B. 空白ラバーバンド用
    private bool _isDraggingRubberBand;
    private Point _rubberBandStartPoint;
    private readonly List<ListBoxItem> _initialSelectedItems = new();

    private void LBI_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _lastPressedEventArgs = e;

        if (sender is not ListBoxItem lbi || lbi.DataContext is not AvaloniaFileItemViewModel fileItem) return;

        var properties = e.GetCurrentPoint(lbi).Properties;

        if (properties.IsLeftButtonPressed)
        {
            // 💡 解決策1：ダブルクリックのカウントをシステム（OS）に判定させる
            // e.ClickCount が 2 以上のときは、OSがダブルクリックとみなしている瞬間です。
            if (e.ClickCount >= 2)
            {
                System.Diagnostics.Debug.WriteLine($"★ダブルクリック確定: {fileItem.FileName}");
                // ここにフォルダ移動やファイルオープンの処理を書く（DoubleTappedイベント自体不要になります）
                e.Handled = true;
                return;
            }

            // 💡 解決策2：シングルクリック時（ドラッグ開始）の処理
            // 重い画面遷移などは行わず、WPF時代のDoDragDropをそのまま呼び出す
            System.Diagnostics.Debug.WriteLine($"★シングルクリック / ドラッグ準備: {fileItem.FileName}");

            // e.Handled = true; をここで「呼ばない」でおくことで、
            // 2回目のカチ（ダブルクリック）の邪魔をせず、OSにClickCountを正しくカウントさせることができます。

            _isReadyToDrag = true;
            _dragStartPoint = e.GetPosition(this); // ドラッグ開始座標を記録
            _dragTargetItem = lbi;

            // ⚠️ 重要：ここでは e.Handled = true; を絶対に呼ばない！
            // ここでイベントを止めずに後ろへ流すことで、OSが「2回目のカチ（ダブルクリック）」を正しく検知できるようになります。
            return;

        }






        // 💡【重要】ここで e.Handled = true; を呼ぶと、親の ListBox に伝播しないため、
        // 空白部分クリック（ラバーバンド）と完璧に役割を分離できます！
        e.Handled = true;
    }

    private global::Avalonia.Input.PointerPressedEventArgs? _lastPressedEventArgs;

    private void LV_Blank_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _lastPressedEventArgs = e;
        // 💡 完璧な役割分離の仕組み：
        // アイテム（行）の上でクリックされた場合は、上の LBI_PointerPressed 内の return によって
        // イベントがここで消費されずに大元の ListBox（ここ）まで浮上してきます。
        // しかし、アイテム上の Pressed の時は _isReadyToDrag=true 状態になっているため、
        // 「純粋な空白部分をクリックした時だけ」ラバーバンドを起動させるようにガードをかけます。

        if (_isReadyToDrag)
        {
            // アイテムの上でのクリックなので、ラバーバンド処理は完全にスルーして終了します
            return;
        }

        var properties = e.GetCurrentPoint(LV).Properties;
        if (properties.IsRightButtonPressed)
        {
            System.Diagnostics.Debug.WriteLine("★空白部分が右クリックされました：すべての選択を解除します");

            // 💡 ListBox の選択を丸ごと一括クリア
            LV.SelectedItems.Clear();

            // 先ほど作成した SelectionChanged イベント（LV_SelectionChanged）が自動で走り、
            // すべてのチェックボックスのレ点も光の速さで全消灯します。

            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed)
        {
            System.Diagnostics.Debug.WriteLine("★空白部分がクリックされました：ラバーバンド開始");
            _isDraggingRubberBand = true;
            _rubberBandStartPoint = e.GetPosition(LV);

            // ShiftやCtrlキーを考慮するため、開始時の選択アイテムをキープ
            _initialSelectedItems.Clear();
            foreach (var selected in LV.SelectedItems)
            {
                if (LV.ContainerFromItem(selected) is ListBoxItem item)
                {
                    _initialSelectedItems.Add(item);
                }
            }

            // ウィンドウの外までマウスを引っ張っても追従するようにキャプチャccccc
            e.Pointer.Capture(LV);
            e.Handled = true;
        }
    }

    private bool _isDragging = false;

    // ② マウス移動時（【超重要】アイテムドラッグの確定 ＆ ラバーバンドの矩形更新）
    //   private async Task LV_Blank_PointerMoved(object? sender, PointerEventArgs e)
    private async void LV_Blank_PointerMoved(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {

        var pointerProperties = e.GetCurrentPoint(this).Properties;

        // 💡 2. 左ボタンが押されていない（クリックされていない）場合は、絶対にドラッグを開始しない
        if (!pointerProperties.IsLeftButtonPressed)
        {
            return;
        }

        // -------------------------------------------------------------
        // パターンA：アイテム（ファイル行）のドラッグ＆ドロップ開始判定
        // -------------------------------------------------------------
        if (_isReadyToDrag && _dragTargetItem != null )
        {

            if (_isDragging) return;

            Point currentPoint = e.GetPosition(this);
            double deltaX = Math.Abs(currentPoint.X - _dragStartPoint.X);
            double deltaY = Math.Abs(currentPoint.Y - _dragStartPoint.Y);

            // OS標準のドラッグしきい値（手ブレ以上の4ピクセル移動を検知）
            if (deltaX > 4 || deltaY > 4)
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;
                var vm = LV.DataContext as FileViewModelAvalonia;

                if (vm?.Explorer?.TargetItems != null)
                {
                    _isDragging = true;
                    e.Handled = true;

                    string[] selectedFiles = vm.Explorer.TargetItems
                            .Select(x => x.Model.FullPath.NormalizePathSeparators()).ToArray();

                    var win32InteropObject = new Win32DataObject(selectedFiles);
                    int resultEffect = 0;

                

                    try
                    {
                        // OLEの初期化（既に初期化されている場合は安全にスルーされます）
                        Win32DataObject.OleInitialize(IntPtr.Zero);

                        // 💡 スレッドを分けず、ここで直接呼び出します。
                        // UIスレッド上で動くため、ボタンを離した瞬間に確実にここを通過して戻ってきます。
                        Win32DataObject.DoDragDrop(win32InteropObject, win32InteropObject, 1 | 2 | 4, out resultEffect);

                        System.Diagnostics.Debug.WriteLine($"Win32ネイティブドラッグ終了結果: {resultEffect}");
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ドラッグエラー: {ex.Message}");
                    }
                    finally
                    {
                        // OLE の解放 
                        Win32DataObject.OleUninitialize();
                        _isDragging = false;

                    }

                    //var targets = new List<string> { @"C:\temp\sample1.txt", @"C:\temp\myFolder" };

                    //var legacyFormat = DataFormat.CreateStringPlatformFormat("FileDrop");

                    //var dragData = new DataTransfer();

                    //// 修正前（エラー発生箇所）
                    //// dragItem.Set(legacyFormat, () => selectedFiles);

                    //var dragItem = new DataTransferItem();




                    //var s = string.Join(Environment.NewLine, targets);

                    //// 修正後（string[] → string へ変換してセット）
                    //dragItem.Set(legacyFormat, () => string.Join("\0", selectedFiles) + "\0\0");
                    //dragData.Add(dragItem);


                    //foreach (var item in vm.Explorer.TargetItems)
                    //{

                    //    var path = item.Model.FullPath.NormalizePathSeparators();
                    //    var uri = new System.Uri(path);

                    //    if (item.Model.IsDirectory == true)
                    //    {
                    //        var storageFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(uri);
                    //        if (storageFolder != null)
                    //        {
                    //            // 識別子は DataFormat.File のままで、Folderオブジェクトを渡します
                    //              dragItem = DataTransferItem.Create(DataFormat.File, storageFolder);
                    //            dragData.Add(dragItem);
                    //        }
                    //    }
                    //    else
                    //    {
                    //        var storageFile = await topLevel.StorageProvider.TryGetFileFromPathAsync(uri);

                    //        if (storageFile != null)
                    //        {
                    //            // 各ファイルを DataTransferItem にして Add する
                    //              dragItem = DataTransferItem.Create(DataFormat.File, storageFile);
                    //            dragData.Add(dragItem);
                    //        }
                    //    }
                    //}







                    //var result = await DragDrop.DoDragDropAsync(_lastPressedEventArgs, dragData, DragDropEffects.Copy);

                    // 必要に応じて、結果（コピーされたか、キャンセルされたか）をログ出力
                    // System.Diagnostics.Debug.WriteLine($"ドラッグ終了結果: {result}");
                }

#if false
                // 💡 1. 後続の再入を完全に防ぐため、最優先でフラグを折る
                _isReadyToDrag = false;
                var targetItem = _dragTargetItem;
                _dragTargetItem = null;

                var pressedArgs = _lastPressedEventArgs;
                var vm = LV.DataContext as FileViewModelAvalonia;

                if (vm?.Explorer?.TargetItems != null)
                {
                    string[] selectedFiles = vm.Explorer.TargetItems
                        .Select(x => x.Model.FullPath.NormalizePathSeparators())
                        .ToArray();
                     
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "TokiDragDropper.exe",
                         UseShellExecute = true,       // 💡 [修正]: OSのシェル経由にしてコンソールを有効化
                        CreateNoWindow = false        // 💡 [修正]: 黒い画面を「表示する」
                    };
                    foreach (var path in selectedFiles)
                    {
                        startInfo.ArgumentList.Add(path);
                    }

                    // 🌟 【完全なる疎結合】：
                    // 子プロセスを非同期に「起動するだけ」です。
                    // 起動に要する時間はわずか数ミリ秒（0.01秒以下）であり、起動した瞬間にこの関数は return します。
                    // 呼び出し元の ViewModel は一瞬で解放されるため、自作ファイラー側は 100% 物理的にフリーズしません。
                    Process.Start(startInfo);
                }

#endif
            }
            e.Handled = true;
                return;
        }

        // -------------------------------------------------------------
        // パターンB：純粋な空白部分のラバーバンド選択処理
        // -------------------------------------------------------------
        if (!_isDraggingRubberBand) return;

        Point currentBandPoint = e.GetPosition(LV);

        double x = Math.Min(_rubberBandStartPoint.X, currentBandPoint.X);
        double y = Math.Min(_rubberBandStartPoint.Y, currentBandPoint.Y);
        double width = Math.Abs(_rubberBandStartPoint.X - currentBandPoint.X);
        double height = Math.Abs(_rubberBandStartPoint.Y - currentBandPoint.Y);

        // 画面上の Rectangle を更新して表示
        RubberBandRect.IsVisible = true;
        Canvas.SetLeft(RubberBandRect, x);
        Canvas.SetTop(RubberBandRect, y);
        RubberBandRect.Width = width;
        RubberBandRect.Height = height;

        // 高速交差判定の実行
        Rect rubberBandBounds = new Rect(x, y, width, height);
        UpdateRubberBandSelection(rubberBandBounds, e.KeyModifiers);

        e.Handled = true;
    }


    // ③ マウスボタンが離された時（すべての状態の確定 ＆ クリーンアップ）
    private void LV_Blank_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // アイテムのドラッグ準備状態をリセット
        _isReadyToDrag = false;
        _dragTargetItem = null;

        // ラバーバンド選択中の場合
        if (_isDraggingRubberBand)
        {
            _isDraggingRubberBand = false;
            RubberBandRect.IsVisible = false;
            RubberBandRect.Width = 0;
            RubberBandRect.Height = 0;

            e.Pointer.Capture(null); // キャプチャ解放
            e.Handled = true;
        }
    }

    // =================================================================
    // 🚀 ラバーバンド用：VisualTree仮想化対応の超高速交差判定メソッド
    // =================================================================
    private void UpdateRubberBandSelection(Rect rubberBandBounds, KeyModifiers modifiers)
    {

        // 現在画面に見えている物理コンテナのみを最速ループ
        foreach (var visual in LV.GetRealizedContainers())
        {
            if (visual is not ListBoxItem lbi) continue;

            Rect itemBounds = lbi.Bounds;
            bool isIntersect = rubberBandBounds.Intersects(itemBounds);

            if (isIntersect)
            {
                // 💡 範囲内に入ったアイテムは、過去の選択を維持したまま「加算（True）」します
                lbi.IsSelected = true;
            }
            else
            {
                // 💡 範囲外になった場合：ドラッグ開始前に選ばれていたアイテム(_initialSelectedItems)なら
                // 選択を解除せず、そのまま選択状態（true）を維持し続けます（加算方式のコア）
                if (_initialSelectedItems.Contains(lbi))
                {
                    lbi.IsSelected = true;
                }
            }
        }
    }

    private void LV_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 💡 1. 今回「新しく選択範囲に入った」アイテムのチェックをオンにする
        if (e.AddedItems != null)
        {
            foreach (var item in e.AddedItems)
            {
                if (item is AvaloniaFileItemViewModel fileItem)
                {
                    // 💡 ご自身のViewModelの構造（Model.IsCheckedなど）に合わせて、チェックフラグを True にします
                    if (fileItem.Model != null)
                    {
                        fileItem.Model.IsChecked = true;
                    }
                }
            }
        }

        // 💡 2. 今回「選択範囲から外れた」アイテムのチェックをオフにする
        if (e.RemovedItems != null)
        {
            foreach (var item in e.RemovedItems)
            {
                if (item is AvaloniaFileItemViewModel fileItem)
                {
                    // 💡 範囲外になったらチェックフラグを False にします
                    if (fileItem.Model != null)
                    {
                        fileItem.Model.IsChecked = false;
                    }
                }
            }
        }
    }

    private void StartDragDrop(ListBoxItem item, PointerPressedEventArgs e, string[] selectedFiles)
    {
        if (selectedFiles == null || selectedFiles.Length == 0) return;

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;

        // 1. DataTransfer（コンテナ）の組み立て
        var dragData = new global::Avalonia.Input.DataTransfer();

        // -------------------------------------------------------------
        // 🌟 1個目のデータ系統：【Linar / 自作ファイラー / レガシーアプリ向け】
        // -------------------------------------------------------------
        // 💡 [超重要]: CreateStringPlatformFormat("Files") を使うのをやめます。
        // Windowsの OLE 規格におけるファイル配列（CF_HDROP）の生の数値 ID である「15」を
        // 直接プラットフォームフォーマットとして登録します。
        // これにより、Linarは「いつも通りOSから15番(CF_HDROP)のデータが降ってきた！」と完璧に認識します。
        var cpHdropFormat = global::Avalonia.Input.DataFormat.CreateStringPlatformFormat("15");

        // パスを改行区切りの1本の文字列にまとめ、型を完全に一致させてアイテムを生成します。
        // WindowsのAvalonia内部ハンドラが、この「15」という名前と文字列を検知して、
        // Linarが最も愛する生の CF_HDROP（バイナリ構造）へ背後で全自動で変換してくれます。
        var selectedFilesString = string.Join(global::System.Environment.NewLine, selectedFiles);
        var legacyItem = global::Avalonia.Input.DataTransferItem.Create(cpHdropFormat, selectedFilesString);
        dragData.Add(legacyItem);

        // -------------------------------------------------------------
        // 🌟 2個目のデータ系統：【エクスプローラー / モダンOS向け】
        // -------------------------------------------------------------
        // 同時に IStorageItem を同期的に生成して同梱します
        var baseAssembly = typeof(global::Avalonia.Platform.Storage.IStorageItem).Assembly;
        var bclFileType = baseAssembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile");
        var bclFolderType = baseAssembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFolder");

        if (bclFileType != null && bclFolderType != null)
        {
            foreach (var path in selectedFiles)
            {
                object? storageItemInstance = null;

                if (Directory.Exists(path))
                {
                    var dirInfo = new System.IO.DirectoryInfo(path);
                    storageItemInstance = Activator.CreateInstance(bclFolderType, dirInfo);
                }
                else if (File.Exists(path))
                {
                    var fileInfo = new System.IO.FileInfo(path);
                    storageItemInstance = Activator.CreateInstance(bclFileType, fileInfo);
                }

                if (storageItemInstance is global::Avalonia.Platform.Storage.IStorageItem storageItem)
                {
                    var fileItem = global::Avalonia.Input.DataTransferItem.Create(
                        global::Avalonia.Input.DataFormat.File,
                        storageItem);
                    dragData.Add(fileItem);
                }
            }
        }

        try
        {
            System.Diagnostics.Debug.WriteLine("🚀 [Avalonia] 生のCF_HDROP(15)を隠し持ったハイブリッドデータでドラッグを開始します。");

            // 💡 投げっぱなし（_ = ）でキックすることで、ドロップ先フリーズの巻き添えを100%防ぎます
            _ = global::Avalonia.Input.DragDrop.DoDragDropAsync(
                e,
                dragData,
                global::Avalonia.Input.DragDropEffects.Copy | global::Avalonia.Input.DragDropEffects.Move);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ ドラッグエラー: {ex.Message}");
        }
    }

    //private async Task _StartDragDrop(ListBoxItem item, PointerPressedEventArgs e)
    //{
    //    // 1. 選択されているアイテム（ファイル・フォルダ）の一覧を取得する
    //    var selectedFiles = new List<string>();
    //    foreach (var selected in LV.SelectedItems)
    //    {
    //        if (selected is AvaloniaFileItemViewModel fileItem)
    //        {
    //            string? fullPath = fileItem.FullPath;
    //            if (!string.IsNullOrEmpty(fullPath) && (File.Exists(fullPath) || Directory.Exists(fullPath)))
    //            {
    //                selectedFiles.Add(fullPath);
    //            }
    //        }
    //    }

    //    if (selectedFiles.Count == 0) return;

    //    // 2. DataTransfer の組み立て
    //    var dragData = new global::Avalonia.Input.DataTransfer();
    //    var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
    //    if (storageProvider == null) return;

    //    // 💡 対策：最新の DataFormat.File を使用し、すべての IStorageItem を格納する
    //    foreach (var path in selectedFiles)
    //    {
    //        global::Avalonia.Platform.Storage.IStorageItem? storageItem = Directory.Exists(path)
    //            ? await storageProvider.TryGetFolderFromPathAsync(path)
    //            : await storageProvider.TryGetFileFromPathAsync(path);

    //        if (storageItem != null)
    //        {
    //            var transferItem = global::Avalonia.Input.DataTransferItem.Create(
    //                global::Avalonia.Input.DataFormat.File, // 👈 正しいv11/v12の形式
    //                storageItem);
    //            dragData.Add(transferItem);
    //        }
    //    }

    //    // 💡 対策B (古いアプリへの保険): Windowsのレガシーなファイル配列要求のためのカスタムデータ
    //    // コンパイルエラーを回避しつつ、OSのCF_HDROP構造（文字列配列）として古いアプリに認識させるためのハックです。
    //    // ※もし不要ならこの3行は削っても構いませんが、WPF等の古いアプリで受け取るための文字列キーです。
    //    var legacyFormat = global::Avalonia.Input.DataFormat.CreateStringPlatformFormat("Files");
    //    // 文字列の配列、またはカンマ区切りなどをセットできるか試せます。ただし基本は上記「File」があればOSが変換します。

    //    try
    //    {
    //        // 3. 静的メソッド DoDragDropAsync を呼び出す
    //        // 💡 呼び出し元（PointerMoved内）で必ず Dispatcher.UIThread.Post(() => { ... }) を使用して呼び出してください。
    //        await global::Avalonia.Input.DragDrop.DoDragDropAsync(
    //            e,
    //            dragData,
    //            global::Avalonia.Input.DragDropEffects.Copy | global::Avalonia.Input.DragDropEffects.Move);
    //    }
    //    catch (System.Exception ex)
    //    {
    //        System.Diagnostics.Debug.WriteLine($"ドラッグ＆ドロップ中にエラーが発生しました: {ex.Message}");
    //    }
    //}












    private ListBoxItem? _lastClickedItem;
    private DateTime _lastClickTime = DateTime.MinValue;

    // 💡 名前テキストブロック部分のクリック専用ハンドラー
    private void RenameTextBlock_PointerPressed(TextBlock textBlock, PointerPressedEventArgs e)
    {
        // 💡 ご自身のViewModelの型（AvaloniaFileItemViewModelなど）に合わせてください
        if (textBlock.DataContext is not AvaloniaFileItemViewModel fileItem) return;

        // 💡 親の行（ListBoxItem）を取得する
        var lbi = textBlock.FindAncestorOfType<ListBoxItem>();
        if (lbi == null) return;

        var properties = e.GetCurrentPoint(textBlock).Properties;

        // 左クリックの場合のみ判定
        if (properties.IsLeftButtonPressed)
        {
            // 素早いダブルクリック時はリネームを即座にガードして抜ける
            if (e.ClickCount >= 2)
            {
                ResetRenameState();
                return;
            }

            var now = DateTime.Now;
            var elapsed = (now - _lastClickTime).TotalMilliseconds;

            // 💡 完璧なエクスプローラー条件：
            // 1. 前回クリックした文字と同じ行であること
            // 2. その行（ListBoxItem）がすでに選択状態（青枠）であること
            // 3. ゆっくりとした時間差（500ms〜2.5秒）であること
            if (_lastClickedItem == lbi && lbi.IsSelected && elapsed > 500 && elapsed < 2500)
            {
                ResetRenameState();
                e.Handled = true; // イベントをここで消費

                // 🚀 リネーム開始（前回のメソッドをそのまま呼び出します）
                StartRename(lbi, fileItem);
                return;
            }

            // 次の判定のために、行の参照と時間をキープ
            _lastClickedItem = lbi;
            _lastClickTime = now;
        }
    }
    private void ResetRenameState()
    {
        _lastClickedItem = null;
        _lastClickTime = DateTime.MinValue;
    }
    private void StartRename(ListBoxItem lbi, AvaloniaFileItemViewModel fileItem)
    {
        System.Diagnostics.Debug.WriteLine($"★[確定] ゆっくりダブルクリック検知: {fileItem.FileName} のリネームを開始します");

        // 1. ViewModel の編集フラグを True にする
        // これにより、XAML側の TextBlock が消え、TextBox が一瞬で画面に出現します！
        fileItem.Model.IsEditing = true;

        // 2. 💡 Avalonia特有の注意点：TextBoxが出現した直後の「次の描画フレーム」でフォーカスを当てる
        // わずかに処理を遅延（Post）させないと、まだ画面に生成されきっていないTextBoxを見つけられずフォーカスに失敗します
        Dispatcher.UIThread.Post(() =>
        {
            // ListBoxItem の中から、先ほど仕込んだリネーム用 TextBox を探し出す
            var textBox = lbi.FindDescendantOfType<TextBox>();
            if (textBox != null && textBox.Classes.Contains("RenameTextBoxStyle"))
            {
                textBox.Focus();        // フォーカスを当てる
                textBox.SelectAll();    // 文字列を全選択状態にする（エクスプローラーと同じ親切設計）
            }
        }, DispatcherPriority.Input);
    }






    private void RenameTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (textBox.DataContext is not AvaloniaFileItemViewModel fileItem) return;

        if (e.Key == Key.Enter)
        {
            // Enterキーでリネーム確定
            e.Handled = true;

            // 💡 ViewModel側の名前変更ロジックをここで実行する（WPF時代のロジックがあれば呼び出す）
            // 例: fileItem.RenameConfirmCommand.Execute(textBox.Text);

            // フォーカスを自分（FileView全体やListBox）に戻すことで、
            // 自動的にTextBoxの「LostFocus」が走り、綺麗な見た目（文字のみ）に戻ります
            this.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            // Escapeキーでリネームキャンセル
            e.Handled = true;

            // TextBoxのテキストを元のファイル名に戻す
            textBox.Text = fileItem.FileName;

            // 編集モードフラグを折るだけで、自動的に元のTextBlockのような見た目に戻ります
            fileItem.Model.IsEditing = false;
        }
    }

    // 💡 2. フォーカスが外れた時の処理（エクスプローラー同様、別枠クリックで自動確定させる場合）
    private void RenameTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (textBox.DataContext is not AvaloniaFileItemViewModel fileItem) return;

        // もし現在進行形で編集モード（True）なのであれば、フォーカスアウト時に自動確定させる
        if (fileItem.Model.IsEditing)
        {
            // 💡 ここでもEnter時と同様にViewModelの確定ロジックを走らせるのが安全です
            fileItem.Model.IsEditing = false; // 編集モード終了
        }
    }



    private void LV_PreviewMouseDown(object? sender, PointerPressedEventArgs e)
    {
        //// 追加：クリックされた「大元の要素」がScrollBar系かどうかをチェック
        //if (IsOverScrollBar(e.OriginalSource as DependencyObject))
        //{
        //    return; // スクロールバー上なら、範囲選択を開始せず標準動作に任せる
        //}

        //var vm = LV.DataContext as FileViewModelAvalonia;


        //if (vm.Explorer.FileList.IsEditing) return;

        ////if (vm.Explorer.FileList.IsChecked)
        ////{
        ////    return;
        ////}
        //// 1. クリックされた位置の要素を特定する
        //VisualTreeHelper.HitTest(LV, null, (result) =>
        //{


        //    // 2. クリックされたのが ListBoxItem (行) かどうかをチェック
        //    var listBoxItem = FindParent<ListBoxItem>(result.VisualHit);

        //    if (listBoxItem != null)
        //    {
        //        var vm = LV.DataContext as FileViewModelAvalonia;


        //        // アイテムの上ならラバーバンドは開始しない
        //        _isRubberbandActive = false;

        //        // 💡 【重要】アイテムの上なので、D&Dの準備をする
        //        _isDragReady = true;
        //        _dragStartPoint = e.GetPosition(null); // 画面全体に対する座標を記録
        //        _draggedItemData = listBoxItem.DataContext; // ドラッグするファイルのデータ

        //        var item = _draggedItemData as FileItemViewModel;

        //        var model = item.Model;



        //        if (model.IsChecked == true && e.ClickCount == 1)
        //            e.Handled = true;



        //        // アイテム（ファイル）の上をクリックした場合は、標準のドラッグ移動や単体選択に任せる
        //        return HitTestResultBehavior.Stop;
        //    }

        //    if (e.LeftButton == MouseButtonState.Pressed)
        //    {

        //        // 3. アイテム以外（背景）をクリックした場合のみ、自前のドラッグ選択を開始
        //        // 背景クリック時のみラバーバンドを準備
        //        _isDragReady = false; // 背景なのでD&Dは無効
        //        _isRubberbandActive = true;
        //        _startPoint = e.GetPosition(LV); // (既存の変数代入)
        //        StartRubberbandSelection(_startPoint);

        //    }
        //    if (e.RightButton == MouseButtonState.Pressed)
        //    {
        //        var dc = this.DataContext as FileViewModelAvalonia;
        //        dc.Explorer.FileList.SelectedClear();
        //    }

        //    // 【重要】標準の「クリックで選択」イベントをここで止める
        //    e.Handled = true;

        //    return HitTestResultBehavior.Stop;
        //}, new PointHitTestParameters(e.GetPosition(LV)));

    }

    private void LV_PreviewMouseUp(object? sender, PointerPressedEventArgs e)
    {
        //// 💡 D&D用のフラグもここで安全にクリア
        //_isDragReady = false;
        //_draggedItemData = null;

        //_isRubberbandActive = false;
        //if (_adorner != null)
        //{
        //    AdornerLayer.GetAdornerLayer(LV).Remove(_adorner);
        //    _adorner = null;
        //    LV.ReleaseMouseCapture();
        //}
    }


    private void LV_PreviewMouseLeftButtonDown(object? sender, PointerPressedEventArgs e)
    {

    }


    private void LV_PreviewMouseMove(object? sender, PointerPressedEventArgs e)
    {
        //// 💡 対策：アイテム上からのD&D処理を最優先でチェック
        //if (_isDragReady
        //    && (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed))
        //{
        //    Vector diff = _dragStartPoint - e.GetPosition(null);

        //    // OSが定めるドラッグ開始の最小移動距離を超えたか判定（誤作動防止）
        //    if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
        //        Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        //    {
        //        _isDragReady = false; // 連打防止のためにフラグを即落とす

        //        var vm = LV.DataContext as FileViewModelAvalonia;



        //        // 1. 選択されているファイルパスの配列を生成する
        //        // ※ここでは例として、Extended選択されたすべてのファイルパスを取得する想定です
        //        // (ViewModel側のExplorer.SelectedItemsなどからstring[]を取り出すコードに書き換えてください)
        //        string[] selectedFiles = vm?.Explorer?.TargetItems?.Select(x => x.Model.FullPath.NormalizePathSeparators()).ToArray();

        //        if (selectedFiles != null && selectedFiles.Length > 0)
        //        {
        //            // 2. Windows標準のFileDrop形式でデータオブジェクトを作成
        //            var dataObject = new DataObject(DataFormats.FileDrop, selectedFiles, false);

        //            try
        //            {
        //                // 3. 👑 核心：try-catchで完全に保護してD&Dを実行
        //                DragDropEffects result = DragDrop.DoDragDrop(LV, dataObject, DragDropEffects.Copy | DragDropEffects.Move);

        //                // 必要に応じて、Move（切り取り移動）成功時の自画面リフレッシュやファイル削除処理
        //                if (result == DragDropEffects.Move)
        //                {
        //                    // Explorer.Refresh() などを呼ぶ
        //                }
        //            }
        //            catch (Exception ex)
        //            {
        //                // 外部アプリ側がDrop時にクラッシュしても、TokiFilerは安全に耐える
        //                System.Diagnostics.Debug.WriteLine($"外部ドロップ時の通信エラーを回避: {ex.Message}");
        //            }
        //        }
        //        return; // D&Dが開始されたら後続のラバーバンド処理は不要
        //    }
        //}

        //// 背景から開始していない、またはアドナーがない場合は何もしない
        //if (!_isRubberbandActive || _adorner == null) return;

        //var currentPoint = e.GetPosition(LV);
        //_adorner.UpdateSelection(_startPoint, currentPoint);

        //// 当たり判定：四角形に重なっているアイテムを選択する
        //Rect bounds = _adorner.SelectionRect;
        //foreach (var item in LV.Items)
        //{
        //    var container = LV.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        //    if (container == null) continue;

        //    // アイテムの座標を取得
        //    var itemPos = container.TranslatePoint(new Point(0, 0), LV);
        //    Rect itemRect = new Rect(itemPos, new Size(container.ActualWidth, container.ActualHeight));

        //    // 重なっていたら選択 (SelectionMode="Extended" が必要)
        //    if (bounds.IntersectsWith(itemRect) == true)
        //        container.IsSelected = true;

        //}
    }

}

















[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DROPFILES
{
    public int pFiles;
    public int ptX;
    public int ptY;
    public int fNC;
    public int fWide;
}

// 🔑 .NET Coreに不足している IDropSource インターフェースを COM 定義として自作する
[ComImport]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropSource
{
    [PreserveSig]
    int QueryContinueDrag(bool fEscapePressed, int grfKeyState);

    [PreserveSig]
    int GiveFeedback(int dwEffect);
}

// 完全に整合性を合わせた実装クラス
public class Win32DataObject : IDataObject, IDropSource
{
    private const short CF_HDROP = 15;
    private readonly string[] _files;


    [DllImport("ole32.dll", ExactSpelling = true, CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll", ExactSpelling = true, CharSet = CharSet.Auto, SetLastError = true)]
    public static extern void OleUninitialize();




    // Win32 API のインポート（IDropSource は上記で定義したものを利用）
    [DllImport("ole32.dll", ExactSpelling = true, CharSet = CharSet.Auto, SetLastError = true)]



    public static extern int DoDragDrop(IDataObject pDataObj, IDropSource pDropSource, int dwOKEffects, out int pdwEffect);


    [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
    public static extern short GetKeyState(int keyCode);
    public Win32DataObject(string[] files)
    {
        _files = files ?? Array.Empty<string>();
    }

    // --- IDropSource の実装 ---
    public int QueryContinueDrag(bool fEscapePressed, int grfKeyState)
    {
        if (fEscapePressed)
            return 0x00040101; // DRAGDROP_S_CANCEL (Escでキャンセル)

        // 💡 物理的なマウス左ボタン (VK_LBUTTON = 0x01) の状態を直接チェック
        // 戻り値の最上位ビット (0x8000) が立っていない = ボタンが離された
        if ((GetKeyState(0x01) & 0x8000) == 0)
        {
            return 0x00040102; // DRAGDROP_S_DROP (ここで初めてドロップ実行)
        }

        return 0; // S_OK (ドラッグを継続)
    }

    public int GiveFeedback(int dwEffect)
    {
        return 0x00040100; // DRAGDROP_S_USEDEFAULTCURSORS
    }

    // --- IDataObject の実装 ---
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = new STGMEDIUM();
        if (format.cfFormat != CF_HDROP || (format.tymed & TYMED.TYMED_HGLOBAL) == 0)
        {
            Marshal.ThrowExceptionForHR(-2147221404); // DV_E_FORMATETC
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (var file in _files)
        {
            sb.Append(file).Append('\0');
        }
        sb.Append('\0');
        byte[] fileBuffer = System.Text.Encoding.Unicode.GetBytes(sb.ToString());

        int dropFilesSize = Marshal.SizeOf<DROPFILES>();
        int totalSize = dropFilesSize + fileBuffer.Length;

        IntPtr hGlobal = Marshal.AllocHGlobal(totalSize);
        if (hGlobal == IntPtr.Zero) Marshal.ThrowExceptionForHR(-2147024882); // E_OUTOFMEMORY

        DROPFILES df = new DROPFILES { pFiles = dropFilesSize, fWide = 1 };
        Marshal.StructureToPtr(df, hGlobal, false);
        Marshal.Copy(fileBuffer, 0, hGlobal + dropFilesSize, fileBuffer.Length);

        medium.tymed = TYMED.TYMED_HGLOBAL;
        medium.unionmember = hGlobal;
        medium.pUnkForRelease = IntPtr.Zero;
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CF_HDROP && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return 0;
        return -2147221404;
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => Marshal.ThrowExceptionForHR(-2147467263);
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = default; return -2147221400; }
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool fRelease) => Marshal.ThrowExceptionForHR(-2147467263);

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        // 今回はデータ提供（GET）のみ対応
        if (direction == DATADIR.DATADIR_GET)
        {
            FORMATETC hd = new FORMATETC
            {
                cfFormat = CF_HDROP,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL
            };

            return new Win32EnumFormatEtc(new FORMATETC[] { hd });
        }

        Marshal.ThrowExceptionForHR(-2147467263); // E_NOTIMPL
        return null!;
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink pAdvSink, out int pdwConnection)
    {
        pdwConnection = 0;
        return -2147467263;
    }

    public void DUnadvise(int dwConnection) => Marshal.ThrowExceptionForHR(-2147467263);

    // 💡 エラー修正箇所: 引数に Null 許容修飾子 (?) を付与し、さらに「Null を int に変換できない」バグを HRESULT（int）を返す形に変更
    public int EnumDAdvise(out IEnumSTATDATA? ppenumAdvise)
    {
        ppenumAdvise = null;
        return -2147467263; // E_NOTIMPL (int型のHRESULTエラーコードを直接返す)
    }
}
public class Win32EnumFormatEtc : IEnumFORMATETC
{
    private readonly FORMATETC[] _formats;
    private int _currentIndex;

    public Win32EnumFormatEtc(FORMATETC[] formats)
    {
        _formats = formats ?? Array.Empty<FORMATETC>();
        _currentIndex = 0;
    }

    public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
    {
        if (rgelt == null || celt <= 0) return -2147024809; // E_INVALIDARG

        int fetched = 0;
        while (_currentIndex < _formats.Length && fetched < celt)
        {
            rgelt[fetched] = _formats[_currentIndex];
            _currentIndex++;
            fetched++;
        }

        if (pceltFetched != null && pceltFetched.Length > 0)
        {
            pceltFetched[0] = fetched;
        }

        return (fetched == celt) ? 0 : 1; // S_OK : S_FALSE
    }

    public int Skip(int celt)
    {
        _currentIndex += celt;
        if (_currentIndex > _formats.Length)
        {
            _currentIndex = _formats.Length;
            return 1; // S_FALSE
        }
        return 0; // S_OK
    }

    public int Reset()
    {
        _currentIndex = 0;
        return 0; // S_OK
    }

    public void Clone(out IEnumFORMATETC ppenum)
    {
        var clone = new Win32EnumFormatEtc(_formats);
        clone._currentIndex = this._currentIndex;
        ppenum = clone;
    }
}
