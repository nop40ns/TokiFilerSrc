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
    int QueryContinueDrag(long fEscapePressed, long grfKeyState);

    [PreserveSig]
    int GiveFeedback(long dwEffects);
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
    private static extern int SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr apidl, IntPtr pFormatEtc, ref Guid riid, out IntPtr ppv);

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
            IntPtr pArrayUnmanaged = IntPtr.Zero;

            try
            {
                string firstPath = paths[0];
                string? parentDir = Path.GetDirectoryName(firstPath);


                //if (string.IsNullOrEmpty(parentDir)) return Task.CompletedTask;

                parentPidl = ILCreateFromPath(parentDir);
                //if (parentPidl == IntPtr.Zero) return Task.CompletedTask;

                foreach (var path in paths)
                {
                    IntPtr fullPidl = ILCreateFromPath(path);

                    if (fullPidl != IntPtr.Zero)
                    {
                        // 1. フルパスPIDLから、末尾のファイル名部分のポインタを特定する
                        IntPtr lastIdPointer = ILFindLastID(fullPidl);

                        // 2. 💡【重要】特定した末尾部分「だけ」をクローンしてリストに詰める
                        if (lastIdPointer != IntPtr.Zero)
                        {
                            relativePidlList.Add(ILClone(lastIdPointer));
                        }

                        // 3. 用が済んだフルパスの元メモリはすぐに解放する
                        ILFree(fullPidl);
                    }
                }
                if (relativePidlList.Count == 0) return Task.CompletedTask;


                int count = relativePidlList.Count;
                int pointerSize = Marshal.SizeOf(typeof(IntPtr));
                pArrayUnmanaged = Marshal.AllocHGlobal(pointerSize * count);

                for (int i = 0; i < count; i++)
                    Marshal.WriteIntPtr(pArrayUnmanaged, i * pointerSize, relativePidlList[i]);


                Guid riid = IID_IDataObject;
                int hrData = SHCreateDataObject(parentPidl, (uint)relativePidlList.Count, pArrayUnmanaged, IntPtr.Zero, ref riid, out pDataObject);

                if (hrData >= 0 && pDataObject != IntPtr.Zero)
                {
                    object comObj = Marshal.GetObjectForIUnknown(pDataObject);
                    var dataObject = (System.Runtime.InteropServices.ComTypes.IDataObject)comObj;

                    // 💡 1. 【最重要】エクスプローラーが100%認識できるファイルパス（CF_HDROP）データを構築して注入
                    IntPtr hGlobal = CreateHDropMedium(paths);
                    if (hGlobal != IntPtr.Zero)
                    {
                        var formatetc = new System.Runtime.InteropServices.ComTypes.FORMATETC
                        {
                            cfFormat = 15, // CF_HDROP (ファイルドロップ形式の定数)
                            ptd = IntPtr.Zero,
                            dwAspect = (System.Runtime.InteropServices.ComTypes.DVASPECT)0,
                            lindex = -1,
                            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                        };

                        var stgmedium = new System.Runtime.InteropServices.ComTypes.STGMEDIUM
                        {
                            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
                            pUnkForRelease = IntPtr.Zero,
                            unionmember = hGlobal,
                        };

                        try
                        {
                            // SHCreateDataObjectで作ったオブジェクトにファイルパスデータをドッキング
                            dataObject.SetData(ref formatetc, ref stgmedium, false);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[SetData Exception] {ex.Message}");
                            Marshal.FreeHGlobal(hGlobal);
                        }
                    }

                    var dropSource = new Win32DropSource();
                    pDropSource = Marshal.GetComInterfaceForObject(dropSource, typeof(IDropSource));

                    if (pDropSource != IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine("🔑 [Win32 Native] 同期コンテキストで DoDragDrop を開始します");

                        int finalEffect = DROPEFFECT_COPY;

                        // 同期実行 (データが完全に揃ったためエクスプローラーが即座に反応します)
                        int result = DoDragDrop(pDataObject, pDropSource, DROPEFFECT_COPY, ref finalEffect);

                        System.Diagnostics.Debug.WriteLine($"✅ [Win32 Native] ループから生還！ 結果: HRESULT=0x{result:X8}, Effect={finalEffect}");
                    }
                }



            }
            finally
            {
                if (pArrayUnmanaged != IntPtr.Zero) Marshal.FreeHGlobal(pArrayUnmanaged);
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



    private static IntPtr CreateHDropMedium(string[] files)
    {
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, System.Text.Encoding.Unicode))
        {
            // 1. DROPFILES 構造体 (20バイト) の書き込み
            writer.Write(20);         // pFilesOffset = 20 (構造体の直後から文字列が始まる)
            writer.Write(0);          // pt.X = 0
            writer.Write(0);          // pt.Y = 0
            writer.Write(0);          // fNC = False
            writer.Write(1);          // fWide = True (Unicode文字列であることを明示)

            // 2. パス文字列をUnicode(2バイト)で連続書き込み
            foreach (var file in files)
            {
                // 文字列をそのまま書き込み (BinaryWriter.Write(string)は長さプレフィックスが付くのでNG。チャット形式で直接バイトを流す)
                byte[] bytes = System.Text.Encoding.Unicode.GetBytes(file);
                writer.Write(bytes);
                writer.Write((ushort)0); // 各ファイルの末尾にヌル終端 (\0)
            }

            // 3. 【超重要】配列全体の最終ヌル終端を追加して、完全な「2重ヌル (\0\0)」を構築する
            writer.Write((ushort)0);

            // 4. アンマネージドメモリ（hGlobal）を確保して丸ごとコピー
            byte[] buffer = ms.ToArray();
            IntPtr hGlobal = Marshal.AllocHGlobal(buffer.Length);
            if (hGlobal != IntPtr.Zero)
            {
                Marshal.Copy(buffer, 0, hGlobal, buffer.Length);
            }
            return hGlobal;
        }
    }

    private static IntPtr _CreateHDropMedium(string[] files)
    {
        // DROPFILES構造体(20バイト) + パス文字列(Unicode) + 終端ヌル文字のメモリサイズを計算
        int charSize = 2; // Unicode (1文字2バイト)
        int totalCharCount = 0;
        foreach (var file in files)
        {
            totalCharCount += file.Length + 1; // 各パスの長さ + ヌル終端
        }
        totalCharCount += 1; // 配列全体の最終ヌル終端

        int dropFilesSize = 20; // sizeof(DROPFILES)
        int totalSize = dropFilesSize + (totalCharCount * charSize);

        IntPtr hGlobal = Marshal.AllocHGlobal(totalSize);
        if (hGlobal == IntPtr.Zero) return IntPtr.Zero;

        // メモリの初期化 (すべてゼロクリア)
        byte[] zeroBuffer = new byte[totalSize];
        Marshal.Copy(zeroBuffer, 0, hGlobal, totalSize);

        // 1. DROPFILES構造体のデータを書き込み
        Marshal.WriteInt32(hGlobal, 0, dropFilesSize); // pFilesOffset = 20
        Marshal.WriteInt32(hGlobal, 16, 1);            // fWide = True (Unicode)

        // 2. パス文字列を連続して書き込み
        IntPtr pCurrent = IntPtr.Add(hGlobal, dropFilesSize);
        foreach (var file in files)
        {
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(file);
            Marshal.Copy(bytes, 0, pCurrent, bytes.Length);
            pCurrent = IntPtr.Add(pCurrent, bytes.Length + charSize); // 文字列 + ヌル文字分進める
        }

        return hGlobal;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class Win32DropSource : IDropSource
    {

        private struct POINT
        {
            int x;
            int y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetCursorPos(out POINT lppoint);


        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool SetSystemCursor(IntPtr hcur, uint id);


        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SetCursor(IntPtr lpCursorHandle);

        private static readonly IntPtr IDC_ARROW = (IntPtr)32512;
        private static readonly IntPtr IDC_CROSS = (IntPtr)32515; // 代替マークなど
        private static readonly IntPtr IDC_NO = (IntPtr)32648;



        public int QueryContinueDrag(long fEscapePressed, long grfKeyState)
        {
            System.Diagnostics.Debug.WriteLine($"[QueryContinueDrag] fEscapePressed = {fEscapePressed}");
            System.Diagnostics.Debug.WriteLine($"[QueryContinueDrag] grfKeyState = {grfKeyState}");

            if (fEscapePressed ==1) return DRAGDROP_S_CANCEL;
            //if (grfKeyState == 0) return 0;

            // 左ボタン(1) と 右ボタン(2) が完全に指から離れたらドロップを完了させる
            if ((grfKeyState & (0x0001 | 0x0002)) == 0)
            {
                return DRAGDROP_S_DROP;
            }

            return 0; // S_OK (ドラッグ継続)
        }

        public int GiveFeedback(long dwEffects)
        {
            System.Diagnostics.Debug.WriteLine($"[GiveFeedback] dwEffects = {dwEffects}");

            if ((dwEffects & 1) != 0) // DROPEFFECT_COPY (1) の場合
            {
                // エクスプローラーがコピーを受け入れ可能なら、通常の矢印（またはプラス付き）を強制セット
                SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
            }
            else
            {
                SetCursor(LoadCursor(IntPtr.Zero, IDC_NO));

                // ドロップできない場所（0）なら、あえて何もしない（または禁止マークをセット）
            }
            return 0;
        }

    }
}
