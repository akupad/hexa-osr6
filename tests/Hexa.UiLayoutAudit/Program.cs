using System.Windows.Markup;
using System.Xml.Linq;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hexa.Models;
using Hexa.Services;
using Hexa.ViewModels;
using Hexa.Views;
using HApp = Hexa.App;

internal static class Program
{
    private static readonly List<object> Checks = new();
    private static string Output = "";
    private static int Failures;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]);
            Output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Output);
            Environment.SetEnvironmentVariable("HEXA_SETTINGS_PATH", Path.Combine(Output, "hexa-selftest-ui", "settings.json"));
            Environment.SetEnvironmentVariable("HEXA_SELFTEST", "1");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var document = XDocument.Load(Path.Combine(root, "App.xaml"));
            var resources = new XElement(document.Root!.Element(ns + "Application.Resources")!.Elements().Single());
            foreach (var attribute in document.Root.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                string value = attribute.Value;
                if (value.StartsWith("clr-namespace:") && !value.Contains(";assembly=")) value += ";assembly=Hexa";
                resources.SetAttributeValue(attribute.Name, value);
            }
            // Resource-only host: omit application mouse-wheel handlers, never run Hexa startup.
            resources.Descendants(ns + "EventSetter").Remove();
            application.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            var cfg = new AppSettings
            {
                SimulationMode = true, AutoConnect = false, GlobalHotkeysEnabled = false,
                AudioReactiveEnabled = false, AudioListenOnly = false, RuleEngineEnabled = false,
                CompanionAuto = false, AmbientOverlay = false, GateCompanionOnSilence = false,
                ScreenWatchEnabled = false, WebApiEnabled = false, GameBridgeEnabled = false,
                LovenseBridgeEnabled = false, OverlayVisible = false,
                CompositionFolder = Path.Combine(Output, "hexa-selftest-ui", "compositions"),
                ScriptFolder = Path.Combine(Output, "hexa-selftest-ui", "scripts")
            };
            SetApp("Settings", cfg);
            using var serial = new SerialService();
            serial.SetSimulationMode(true);
            SetApp("Serial", serial);
            using var engine = new MotionEngine(serial, cfg);
            SetApp("Engine", engine);
            using var hotkeys = new HotkeyService();
            SetApp("Hotkeys", hotkeys);
            using var audio = new AudioReactiveService(engine, cfg);
            SetApp("AudioReactive", audio);
            using var telemetry = new GameTelemetryService(port: 0);
            SetApp("GameTelemetry", telemetry);
            using var rules = new RuleEngine(engine, cfg, audio, telemetry);
            SetApp("RuleEngine", rules);
            using var bridge = new IntifaceBridgeService(serial, engine, cfg);
            SetApp("Bridge", bridge);
            SetApp("PlaygroundVm", new PlaygroundViewModel(engine, cfg));
            SetApp("SettingsVm", new SettingsViewModel(serial, engine, cfg));
            AuditPage(new PlaygroundPage(), "play", new[] {
                ("LovenseFoldBtn", "LovenseFoldBody"), ("BridgeSectionBtn", "BridgeSectionBody"),
                ("GamepadFoldBtn", "GamepadBody"), ("AutoParamsToggleBtn", "AutoParamsBody") });
            AuditPage(new SettingsPage(), "settings", new[] {
                ("OverlayFoldBtn", "OverlayFoldBody"), ("HotkeysFoldBtn", "HotkeysFoldBody"),
                ("WebApiFoldBtn", "WebApiFoldBody"), ("AiFoldBtn", "AiFoldBody"),
                ("DiagnosticsFoldBtn", "DiagnosticsFoldBody"), ("LogFoldBtn", "LogFoldBody"),
                ("DeviceTestFoldBtn", "DeviceTestFoldBody"), ("LimitsFoldBtn", "LimitsFoldBody"),
                ("StorageFoldBtn", "StorageFoldBody"), ("FirmwareFoldBtn", "FirmwareFoldBody"),
                ("AboutFoldBtn", "AboutFoldBody") });
            using (var screenWatch = new ScreenWatchService(cfg))
            {
                screenWatch.BeginManualBinding();
                var manualDialog = new ManualBindingWindow(screenWatch);
                manualDialog.Measure(new Size(820, 560));
                manualDialog.Arrange(new Rect(0, 0, 820, 560));
                manualDialog.UpdateLayout();
                Check("manual-dialog.target-step", manualDialog.FindName("TargetStep") is Button, manualDialog.FindName("TargetStep")?.GetType().Name ?? "missing");
                Check("manual-dialog.shallow-deep-steps", manualDialog.FindName("ShallowStep") is Button && manualDialog.FindName("DeepStep") is Button, new { shallow = manualDialog.FindName("ShallowStep")?.GetType().Name, deep = manualDialog.FindName("DeepStep")?.GetType().Name });
                Check("manual-dialog.apply-requires-three-points", manualDialog.FindName("ApplyBindingButton") is Button apply && !apply.IsEnabled, new { apply = manualDialog.FindName("ApplyBindingButton")?.GetType().Name, enabled = (manualDialog.FindName("ApplyBindingButton") as Button)?.IsEnabled });
                Check("manual-dialog.resample-action", manualDialog.FindName("RefreshFrameButton") is Button, manualDialog.FindName("RefreshFrameButton")?.GetType().Name ?? "missing");
                screenWatch.EndManualBinding();
            }
            Check("fixture.output-disabled", !serial.OutputEnabled, new { serial.OutputEnabled, telemetry.Port });
            SaveResult();
            Console.WriteLine($"UI audit: {Checks.Count} checks, {Failures} failures; {Output}");
            return Failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (Output.Length > 0) File.WriteAllText(Path.Combine(Output, "exception.txt"), ex.ToString());
            return 2;
        }
    }

    private static void SetApp(string name, object value) => typeof(HApp).GetProperty(name)!.SetValue(null, value);
    private static T Find<T>(Page page, string name) where T : class => (page.FindName(name) as T) ?? throw new Exception($"Missing {page.GetType().Name}.{name}");
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Layout(Page page, double width, double height = 900)
    {
        page.Width = width;
        page.Height = height;
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();
        Pump();
        page.UpdateLayout();
    }
    private static void AuditPage(Page page, string label, (string Button, string Body)[] folds)
    {
        Layout(page, 1280);
        foreach (var fold in folds)
        {
            var button = Find<Button>(page, fold.Button);
            var body = Find<FrameworkElement>(page, fold.Body);
            Check(label + "." + fold.Body + ".default", body.Visibility == Visibility.Collapsed, body.Visibility.ToString());
            for (int cycle = 0; cycle < 2; cycle++)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(label + "." + fold.Body + ".open" + cycle, body.Visibility == Visibility.Visible, body.Visibility.ToString());
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(label + "." + fold.Body + ".close" + cycle, body.Visibility == Visibility.Collapsed, body.Visibility.ToString());
            }
        }
        if (label == "play")
        {
            Layout(page, 1280, 980);
            var scroll = (ScrollViewer)page.Content;
            Check("play.one-screen.1280x980", scroll.ScrollableHeight <= 1,
                new { scroll.ScrollableHeight, scroll.ExtentHeight, scroll.ViewportHeight });
            SaveImage(page, "play-1280x980-folded.png");
        }
        foreach (double width in new[] { 1280.0, 940.0, 939.0, 900.0, 899.0, 760.0, 620.0, 619.0, 560.0, 1280.0 })
        {
            Layout(page, width);
            var scroll = (ScrollViewer)page.Content;
            string rightName = label == "settings" ? "SettingsRightColumn" : "RightCard";
            var right = Find<FrameworkElement>(page, rightName);
            int row = Grid.GetRow(right);
            int expected = label == "settings" ? (width >= 940 ? 0 : 1) : (width >= 620 ? 0 : 1);
            Check($"{label}.layout.{width}", row == expected, new { width, row, expected, right.ActualWidth, scroll.ViewportWidth, scroll.ExtentWidth, scroll.ScrollableWidth });
            Check($"{label}.scroll.{width}", scroll.ScrollableWidth < 1, new { scroll.ScrollableWidth });
            if (label == "play" && width < 620)
            {
                var grid = Find<Grid>(page, "MainGrid");
                var left = Find<StackPanel>(page, "LeftStack");
                Check($"play.stacked-full-width.{width}", Math.Abs(left.ActualWidth - grid.ActualWidth) < 1 && Math.Abs(right.ActualWidth - grid.ActualWidth) < 1, new { gridWidth = grid.ActualWidth, leftWidth = left.ActualWidth, rightWidth = right.ActualWidth });
            }
            SaveImage(page, $"{label}-{width}-folded.png");
        }
        foreach (var fold in folds) Find<Button>(page, fold.Button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        foreach (double width in new[] { 1280.0, 760.0, 560.0 })
        {
            Layout(page, width);
            var scroll = (ScrollViewer)page.Content;
            Check($"{label}.expanded-scroll.{width}", scroll.ScrollableWidth < 1, new { scroll.ScrollableWidth, scroll.ExtentWidth, scroll.ViewportWidth });
            SaveImage(page, $"{label}-{width}-expanded.png");
        }
        if (label == "play")
        {
            Find<TextBlock>(page, "LovenseStatusText").Text = "FIXTURE: bridge status";
            Pump();
            var header = Find<Button>(page, "LovenseFoldBtn");
            Check("play.status-binding", Descendants(header).OfType<TextBlock>().Any(t => t.Text == "FIXTURE: bridge status"), "LovenseStatusText -> header");
        }
        else
        {
            Find<TextBlock>(page, "OverlayStatusText").Text = "FIXTURE: overlay status";
            Find<TextBlock>(page, "LogCountText").Text = "FIXTURE: log count";
            Pump();
            Check("settings.overlay-binding", Descendants(Find<Button>(page, "OverlayFoldBtn")).OfType<TextBlock>().Any(t => t.Text == "FIXTURE: overlay status"), "OverlayStatusText -> header");
            Check("settings.log-binding", Descendants(Find<Button>(page, "LogFoldBtn")).OfType<TextBlock>().Any(t => t.Text == "FIXTURE: log count"), "LogCountText -> header");
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(item, i))) yield return child;
    }
    private static void SaveImage(Page page, string name)
    {
        var bitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, name));
        encoder.Save(stream);
    }
    private static void Check(string name, bool passed, object evidence)
    {
        Checks.Add(new { name, passed, evidence });
        if (!passed) { Failures++; Console.WriteLine("FAIL " + name + ": " + JsonSerializer.Serialize(evidence)); }
    }
    private static void SaveResult() => File.WriteAllText(Path.Combine(Output, "result.json"), JsonSerializer.Serialize(new { checks = Checks, failures = Failures }, new JsonSerializerOptions { WriteIndented = true }));
}

