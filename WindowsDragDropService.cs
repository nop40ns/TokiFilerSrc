using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ExplorerKit.Core.Interfaces;

namespace ExplorerKit.Native.Windows.Services;

[ComImport]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComVisible(true)]
public interface IDropSource
{
    [PreserveSig]
    int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);

    [PreserveSig]
    int GiveFeedback(uint dwEffects);
}

public class WindowsDragDropService : INativeDragDropService
{
    // 💡 1. 第一・第二引数を安全な IntPtr (生のポインタ) に統一し、メモリ破壊を完璧に防ぎます
    [DllImport("ole32.dll", PreserveSig = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int DoDragDrop(IntPtr pDataObj, IntPtr pDropSource, int dwEffects, ref int pdwEffect);

    [DllImport("ole32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    // 💡 2. Windows公式の完璧な IDataObject (IntPtr) を製造するシェルAPI
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, IntPtr pFormatEtc, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern IntPtr ILCreateFromPath([MarshalAs(UnmanagedType.LPWStr)] string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILClone(IntPtr pidl);

    private const int DROPEFFECT_COPY = 1;
    private const int DROPEFFECT_MOVE = 2;
    private const int DRAGDROP_S_DROP = 0x00040001;
    private const int DRAGDROP_S_CANCEL = 0x00040002;

    private static Guid IID_IDataObject = new Guid("0000010e-0000-0000-C000-000000000046");

    // 💡 WindowsDragDropService 内の ExecuteDragDropAsync を以下に丸ごと差し替えます
    public Task ExecuteDragDropAsync(IEnumerable<string> filePaths)
    {
        var paths = filePaths?.ToArray();
        if (paths == null || paths.Length == 0) return Task.CompletedTask;

        // 💡 Dispatcher.UIThread.Post を完全に撤廃します！
        // 呼び出し元の PointerMoved (UIスレッド) の上で、今まさに掴んでいるマウスの状態をそのまま利用して
        // 同期的に直接 DoDragDrop の Win32 ループへ突入させます。
        try
        {
            OleInitialize(IntPtr.Zero);

            IntPtr pDataObject = IntPtr.Zero;
            IntPtr pDropSource = IntPtr.Zero;
            IntPtr parentPidl = IntPtr.Zero;
            var relativePidlList = new List<IntPtr>();

            try
            {
                string firstPath = paths[0];
                string? parentDir = Path.GetDirectoryName(firstPath);
                if (string.IsNullOrEmpty(parentDir)) return Task.CompletedTask;

                parentPidl = ILCreateFromPath(parentDir);
                if (parentPidl == IntPtr.Zero) return Task.CompletedTask;

                foreach (var path in paths)
                {
                    IntPtr fullPidl = ILCreateFromPath(path);
                    if (fullPidl != IntPtr.Zero)
                    {
                        IntPtr childPidl = ILFindLastID(fullPidl);
                        if (childPidl != IntPtr.Zero)
                        {
                            relativePidlList.Add(ILClone(childPidl));
                        }
                        ILFree(fullPidl);
                    }
                }

                if (relativePidlList.Count == 0) return Task.CompletedTask;

                Guid riid = IID_IDataObject;
                int hrData = SHCreateDataObject(parentPidl, (uint)relativePidlList.Count, relativePidlList.ToArray(), IntPtr.Zero, ref riid, out pDataObject);

                if (hrData >= 0 && pDataObject != IntPtr.Zero)
                {
                    var dropSource = new Win32DropSource();
                    pDropSource = Marshal.GetComInterfaceForObject(dropSource, typeof(IDropSource));

                    if (pDropSource != IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine("🔑 [Win32 Native] 同期コンテキストで DoDragDrop を開始します");

                        int finalEffect = DROPEFFECT_COPY | DROPEFFECT_MOVE;

                        // 💡 同期実行されるため、マウスの状態が1ミリ秒も途切れることなくOSへ引き継がれます！
                        int result = DoDragDrop(pDataObject, pDropSource, DROPEFFECT_COPY , ref finalEffect);

                        System.Diagnostics.Debug.WriteLine($"✅ [Win32 Native] ループから生還！ 結果: HRESULT=0x{result:X8}, Effect={finalEffect}");
                    }
                }
            }
            finally
            {
                if (pDataObject != IntPtr.Zero) Marshal.Release(pDataObject);
                if (pDropSource != IntPtr.Zero) Marshal.Release(pDropSource);
                if (parentPidl != IntPtr.Zero) ILFree(parentPidl);
                foreach (var pidl in relativePidlList) ILFree(pidl);

                OleUninitialize();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ 例外発生: {ex.Message}");
        }

        // 💡 ドラッグループから戻ってきたら、完了したことを await 側に通知する
        return Task.CompletedTask;
    }


    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class Win32DropSource : IDropSource
    {
        public int QueryContinueDrag(bool fEscapePressed, uint grfKeyState)
        {
            if (fEscapePressed) return DRAGDROP_S_CANCEL;

            // 左ボタン(1) と 右ボタン(2) が完全に指から離れたらドロップを完了させる
            if ((grfKeyState & (0x0001 | 0x0002)) == 0)
            {
                return DRAGDROP_S_DROP;
            }

            return 0; // S_OK (ドラッグ継続)
        }

        public int GiveFeedback(uint dwEffects) => 0x00040002; // DRAGDROP_S_USEDEFAULTCURSORS
    }
}
