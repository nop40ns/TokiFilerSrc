using CommunityToolkit.Mvvm.Messaging;
using ExplorerKit.MVVM.Messages;
using ExplorerKit.MVVM.ViewModels;
using ExplorerKit.MVVM.WPF.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace TokiFiler.UserControls.Templates;

public partial class FileViewTemplate
{　
    public FileViewTemplate()
    {
        InitializeComponent();
         




    }

    　 

    private void EditTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            if (e.Key == Key.Enter)
            {
                // フォーカスを外して LostFocus イベントを誘発させる
                BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty)?.UpdateSource();
                if (textBox.DataContext is FileItemViewModel vm)
                {
                    //  vm.IsEditing = false;
                    editBox = textBox;

                    WeakReferenceMessenger.Default.Send(new CloseEditMessage());
                }
               
            }
            else if (e.Key == Key.Escape)
            {
                // 編集キャンセル（バインドを更新せずに閉じる）
                if (textBox.DataContext is FileItemViewModel vm)
                {
                    vm.Model.IsEditing = false;

                }
            }
        }
    }


    private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        T parent = parentObject as T;
        if (parent != null) return parent;
        return FindVisualParent<T>(parentObject);
    }
    TextBox editBox;

    private void EditTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var editBox = (TextBox)sender;
        var itemVM = editBox.DataContext as WpfFileItemViewModel;

        if (editBox.IsVisible && itemVM?.Model.IsEditing == true)
        {
            editBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                editBox.Focus();
                Keyboard.Focus(editBox);

                // 拡張子を除いて選択
                int dotIndex = editBox.Text.LastIndexOf('.');
                if (dotIndex > 0) editBox.Select(0, dotIndex);
                else editBox.SelectAll();
            }), DispatcherPriority.ApplicationIdle);
        }
    }
    private void EditTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {

        // 1. EnterやTabなどの制御文字は判定から除外する
        if (e.Text.Any(c => char.IsControl(c)))
        {
            return;
        }

        // 入力された文字が禁止文字（\ / : * ? " < > |）に含まれているかチェック
        char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();

        // e.Text は入力しようとしている文字。それが禁止文字なら Handled = true でイベントを殺す
        if (e.Text.Any(c => invalidChars.Contains(c)))
        {
            e.Handled = true;

            // オプション：エクスプローラー風にツールチップや警告を出すならここ
            ShowValidationTooltip((TextBox)sender, "ファイル名には次の文字は使えません: \n \\ / : * ? \" < > |");
        }

    }
    　　

 


    private void ShowValidationTooltip(TextBox textBox, string message)
    {
        // 1. 自動表示機能をオフにする（これでマウスホバーで勝手に出なくなる）
        ToolTipService.SetIsEnabled(textBox, false);

        // 2. ツールチップを作成・表示
        var tt = new ToolTip
        {
            Content = message,
            IsOpen = true,
            PlacementTarget = textBox,      // TextBoxを基準にする
            Placement = PlacementMode.Bottom, // 下側に表示
            HorizontalOffset = 0,           // 必要に応じて横位置を微調整
            VerticalOffset = 2              // TextBoxとの隙間を少し開ける};
        };
        textBox.ToolTip = tt;

        // 3. タイマーで閉じる処理
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += (s, e) =>
        {
            tt.IsOpen = false;
            textBox.ToolTip = null; // 一旦クリア

            // 4. 自動表示機能をオンに戻す（必要なら）
            ToolTipService.SetIsEnabled(textBox, true);

            timer.Stop();
        };
        timer.Start();
    }

    private void EditTextBox_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        var et = (TextBox)sender;

        DataObject.AddPastingHandler(et, OnPaste);

    }
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var vm = ((TextBox)sender).DataContext as FileItemViewModel;
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            string text = (string)e.DataObject.GetData(typeof(string));



            if (!IsValidFileName(text))
            { // オプション：エクスプローラー風にツールチップや警告を出すならここ
                ShowValidationTooltip((TextBox)sender, "ファイル名には次の文字は使えません: \n \\ / : * ? \" < > |");

                e.CancelCommand(); // 貼り付け自体をキャンセル
                                   // あるいは、禁止文字を除去した文字列を自分で挿入する
            }
        }
    }


    public bool IsValidFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        // 禁止文字が含まれているかチェック
        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (fileName.Any(c => invalidChars.Contains(c)))
        {
            return false;
        }

        // Windows特有の予約名（CON, PRN, AUXなど）をチェックする場合
        // (これらもファイル名にできません)
        string[] reservedNames = { "CON", "PRN", "AUX", "NUL", "COM1", "LPT1" }; // 一部抜粋
        string nameOnly = Path.GetFileNameWithoutExtension(fileName).ToUpper();
        if (reservedNames.Contains(nameOnly))
        {
            return false;
        }

        return true;
    }
}
