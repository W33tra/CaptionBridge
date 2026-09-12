using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class Program
{
    [MTAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "list")
            {
                Console.WriteLine(JsonSerializer.Serialize(AudioSessions.ListActive()));
                return 0;
            }

            if (args.Length == 2 && args[0] == "capture" && uint.TryParse(args[1], out uint pid) && pid > 0)
            {
                await ProcessLoopback.CaptureAsync(pid);
                return 0;
            }

            if (args.Length == 1 && args[0] == "caption-windows")
            {
                Console.WriteLine(JsonSerializer.Serialize(CaptionWindows.Find()));
                return 0;
            }

            if (args.Length == 1 && args[0] == "caption-source")
            {
                Console.WriteLine(JsonSerializer.Serialize(CaptionOverlay.Probe()));
                return 0;
            }

            if (args.Length == 5 && args[0] == "overlay"
                && int.TryParse(args[1], out int width)
                && int.TryParse(args[2], out int height)
                && int.TryParse(args[3], out int fontSize)
                && int.TryParse(args[4], out int opacity))
            {
                CaptionOverlay.Run(width, height, fontSize, opacity);
                return 0;
            }

            Console.Error.WriteLine("Использование: CaptionBridge.Host <list|capture PID|caption-windows|caption-source|overlay WIDTH HEIGHT FONT_SIZE OPACITY>");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

internal sealed record AudioSessionInfo(uint Pid, string Name, string? WindowTitle);
internal sealed record CaptionWindowInfo(long Handle, uint Pid, string Title, int X, int Y, int Width, int Height);
internal sealed record CaptionSourceInfo(bool WindowFound, bool TextElementFound, int TextLength);

internal static class CaptionWindows
{
    public static IReadOnlyList<CaptionWindowInfo> Find()
    {
        var windows = new List<CaptionWindowInfo>();
        Native.EnumWindows((handle, _) =>
        {
            if (!Native.IsWindowVisible(handle))
                return true;

            Native.GetWindowThreadProcessId(handle, out uint pid);
            try
            {
                using Process process = Process.GetProcessById((int)pid);
                if (!process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                return true;
            }

            string className = Native.ReadClassName(handle);
            string title = Native.ReadWindowText(handle);
            string normalizedTitle = title.ToLowerInvariant();
            bool captionTitle = normalizedTitle.Contains("live caption") || normalizedTitle.Contains("субтитр");
            if (!className.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) || !captionTitle)
                return true;

            if (Native.GetWindowRect(handle, out WindowRect rect))
            {
                windows.Add(new CaptionWindowInfo(
                    handle.ToInt64(), pid, title, rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top));
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

}

internal static class CaptionOverlay
{
    public static CaptionSourceInfo Probe()
    {
        bool windowFound = CaptionWindows.Find().Count > 0;
        AutomationElement? element = CaptionOverlayForm.FindTextElement();
        if (element is null)
            return new CaptionSourceInfo(windowFound, false, 0);

        int length = CaptionOverlayForm.ReadCaptionText(element).Length;
        return new CaptionSourceInfo(windowFound, true, length);
    }

    public static void Run(int width, int height, int fontSize, int opacity)
    {
        if (width is < 320 or > 2400 || height is < 120 or > 1400)
            throw new ArgumentOutOfRangeException(nameof(width), "Размер оверлея вне допустимого диапазона.");
        if (fontSize is < 12 or > 96)
            throw new ArgumentOutOfRangeException(nameof(fontSize), "Размер шрифта должен быть от 12 до 96.");
        if (opacity is < 30 or > 100)
            throw new ArgumentOutOfRangeException(nameof(opacity), "Прозрачность должна быть от 30 до 100 процентов.");

        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.Run(new CaptionOverlayForm(width, height, fontSize, opacity));
            }
            catch (Exception error)
            {
                threadError = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (threadError is not null)
            throw threadError;
    }
}

internal sealed class CaptionOverlayForm : Form
{
    private readonly Label _caption;
    private readonly System.Windows.Forms.Timer _timer;
    private AutomationElement? _textElement;
    private string _lastText = "";
    private int _emptyTicks;

    public CaptionOverlayForm(int width, int height, int fontSize, int opacity)
    {
        Text = "CaptionBridge — субтитры";
        Size = new System.Drawing.Size(width, height);
        MinimumSize = new System.Drawing.Size(320, 120);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        TopMost = true;
        BackColor = System.Drawing.Color.Black;
        Opacity = opacity / 100.0;
        ShowInTaskbar = true;

        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, width, height);
        Location = new System.Drawing.Point(
            Math.Max(workingArea.Left, workingArea.Left + (workingArea.Width - width) / 2),
            workingArea.Top + 40);

        _caption = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 10, 18, 10),
            BackColor = System.Drawing.Color.Black,
            ForeColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Segoe UI", fontSize, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel),
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            Text = "Ожидание текста Chrome Live Caption…",
        };
        Controls.Add(_caption);

        Shown += (_, _) =>
        {
            BringToFront();
            Activate();
        };

        _timer = new System.Windows.Forms.Timer { Interval = 120 };
        _timer.Tick += (_, _) => RefreshCaption();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }

    private void RefreshCaption()
    {
        try
        {
            if (_textElement is null)
                _textElement = FindTextElement();

            string text = _textElement is null ? "" : ReadCaptionText(_textElement);
            if (text.Length > 0)
            {
                _emptyTicks = 0;
                if (!string.Equals(text, _lastText, StringComparison.Ordinal))
                {
                    _lastText = text;
                    _caption.Text = text;
                }
            }
            else if (++_emptyTicks >= 15 && _lastText.Length == 0)
            {
                _caption.Text = "Ожидание речи…";
            }
        }
        catch (ElementNotAvailableException)
        {
            _textElement = null;
        }
        catch (InvalidOperationException)
        {
            _textElement = null;
        }
    }

    internal static AutomationElement? FindTextElement()
    {
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "AnnounceTextView"),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "AnnounceTextView"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "CaptionBubbleLabel"));
        foreach (CaptionWindowInfo window in CaptionWindows.Find())
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(new IntPtr(window.Handle));
                AutomationElement? element = root.FindFirst(TreeScope.Descendants, condition);
                if (element is not null)
                    return element;
            }
            catch (ElementNotAvailableException)
            {
                // Chrome может пересоздать плашку в момент обновления текста.
            }
        }
        return null;
    }

    internal static string ReadCaptionText(AutomationElement element)
    {
        if (element.Current.ClassName == "CaptionBubbleLabel")
        {
            var textCondition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Text);
            AutomationElementCollection fragments = element.FindAll(TreeScope.Children, textCondition);
            string[] latest = fragments.Cast<AutomationElement>()
                .Select(fragment => fragment.Current.Name?.Trim() ?? "")
                .Where(text => text.Length > 0)
                .TakeLast(4)
                .ToArray();
            if (latest.Length > 0)
                return string.Join(" ", latest);
        }
        return element.Current.Name?.Trim() ?? "";
    }
}

internal static class AudioSessions
{
    public static IReadOnlyList<AudioSessionInfo> ListActive()
    {
        var found = new Dictionary<uint, AudioSessionInfo>();
        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            HResult.Check(deviceEnumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out devices));
            HResult.Check(devices.GetCount(out uint deviceCount));

            for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                IMMDevice? device = null;
                object? managerObject = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    HResult.Check(devices.Item(deviceIndex, out device));
                    Guid managerId = typeof(IAudioSessionManager2).GUID;
                    HResult.Check(device.Activate(ref managerId, ClsCtx.All, IntPtr.Zero, out managerObject));
                    var manager = (IAudioSessionManager2)managerObject;
                    HResult.Check(manager.GetSessionEnumerator(out sessions));
                    HResult.Check(sessions.GetCount(out int sessionCount));

                    for (int sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                    {
                        IAudioSessionControl? session = null;
                        try
                        {
                            HResult.Check(sessions.GetSession(sessionIndex, out session));
                            var session2 = (IAudioSessionControl2)session;
                            HResult.Check(session2.GetState(out AudioSessionState state));
                            if (state != AudioSessionState.Active || session2.IsSystemSoundsSession() == 0)
                                continue;

                            HResult.Check(session2.GetProcessId(out uint pid));
                            if (pid == 0 || found.ContainsKey(pid))
                                continue;

                            try
                            {
                                using Process process = Process.GetProcessById((int)pid);
                                string name = process.ProcessName;
                                string? title = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle;
                                found[pid] = new AudioSessionInfo(pid, name, title);
                            }
                            catch
                            {
                                found[pid] = new AudioSessionInfo(pid, $"Process {pid}", null);
                            }
                        }
                        finally
                        {
                            Com.Release(session);
                        }
                    }
                }
                catch (COMException)
                {
                    // An endpoint can disappear while Windows is enumerating it.
                }
                finally
                {
                    Com.Release(sessions);
                    Com.Release(managerObject);
                    Com.Release(device);
                }
            }
        }
        finally
        {
            Com.Release(devices);
            Com.Release(deviceEnumerator);
        }

        return found.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Pid).ToArray();
    }
}

internal static class ProcessLoopback
{
    private const string ProcessLoopbackDevice = @"VAD\Process_Loopback";
    private const uint StreamFlags = 0x00020000 | 0x00040000 | 0x80000000 | 0x08000000;
    private const uint SilentBuffer = 0x2;
    private const int FramesPerSecond = 44100;
    private const ushort Channels = 2;
    private const ushort BitsPerSample = 16;

    public static async Task CaptureAsync(uint pid)
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        using var sampleReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        try
        {
            audioClient = await ActivateAsync(pid);
            var format = new WaveFormatEx
            {
                FormatTag = 1,
                Channels = Channels,
                SamplesPerSec = FramesPerSecond,
                BitsPerSample = BitsPerSample,
                BlockAlign = Channels * BitsPerSample / 8,
                AvgBytesPerSec = FramesPerSecond * Channels * BitsPerSample / 8,
                ExtraSize = 0,
            };

            HResult.Check(audioClient.Initialize(AudioClientShareMode.Shared, StreamFlags, 0, 0, ref format, IntPtr.Zero));
            HResult.Check(audioClient.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()));
            Guid captureId = typeof(IAudioCaptureClient).GUID;
            HResult.Check(audioClient.GetService(ref captureId, out object captureObject));
            captureClient = (IAudioCaptureClient)captureObject;
            HResult.Check(audioClient.Start());

            Stream output = Console.OpenStandardOutput();
            int bytesPerFrame = format.BlockAlign;
            while (true)
            {
                sampleReady.WaitOne();
                HResult.Check(captureClient.GetNextPacketSize(out uint availableFrames));
                while (availableFrames > 0)
                {
                    HResult.Check(captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    try
                    {
                        int byteCount = checked((int)frames * bytesPerFrame);
                        byte[] bytes = new byte[byteCount];
                        if ((flags & SilentBuffer) == 0)
                            Marshal.Copy(data, bytes, 0, byteCount);
                        await output.WriteAsync(bytes);
                    }
                    finally
                    {
                        HResult.Check(captureClient.ReleaseBuffer(frames));
                    }
                    HResult.Check(captureClient.GetNextPacketSize(out availableFrames));
                }
            }
        }
        finally
        {
            if (audioClient is not null)
                audioClient.Stop();
            Com.Release(captureClient);
            Com.Release(audioClient);
        }
    }

    private static async Task<IAudioClient> ActivateAsync(uint pid)
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new ProcessLoopbackParams
            {
                TargetProcessId = pid,
                ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree,
            },
        };
        IntPtr parameterData = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        Marshal.StructureToPtr(parameters, parameterData, false);
        var variant = new PropVariant
        {
            VariantType = 0x0041, // VT_BLOB
            Blob = new Blob { Size = Marshal.SizeOf<AudioClientActivationParams>(), Data = parameterData },
        };
        var completion = new ActivationCompletion();
        try
        {
            Guid audioClientId = typeof(IAudioClient).GUID;
            HResult.Check(Native.ActivateAudioInterfaceAsync(
                ProcessLoopbackDevice,
                ref audioClientId,
                ref variant,
                completion,
                out IActivateAudioInterfaceAsyncOperation operation));
            completion.KeepAlive(operation);
            return await completion.Task;
        }
        finally
        {
            Marshal.FreeHGlobal(parameterData);
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler
{
    private readonly TaskCompletionSource<IAudioClient> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IActivateAudioInterfaceAsyncOperation? _operation;

    public Task<IAudioClient> Task => _completion.Task;
    public void KeepAlive(IActivateAudioInterfaceAsyncOperation operation) => _operation = operation;

    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            HResult.Check(operation.GetActivateResult(out int activationResult, out object activated));
            HResult.Check(activationResult);
            _completion.TrySetResult((IAudioClient)activated);
        }
        catch (Exception error)
        {
            _completion.TrySetException(error);
        }
        return 0;
    }
}

internal static class HResult
{
    public static void Check(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }
}

internal static class Com
{
    public static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}

internal static class Native
{
    internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid riid,
        ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    internal static string ReadWindowText(IntPtr window)
    {
        var text = new System.Text.StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    internal static string ReadClassName(IntPtr window)
    {
        var className = new System.Text.StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        return className.ToString();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public ProcessLoopbackParams ProcessLoopbackParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessLoopbackParams
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort VariantType;
    [FieldOffset(8)] public Blob Blob;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Blob
{
    public int Size;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

internal enum AudioClientActivationType { Default, ProcessLoopback }
internal enum ProcessLoopbackMode { IncludeTargetProcessTree, ExcludeTargetProcessTree }
internal enum AudioClientShareMode { Shared, Exclusive }
internal enum EDataFlow { Render, Capture, All }
internal enum AudioSessionState { Inactive, Active, Expired }

[Flags]
internal enum DeviceState : uint { Active = 0x1 }

[Flags]
internal enum ClsCtx : uint { All = 0x17 }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, ClsCtx context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
    [PreserveSig] int GetId(out IntPtr id);
    [PreserveSig] int GetState(out DeviceState state);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IntPtr sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    [PreserveSig] int RegisterSessionNotification(IntPtr notification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName(out IntPtr displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetIconPath(out IntPtr iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName(out IntPtr displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetIconPath(out IntPtr iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int GetSessionIdentifier(out IntPtr sessionId);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr instanceId);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(AudioClientShareMode shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(AudioClientShareMode shareMode, ref WaveFormatEx format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid serviceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
