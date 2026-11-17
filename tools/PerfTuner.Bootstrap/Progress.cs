using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PerfTuner.Bootstrap;

/// <summary>
/// A Windows task dialog with a moving progress bar while <c>work</c> runs on a worker thread (documented
/// TaskDialogIndirect, Common Controls 6). The dialog closes itself when the work is done; errors are re-thrown.
/// </summary>
internal static unsafe partial class Progress
{
    private const int TdfAllowDialogCancellation = 0x0008, TdfShowMarqueeProgressBar = 0x0400, TdfCallbackTimer = 0x0800, TdfPositionRelativeToWindow = 0x1000;
    private const int TdnCreated = 0, TdnTimer = 4;
    private const uint WmUser = 0x0400, TdmClickButton = WmUser + 102, TdmSetProgressBarMarquee = WmUser + 107;
    private const int IdCancel = 2, TdcbfCancelButton = 0x0008;

    private static volatile bool _done;

    public static void Run(string title, string heading, string text, Action work)
    {
        _done = false;
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
            finally { _done = true; }
        }) { IsBackground = true, Name = "unpack" };
        worker.Start();

        fixed (char* pTitle = title)
        fixed (char* pHeading = heading)
        fixed (char* pText = text)
        {
            var config = new TaskDialogConfig
            {
                cbSize = (uint)sizeof(TaskDialogConfig),
                dwFlags = TdfShowMarqueeProgressBar | TdfCallbackTimer | TdfPositionRelativeToWindow,
                dwCommonButtons = TdcbfCancelButton,
                pszWindowTitle = pTitle,
                pszMainInstruction = pHeading,
                pszContent = pText,
                pfCallback = &Callback,
            };
            var cancelled = false;
            try
            {
                int button;
                if (TaskDialogIndirect(&config, &button, null, null) == 0 && button == IdCancel && !_done) { cancelled = true; }
            }
            catch (EntryPointNotFoundException)
            {
                // no Common Controls 6 (shouldn't happen on Windows 10/11): just wait without a dialog
            }
            worker.Join();
            if (error is not null) { ExceptionDispatchInfoThrow(error); }
            if (cancelled) { Environment.Exit(1223); }
        }
    }

    private static void ExceptionDispatchInfoThrow(Exception ex) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Callback(nint hwnd, uint msg, nint wParam, nint lParam, nint data)
    {
        if (msg == TdnCreated) { _ = SendMessageW(hwnd, TdmSetProgressBarMarquee, 1, 30); }
        else if (msg == TdnTimer && _done) { _ = PostMessageW(hwnd, TdmClickButton, IdCancel, 0); }
        return 0;
    }

    // TASKDIALOGCONFIG is declared with 1-byte packing in commctrl.h.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogConfig
    {
        public uint cbSize;
        public nint hwndParent;
        public nint hInstance;
        public int dwFlags;
        public int dwCommonButtons;
        public char* pszWindowTitle;
        public nint hMainIcon;
        public char* pszMainInstruction;
        public char* pszContent;
        public uint cButtons;
        public nint pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public nint pRadioButtons;
        public int nDefaultRadioButton;
        public char* pszVerificationText;
        public char* pszExpandedInformation;
        public char* pszExpandedControlText;
        public char* pszCollapsedControlText;
        public nint hFooterIcon;
        public char* pszFooter;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int> pfCallback;
        public nint lpCallbackData;
        public uint cxWidth;
    }

    [LibraryImport("comctl32.dll")]
    private static partial int TaskDialogIndirect(TaskDialogConfig* config, int* button, int* radio, int* verificationChecked);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
