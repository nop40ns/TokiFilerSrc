
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using ExplorerKit.MVVM.Messages;
using ExplorerKit.MVVM.ViewModels;

using System;
using System.Diagnostics;

using TokiFiler.Behavior;
using TokiFiler.ViewModel;

namespace TokiFiler.Avalonia.UserControls;

/// <summary>
/// FileView.xaml の相互作用ロジック
/// </summary>
public partial class FileView : UserControl
{

    public void RefreshList()
    {
        //var view = CollectionViewSource.GetDefaultView(LV.ItemsSource);
        //view.Refresh();


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
 var vm =this.DataContext;


        });
        WeakReferenceMessenger.Default.Register<FileView, RefreshView>(this, async (r, m) =>
        {
            if (DataContext is FileViewModel vm)
            {
                var exp = (ExplorerViewModel)m.vm;

                if (exp ==null || vm.Explorer == exp)
                {

                        await vm.Explorer.Refresh();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            } 


        });
 



    }

    
    
     
    private void OnItemKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        FileViewItemBehavior.OnKeyDown(sender, e);
    }

   
}
