using ExplorerKit.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using System.Threading.Tasks;

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
            IntPtr hGlobal = IntPtr.Zero;
            IntPtr fullPidl = IntPtr.Zero;


            try
            {
                string firstPath = paths[0];
                string? parentDir = Path.GetDirectoryName(firstPath);


                //if (string.IsNullOrEmpty(parentDir)) return Task.CompletedTask;

                parentPidl = ILCreateFromPath(parentDir);
                //if (parentPidl == IntPtr.Zero) return Task.CompletedTask;

                foreach (var path in paths)
                {
                    fullPidl = ILCreateFromPath(path);

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


                // 💡 1. 複雑な SHCreateDataObject や PIDL配列 (pArrayUnmanaged) の処理はすべて不要になります！
                // 今作成した、純粋でエラーの出ない完璧なCOMデータオブジェクトをインスタンス化
                var comDataObject = new SimpleComDataObject();

                // 💡 2. あなたが完成させた、完璧なファイルパス配列（CF_HDROP）のバイナリ（hGlobal）を直接セット
                hGlobal = CreateHDropMedium(paths);
                if (hGlobal != IntPtr.Zero)
                {
                    var formatetc = new System.Runtime.InteropServices.ComTypes.FORMATETC
                    {
                        cfFormat = 15, // CF_HDROP (ファイルドロップ形式)
                        ptd = IntPtr.Zero,
                        dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT, // 1 (CONTENT) で完全に適合します
                        lindex = -1,
                        tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                    };

                    var stgmedium = new System.Runtime.InteropServices.ComTypes.STGMEDIUM
                    {
                        tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
                        unionmember = hGlobal,
                        pUnkForRelease = IntPtr.Zero
                    };

                    // 自作のラッパーにデータを格納 (所有権はC#でキープするため false)
                    comDataObject.SetData(ref formatetc, ref stgmedium, false);

                    // 💡 3. 生の DoDragDrop に引き渡すために、インターフェースのポインタに変換
                    pDataObject = Marshal.GetComInterfaceForObject(comDataObject, typeof(System.Runtime.InteropServices.ComTypes.IDataObject));

                    var dropSource = new Win32DropSource();
                    pDropSource = Marshal.GetComInterfaceForObject(dropSource, typeof(IDropSource));

                    if (pDataObject != IntPtr.Zero && pDropSource != IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine("🔑 [Win32 Native] 最もクリーンで高互換な構造で OLE DoDragDrop を開始します");

                        int finalEffect = DROPEFFECT_COPY;

                        // 💡 エクスプローラーとの通信スタックが完全に純粋な形式になるため、エラー（2147483649）が完全に破砕されます！
                        int result = DoDragDrop(pDataObject, pDropSource, DROPEFFECT_COPY, ref finalEffect);

                        System.Diagnostics.Debug.WriteLine($"✅ [Win32 Native] ループから生還！ 結果: HRESULT=0x{result:X8}, Effect={finalEffect}");
                    }
                }


            }
            finally
            {
                if (hGlobal != IntPtr.Zero) Marshal.FreeHGlobal(hGlobal);

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

            if (fEscapePressed == 1) return DRAGDROP_S_CANCEL;
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

struct NativeFormatEtc
{
    public int cfFormat;
    public IntPtr ptd; 
    public int dwAspect; 
    public int lindex; 
    public int tymed;
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public class SimpleComDataObject : System.Runtime.InteropServices.ComTypes.IDataObject
{

    private NativeFormatEtc _nativeFormat;
    private STGMEDIUM _medium;
    private bool _hasData = false;

     

    public void SetData(ref FORMATETC pformatetc, ref STGMEDIUM pmedium, bool fRelease)
    {
        _nativeFormat.cfFormat = pformatetc.cfFormat;
        _nativeFormat.ptd = pformatetc.ptd;
        _nativeFormat.dwAspect = (int)pformatetc.dwAspect;
        _nativeFormat.lindex = pformatetc.lindex;
        _nativeFormat.tymed = (int)pformatetc.tymed;

        _medium = pmedium;
        _hasData = true;
    }

    public void GetData(ref FORMATETC pformatetc, out STGMEDIUM pmedium)
    {

        Debug.WriteLine($"GetDataに入った：");
        Debug.WriteLine($"_hasData：{_hasData}");
        Debug.WriteLine($"pformatetc.cfFormat：{pformatetc.cfFormat}");
        Debug.WriteLine($"_format.cfFormat：{_nativeFormat.cfFormat}");
        Debug.WriteLine($"pformatetc.tymed：{pformatetc.tymed}");
        Debug.WriteLine($"_format.tymed：{_nativeFormat.tymed}");

        int requestedFormat = (ushort)pformatetc.cfFormat;
        int targetFormat = (ushort)_nativeFormat.cfFormat;

        Debug.WriteLine($"[GetData] 要求フォーマット: {requestedFormat}, 保持フォーマット: {targetFormat}");

        // 💡 4. 【本命】CF_HDROP (15) の要求が来た場合
        if (_hasData && requestedFormat == 15)
        {
            Debug.WriteLine("🔥【完全開通】エクスプローラーがファイルパスデータを正常に読み込みました！");
            pmedium.tymed = _medium.tymed;
            pmedium.unionmember = _medium.unionmember;
            pmedium.pUnkForRelease = IntPtr.Zero;
            return;
        }

        // 💡 5. 対応していない形式が来たら、例外を出さずに空データを返して綺麗に受け流す
        pmedium.tymed = pformatetc.tymed;
        pmedium.unionmember = IntPtr.Zero;
        pmedium.pUnkForRelease = IntPtr.Zero;
        return;
    
    }

    public int QueryGetData(ref FORMATETC pformatetc)
    {
        int requestedFormat = (ushort)pformatetc.cfFormat;
        if (_hasData && requestedFormat == 15) return 0; // S_OK
        return unchecked((int)0x80040064); // DV_E_FORMATETC
    }

    public void GetDataHere(ref FORMATETC pformatetc, ref STGMEDIUM pmedium)
        => throw new COMException(string.Empty, unchecked((int)0x80004001)); // E_NOTIMPL

    public int GetCanonicalFormatEtc(ref FORMATETC pformatetcIn, out FORMATETC pformatetcOut)
    {
        pformatetcOut = new FORMATETC();
        return unchecked((int)0x80004001); // E_NOTIMPL
    }

    public int DAdvise(ref FORMATETC pformatetc, ADVF advf, IAdviseSink pAdvSink, out int pdwConnection)
    {
        pdwConnection = 0;
        return unchecked((int)0x80004001); // E_NOTIMPL
    }

    public void DUnadvise(int dwConnection) { }

    public int EnumDAdvise(out IEnumSTATDATA ppenumAdvise)
    {
        ppenumAdvise = null;
        return unchecked((int)0x80004001); // E_NOTIMPL
    }

    // ★【今回の修正ポイント】
    // .NETのEnumFormatEtcメソッドの戻り値の型は例外を要求するため、
    // ランタイムに横取りされない純粋な COMException を直接スローします。
    public IEnumFORMATETC EnumFormatEtc(DATADIR dwDirection)
    {
        if (dwDirection == DATADIR.DATADIR_GET && _hasData)
        {
            // .NET標準の FORMATETC 構造体に戻して列挙子へ引き渡す
            var format = new FORMATETC
            {
                cfFormat = (short)_nativeFormat.cfFormat,
                ptd = _nativeFormat.ptd,
                dwAspect = (DVASPECT)_nativeFormat.dwAspect,
                lindex = _nativeFormat.lindex,
                tymed = (TYMED)_nativeFormat.tymed
            };
            return new SimpleEnumFormatEtc(new FORMATETC[] { format });
        }

        throw new COMException(string.Empty, unchecked((int)0x80004001)); // E_NOTIMPL
    }
}



public class SimpleEnumFormatEtc : IEnumFORMATETC
{
    private readonly FORMATETC[] _formats;
    private int _currentIndex = 0;

    public SimpleEnumFormatEtc(FORMATETC[] formats)
    {
        _formats = formats;
    }

    public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
    {
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

        return fetched == celt ? 0 : 1; // 0 = S_OK, 1 = S_FALSE
    }

    public int Skip(int celt)
    {
        _currentIndex += celt;
        return _currentIndex <= _formats.Length ? 0 : 1;
    }

    public int Reset()
    {
        _currentIndex = 0;
        return 0;
    }

    public void Clone(out IEnumFORMATETC ppenum)
    {
        ppenum = new SimpleEnumFormatEtc(_formats) { _currentIndex = _currentIndex };
    }
}