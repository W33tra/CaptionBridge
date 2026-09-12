using System.Diagnostics;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length is < 2 or > 3 || args[0] != "run")
                return 2;

            string settingsPath = Path.GetFullPath(args[1]);
            string statePath = args.Length == 3
                ? Path.GetFullPath(args[2])
                : Path.Combine(Path.GetDirectoryName(settingsPath)!, "overlay-state.json");
            OverlayOptions options = OverlayOptions.Load(settingsPath);
            System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.Run(new CaptionOverlayForm(settingsPath, statePath, options));
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}

internal sealed class OverlayOptions
{
    public int Width { get; set; } = 900;
    public int Height { get; set; } = 220;
    public int FontSize { get; set; } = 32;
    public string FontFamily { get; set; } = "Chrome";
    public string? FontFile { get; set; }
    public int Opacity { get; set; } = 90;
    public bool ResetPosition { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool ClickThrough { get; set; }
    public string NativeCaptionPlacement { get; set; } = "leave";

    public static OverlayOptions Load(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        var options = JsonSerializer.Deserialize<OverlayOptions>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Пустые настройки оверлея.");
        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (Width is < 320 or > 2400 || Height is < 50 or > 1400)
            throw new InvalidDataException("Недопустимый размер оверлея.");
        if (FontSize is < 12 or > 96)
            throw new InvalidDataException("Недопустимый размер шрифта.");
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 80)
            throw new InvalidDataException("Недопустимое семейство шрифта.");
        if (FontFile is not null
            && (!File.Exists(FontFile)
                || Path.GetExtension(FontFile).ToLowerInvariant() is not (".ttf" or ".otf")))
            throw new InvalidDataException("Недопустимый файл пользовательского шрифта.");
        if (Opacity is < 30 or > 100)
            throw new InvalidDataException("Недопустимая непрозрачность.");
        if (NativeCaptionPlacement is not ("leave" or "edge-right" or "offscreen-right" or "restore"))
            throw new InvalidDataException("Недопустимое положение штатного окна субтитров.");
    }
}

internal sealed class CaptionOverlayForm : Form
{
    private const int HorizontalTextPadding = 18;
    private const int VerticalTextPadding = 10;
    private const int WindowHitTest = 0x0084;
    private const int HitTransparent = -1;
    private const int HitClient = 1;
    private const int HitCaption = 2;
    private const int HitLeft = 10;
    private const int HitRight = 11;
    private const int HitTop = 12;
    private const int HitTopLeft = 13;
    private const int HitTopRight = 14;
    private const int HitBottom = 15;
    private const int HitBottomLeft = 16;
    private const int HitBottomRight = 17;
    private const int ResizeGrip = 8;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;
    private const uint ShowWindow = 0x0040;
    private const long TransparentExtendedStyle = 0x00000020L;
    private const long LayeredExtendedStyle = 0x00080000L;
    private const long NoActivateExtendedStyle = 0x08000000L;
    private static readonly IntPtr TopMostWindow = new(-1);

    private readonly string _settingsPath;
    private readonly string _statePath;
    private readonly System.Windows.Forms.Timer _timer;
    private System.Drawing.Font? _ownedFont;
    private PrivateFontCollection? _privateFonts;
    private int? _fontSize;
    private string? _fontChoice;
    private string? _fontFile;
    private OverlayOptions _options;
    private AutomationElement? _textElement;
    private DateTime _settingsModifiedUtc;
    private string _lastText = "";
    private string _displayText = "Ожидание текста Chrome Live Caption…";
    private int _emptyTicks;
    private int _topmostTicks;
    private System.Drawing.Rectangle? _savedBounds;
    private bool _clickThrough;

    public CaptionOverlayForm(string settingsPath, string statePath, OverlayOptions options)
    {
        _settingsPath = settingsPath;
        _statePath = statePath;
        _options = options;
        Text = "CaptionBridge — субтитры";
        AutoScaleMode = AutoScaleMode.None;
        MinimumSize = new System.Drawing.Size(320, 50);
        MaximumSize = new System.Drawing.Size(2400, 1400);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        BackColor = System.Drawing.Color.Black;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer,
            true);
        ApplyOptions(options);

        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, options.Width, options.Height);
        Location = new System.Drawing.Point(
            Math.Max(workingArea.Left, workingArea.Left + (workingArea.Width - Width) / 2),
            workingArea.Top + 40);

        _settingsModifiedUtc = File.GetLastWriteTimeUtc(_settingsPath);
        _timer = new System.Windows.Forms.Timer { Interval = 120 };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        ApplyWindowBounds(_options, _options.ResetPosition);
        ApplyClickThrough(_options.ClickThrough);
        SaveWindowStateIfChanged();
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        RefreshDisplayedText();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.Clear(System.Drawing.Color.Black);
        System.Drawing.Font font = _ownedFont ?? Font;
        var textArea = new System.Drawing.Rectangle(
            HorizontalTextPadding,
            VerticalTextPadding,
            Math.Max(1, ClientSize.Width - HorizontalTextPadding * 2),
            Math.Max(1, ClientSize.Height - VerticalTextPadding * 2));
        const TextFormatFlags flags = TextFormatFlags.WordBreak
            | TextFormatFlags.Left
            | TextFormatFlags.Bottom
            | TextFormatFlags.NoPadding
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.PreserveGraphicsClipping;
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _displayText,
            font,
            textArea,
            System.Drawing.Color.White,
            System.Drawing.Color.Black,
            flags);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != WindowHitTest || message.Result.ToInt32() != HitClient)
            return;

        if (_clickThrough)
        {
            message.Result = new IntPtr(HitTransparent);
            return;
        }

        long coordinates = message.LParam.ToInt64();
        var screenPoint = new System.Drawing.Point(
            unchecked((short)(coordinates & 0xffff)),
            unchecked((short)((coordinates >> 16) & 0xffff)));
        System.Drawing.Point point = PointToClient(screenPoint);
        bool left = point.X < ResizeGrip;
        bool right = point.X >= ClientSize.Width - ResizeGrip;
        bool top = point.Y < ResizeGrip;
        bool bottom = point.Y >= ClientSize.Height - ResizeGrip;

        message.Result = (left, right, top, bottom) switch
        {
            (true, _, true, _) => new IntPtr(HitTopLeft),
            (_, true, true, _) => new IntPtr(HitTopRight),
            (true, _, _, true) => new IntPtr(HitBottomLeft),
            (_, true, _, true) => new IntPtr(HitBottomRight),
            (true, _, _, _) => new IntPtr(HitLeft),
            (_, true, _, _) => new IntPtr(HitRight),
            (_, _, true, _) => new IntPtr(HitTop),
            (_, _, _, true) => new IntPtr(HitBottom),
            _ => new IntPtr(HitCaption),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CaptionText.PlaceNativeWindows("restore");
            _timer.Dispose();
            _ownedFont?.Dispose();
            _privateFonts?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnTimerTick()
    {
        RefreshSettings();
        RefreshCaption();
        SaveWindowStateIfChanged();
        if (++_topmostTicks >= 2)
        {
            _topmostTicks = 0;
            KeepAboveOtherWindows();
            CaptionText.PlaceNativeWindows(_options.NativeCaptionPlacement);
        }
    }

    private void KeepAboveOtherWindows()
    {
        if (IsHandleCreated)
        {
            Native.SetWindowPos(
                Handle,
                TopMostWindow,
                0,
                0,
                0,
                0,
                NoMove | NoSize | NoActivate | ShowWindow);
        }
    }

    private void RefreshSettings()
    {
        try
        {
            DateTime modifiedUtc = File.GetLastWriteTimeUtc(_settingsPath);
            if (modifiedUtc <= _settingsModifiedUtc)
                return;
            OverlayOptions options = OverlayOptions.Load(_settingsPath);
            ApplyOptions(options);
            _settingsModifiedUtc = modifiedUtc;
            KeepAboveOtherWindows();
        }
        catch (IOException)
        {
            // Python заменяет файл атомарно; повторим чтение на следующем тике.
        }
        catch (JsonException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (ExternalException)
        {
        }
    }

    private void ApplyOptions(OverlayOptions options)
    {
        System.Drawing.Font? newFont = null;
        PrivateFontCollection? newPrivateFonts = null;
        bool fontChanged = _fontSize != options.FontSize
            || !string.Equals(_fontChoice, options.FontFamily, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_fontFile, options.FontFile, StringComparison.OrdinalIgnoreCase);
        if (fontChanged)
            (newFont, newPrivateFonts) = CreateFont(options.FontFamily, options.FontFile, options.FontSize);

        _options = options;
        if (IsHandleCreated)
            ApplyWindowBounds(options, options.ResetPosition);
        else
            Size = new System.Drawing.Size(options.Width, options.Height);
        Opacity = options.Opacity / 100.0;
        if (IsHandleCreated)
            ApplyClickThrough(options.ClickThrough);
        if (fontChanged)
        {
            System.Drawing.Font? oldFont = _ownedFont;
            PrivateFontCollection? oldPrivateFonts = _privateFonts;
            _ownedFont = newFont!;
            _privateFonts = newPrivateFonts;
            _fontSize = options.FontSize;
            _fontChoice = options.FontFamily;
            _fontFile = options.FontFile;
            oldFont?.Dispose();
            oldPrivateFonts?.Dispose();
        }
        RefreshDisplayedText();
    }

    private static (System.Drawing.Font Font, PrivateFontCollection? PrivateFonts) CreateFont(
        string fontChoice,
        string? fontFile,
        int fontSize)
    {
        if (!string.IsNullOrWhiteSpace(fontFile))
        {
            var privateFonts = new PrivateFontCollection();
            try
            {
                privateFonts.AddFontFile(fontFile);
                if (privateFonts.Families.Length == 0)
                    throw new ArgumentException("В файле не найдено семейство шрифта.");
                System.Drawing.FontFamily family = privateFonts.Families[0];
                System.Drawing.FontStyle style = new[]
                {
                    System.Drawing.FontStyle.Regular,
                    System.Drawing.FontStyle.Bold,
                    System.Drawing.FontStyle.Italic,
                    System.Drawing.FontStyle.Bold | System.Drawing.FontStyle.Italic,
                }.FirstOrDefault(family.IsStyleAvailable);
                if (!family.IsStyleAvailable(style))
                    throw new ArgumentException("В файле не найдено поддерживаемое начертание шрифта.");
                var font = new System.Drawing.Font(
                    family,
                    fontSize,
                    style,
                    System.Drawing.GraphicsUnit.Pixel);
                return (font, privateFonts);
            }
            catch
            {
                privateFonts.Dispose();
                throw;
            }
        }

        if (!fontChoice.Equals("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            return (
                new System.Drawing.Font(
                    fontChoice,
                    fontSize,
                    System.Drawing.FontStyle.Regular,
                    System.Drawing.GraphicsUnit.Pixel),
                null);
        }

        foreach (string candidate in new[] { "Roboto", "Arial" })
        {
            var font = new System.Drawing.Font(
                candidate,
                fontSize,
                System.Drawing.FontStyle.Regular,
                System.Drawing.GraphicsUnit.Pixel);
            if (font.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return (font, null);
            font.Dispose();
        }
        return (
            new System.Drawing.Font(
                "Segoe UI",
                fontSize,
                System.Drawing.FontStyle.Regular,
                System.Drawing.GraphicsUnit.Pixel),
            null);
    }

    private void ApplyWindowBounds(OverlayOptions options, bool resetPosition)
    {
        var requestedBounds = new System.Drawing.Rectangle(
            options.X ?? Left,
            options.Y ?? Top,
            options.Width,
            options.Height);
        System.Drawing.Rectangle area = resetPosition
            ? (Screen.PrimaryScreen?.WorkingArea ?? Screen.FromRectangle(Bounds).WorkingArea)
            : options.X.HasValue && options.Y.HasValue
                ? Screen.FromRectangle(requestedBounds).WorkingArea
                : Screen.FromRectangle(Bounds).WorkingArea;
        int requestedX = resetPosition
            ? area.Left + (area.Width - options.Width) / 2
            : options.X ?? Left;
        int requestedY = resetPosition ? area.Top + 40 : options.Y ?? Top;
        int maximumX = Math.Max(area.Left, area.Right - options.Width);
        int maximumY = Math.Max(area.Top, area.Bottom - options.Height);
        int safeX = Math.Clamp(requestedX, area.Left, maximumX);
        int safeY = Math.Clamp(requestedY, area.Top, maximumY);
        Native.SetWindowPos(
            Handle,
            TopMostWindow,
            safeX,
            safeY,
            options.Width,
            options.Height,
            NoActivate | ShowWindow);
    }

    private void ApplyClickThrough(bool enabled)
    {
        if (!IsHandleCreated || _clickThrough == enabled)
            return;

        long style = Native.GetExtendedWindowStyle(Handle);
        long clickThroughFlags = TransparentExtendedStyle | NoActivateExtendedStyle;
        style = enabled
            ? style | clickThroughFlags | LayeredExtendedStyle
            : style & ~clickThroughFlags;
        Native.SetExtendedWindowStyle(Handle, style);
        Native.SetWindowPos(
            Handle,
            TopMostWindow,
            0,
            0,
            0,
            0,
            NoMove | NoSize | NoActivate | FrameChanged | ShowWindow);
        _clickThrough = enabled;
    }

    private void SaveWindowStateIfChanged()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized || Bounds == _savedBounds)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            string temporaryPath = _statePath + ".tmp";
            string json = JsonSerializer.Serialize(new
            {
                x = Left,
                y = Top,
                width = Width,
                height = Height,
            });
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, _statePath, true);
            _savedBounds = Bounds;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RefreshCaption()
    {
        try
        {
            if (_textElement is null)
                _textElement = CaptionText.FindElement();

            string text = _textElement is null ? "" : CaptionText.Read(_textElement);
            if (text.Length > 0)
            {
                _emptyTicks = 0;
                if (!string.Equals(text, _lastText, StringComparison.Ordinal))
                {
                    _lastText = text;
                    RefreshDisplayedText();
                }
            }
            else if (++_emptyTicks >= 15 && _lastText.Length == 0)
            {
                _displayText = "Ожидание речи…";
                Invalidate();
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
        catch (COMException)
        {
            _textElement = null;
        }
    }

    private void RefreshDisplayedText()
    {
        if (_lastText.Length == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        string[] chromeLines = _lastText
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (chromeLines.Length == 0)
            return;

        int availableWidth = Math.Max(1, ClientSize.Width - HorizontalTextPadding * 2);
        int availableHeight = Math.Max(1, ClientSize.Height - VerticalTextPadding * 2);
        var proposedSize = new System.Drawing.Size(availableWidth, 10_000);
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        System.Drawing.Font font = _ownedFont ?? Font;
        string fitted = FitLastWords(chromeLines[^1], font, proposedSize, availableHeight, flags);

        for (int index = chromeLines.Length - 2; index >= 0; index--)
        {
            string candidate = chromeLines[index] + Environment.NewLine + fitted;
            System.Drawing.Size measured = TextRenderer.MeasureText(candidate, font, proposedSize, flags);
            if (measured.Height > availableHeight)
                break;
            fitted = candidate;
        }
        _displayText = fitted;
        Invalidate();
    }

    private static string FitLastWords(
        string line,
        System.Drawing.Font font,
        System.Drawing.Size proposedSize,
        int availableHeight,
        TextFormatFlags flags)
    {
        if (TextRenderer.MeasureText(line, font, proposedSize, flags).Height <= availableHeight)
            return line;

        string[] words = line.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string fitted = words.Length > 0 ? words[^1] : line;
        for (int index = words.Length - 2; index >= 0; index--)
        {
            string candidate = words[index] + " " + fitted;
            if (TextRenderer.MeasureText(candidate, font, proposedSize, flags).Height > availableHeight)
                break;
            fitted = candidate;
        }
        return fitted;
    }
}

internal static class CaptionText
{
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindow = 0x0040;
    private static readonly Dictionary<IntPtr, System.Drawing.Rectangle> OriginalBounds = new();
    private static readonly Dictionary<IntPtr, System.Drawing.Rectangle> OriginalWorkingAreas = new();

    public static AutomationElement? FindElement()
    {
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "AnnounceTextView"),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "AnnounceTextView"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "CaptionBubbleLabel"));

        foreach (IntPtr handle in FindChromeCaptionWindows())
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(handle);
                AutomationElement? element = root.FindFirst(TreeScope.Descendants, condition);
                if (element is not null)
                    return element;
            }
            catch (ElementNotAvailableException)
            {
            }
        }
        return null;
    }

    public static string Read(AutomationElement element)
    {
        if (element.Current.ClassName == "CaptionBubbleLabel")
        {
            var textCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
            AutomationElementCollection fragments = element.FindAll(TreeScope.Children, textCondition);
            string[] latest = fragments.Cast<AutomationElement>()
                .Select(fragment => fragment.Current.Name?.Trim() ?? "")
                .Where(text => text.Length > 0)
                .TakeLast(12)
                .ToArray();
            if (latest.Length > 0)
                return string.Join(Environment.NewLine, latest);
        }
        return element.Current.Name?.Trim() ?? "";
    }

    public static void PlaceNativeWindows(string placement)
    {
        if (placement == "leave")
            return;

        foreach (IntPtr handle in FindChromeCaptionWindows())
        {
            if (!Native.TryGetWindowRectangle(handle, out System.Drawing.Rectangle bounds))
                continue;

            System.Drawing.Rectangle workingArea;
            if (placement != "restore")
            {
                if (!OriginalBounds.ContainsKey(handle))
                {
                    workingArea = Screen.FromRectangle(bounds).WorkingArea;
                    if (IsMeaningfullyVisible(bounds, workingArea))
                    {
                        OriginalBounds[handle] = bounds;
                        OriginalWorkingAreas[handle] = workingArea;
                    }
                }
                workingArea = OriginalWorkingAreas.TryGetValue(handle, out System.Drawing.Rectangle savedArea)
                    ? savedArea
                    : Screen.FromRectangle(bounds).WorkingArea;
            }
            else if (OriginalWorkingAreas.TryGetValue(handle, out System.Drawing.Rectangle savedArea))
            {
                workingArea = savedArea;
            }
            else
            {
                workingArea = Screen.FromRectangle(bounds).WorkingArea;
            }

            int x;
            int y;
            if (placement == "restore")
            {
                if (OriginalBounds.TryGetValue(handle, out System.Drawing.Rectangle original))
                {
                    x = original.Left;
                    y = original.Top;
                }
                else
                {
                    if (IsMeaningfullyVisible(bounds, workingArea))
                        continue;
                    x = workingArea.Left + Math.Max(0, (workingArea.Width - bounds.Width) / 2);
                    y = Math.Max(workingArea.Top, workingArea.Bottom - bounds.Height - 40);
                }
            }
            else
            {
                System.Drawing.Rectangle destinationArea = placement == "edge-right"
                    ? Screen.AllScreens.OrderBy(screen => screen.Bounds.Right).Last().WorkingArea
                    : workingArea;
                x = placement == "edge-right"
                    ? destinationArea.Right - 1
                    : SystemInformation.VirtualScreen.Right + 16;
                y = Math.Clamp(
                    bounds.Top,
                    destinationArea.Top,
                    Math.Max(destinationArea.Top, destinationArea.Bottom - bounds.Height));
            }

            Native.SetWindowPos(
                handle,
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                NoSize | NoZOrder | NoActivate | ShowWindow);

            if (placement == "restore")
            {
                OriginalBounds.Remove(handle);
                OriginalWorkingAreas.Remove(handle);
            }
        }
    }

    private static bool IsMeaningfullyVisible(
        System.Drawing.Rectangle bounds,
        System.Drawing.Rectangle workingArea)
    {
        System.Drawing.Rectangle intersection = System.Drawing.Rectangle.Intersect(bounds, workingArea);
        return intersection.Width >= 32 && intersection.Height >= 32;
    }

    private static IReadOnlyList<IntPtr> FindChromeCaptionWindows()
    {
        var result = new List<IntPtr>();
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
            string title = Native.ReadWindowText(handle).ToLowerInvariant();
            bool isCaption = title.Contains("live caption") || title.Contains("субтитр");
            if (className.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) && isCaption)
                result.Add(handle);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}

internal static class Native
{
    private const int ExtendedStyleIndex = -20;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLong64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLong64(IntPtr window, int index, IntPtr value);

    internal static long GetExtendedWindowStyle(IntPtr window)
    {
        return IntPtr.Size == 8
            ? GetWindowLong64(window, ExtendedStyleIndex).ToInt64()
            : GetWindowLong32(window, ExtendedStyleIndex);
    }

    internal static void SetExtendedWindowStyle(IntPtr window, long value)
    {
        if (IntPtr.Size == 8)
            SetWindowLong64(window, ExtendedStyleIndex, new IntPtr(value));
        else
            SetWindowLong32(window, ExtendedStyleIndex, unchecked((int)value));
    }

    internal static bool TryGetWindowRectangle(IntPtr window, out System.Drawing.Rectangle rectangle)
    {
        if (GetWindowRect(window, out WindowRectangle nativeRectangle))
        {
            rectangle = System.Drawing.Rectangle.FromLTRB(
                nativeRectangle.Left,
                nativeRectangle.Top,
                nativeRectangle.Right,
                nativeRectangle.Bottom);
            return true;
        }
        rectangle = System.Drawing.Rectangle.Empty;
        return false;
    }

    internal static string ReadWindowText(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    internal static string ReadClassName(IntPtr window)
    {
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        return className.ToString();
    }
}
