using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class CleanPlayRegression
{
    private const uint ExpectedNativeApiVersion = 22u;

    public static void Run()
    {
        var failures = new List<string>();
        Probe(failures, "ABI v22 synchronization", RequireAbiVersionSynchronization);
        Probe(failures, "native clean-view API", RequireNativeCleanViewApi);
        Probe(failures, "Alt+Space / Escape routing", RequireShortcutContract);
        Probe(failures, "clean-play transport and layout lifecycle", RequireLayoutRoundTrip);
        Probe(failures, "non-destructive toggle path", RequireNonDestructiveTogglePath);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Clean Play is not available:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static void RequireAbiVersionSynchronization()
    {
        Type nativeBridge = typeof(MainWindow).Assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererNative")
            ?? throw new InvalidOperationException("NativeRendererNative is missing.");
        MethodInfo getApiVersion = nativeBridge.GetMethod(
            "GetApiVersion",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NativeRendererNative.GetApiVersion is missing.");
        uint loadedVersion = (uint)(getApiVersion.Invoke(null, null) ?? 0u);
        if (loadedVersion != ExpectedNativeApiVersion)
        {
            throw new InvalidOperationException(
                $"loaded native API is {loadedVersion}; expected {ExpectedNativeApiVersion}.");
        }

        string root = FindRepositoryRoot();
        RequireVersionLiteral(
            Path.Combine(root, "src", "ExtremeEditor.NativeRenderer", "include", "extreme_editor_renderer.h"),
            @"#define\s+EE_RENDERER_API_VERSION\s+22u",
            "native header");
        RequireVersionLiteral(
            Path.Combine(root, "src", "ExtremeEditor.Wpf", "Native", "NativeRendererSession.cs"),
            @"ExpectedApiVersion\s*=\s*22",
            "NativeRendererSession");
        RequireVersionLiteral(
            Path.Combine(root, "src", "ExtremeEditor.Wpf", "Native", "NativeHeadlessRendererSession.cs"),
            @"ExpectedBaseApiVersion\s*=\s*22",
            "NativeHeadlessRendererSession");
        RequireVersionLiteral(
            Path.Combine(root, "src", "ExtremeEditor.Wpf.Tests", "Native", "NativeRendererAbiRegression.cs"),
            @"version\s*!=\s*22u",
            "NativeRendererAbiRegression");
    }

    private static void RequireNativeCleanViewApi()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type nativeBridge = assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererNative")
            ?? throw new InvalidOperationException("NativeRendererNative is missing.");
        RequireMethod(nativeBridge, "SetCleanView", BindingFlags.Static | BindingFlags.NonPublic, typeof(nint), typeof(int));

        Type rendererSession = assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererSession")
            ?? throw new InvalidOperationException("NativeRendererSession is missing.");
        RequireMethod(rendererSession, "SetCleanView", BindingFlags.Instance | BindingFlags.NonPublic, typeof(bool));

        Type headlessSession = assembly.GetType("ExtremeEditor.Wpf.Native.NativeHeadlessRendererSession")
            ?? throw new InvalidOperationException("NativeHeadlessRendererSession is missing.");
        RequireMethod(headlessSession, "SetCleanView", BindingFlags.Instance | BindingFlags.NonPublic, typeof(bool));

        Type viewport = assembly.GetType("ExtremeEditor.Wpf.Native.NativeLevelViewport")
            ?? throw new InvalidOperationException("NativeLevelViewport is missing.");
        RequireMethod(viewport, "SetCleanView", BindingFlags.Instance | BindingFlags.Public, typeof(bool));

        string header = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExtremeEditor.NativeRenderer",
            "include",
            "extreme_editor_renderer.h"));
        if (!header.Contains("ee_renderer_set_clean_view", StringComparison.Ordinal))
            throw new InvalidOperationException("native header does not export ee_renderer_set_clean_view.");
    }

    private static void RequireShortcutContract()
    {
        Type shortcut = typeof(MainWindow).Assembly.GetType("ExtremeEditor.Wpf.CleanPlayShortcut")
            ?? throw new InvalidOperationException("CleanPlayShortcut pure input contract is missing.");
        MethodInfo resolve = shortcut.GetMethod(
            "Resolve",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Key), typeof(ModifierKeys), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("CleanPlayShortcut.Resolve is missing.");

        RequireAction(resolve, Key.Space, ModifierKeys.Alt, false, "Enter");
        RequireAction(resolve, Key.Space, ModifierKeys.Alt, true, "None");
        RequireAction(resolve, Key.Escape, ModifierKeys.None, true, "Exit");
        RequireAction(resolve, Key.Escape, ModifierKeys.None, false, "None");
        RequireAction(resolve, Key.Space, ModifierKeys.None, false, "None");

        string mainWindowSources = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(FindRepositoryRoot(), "src", "ExtremeEditor.Wpf", "MainWindow"),
                    "MainWindow*.cs")
                .Select(File.ReadAllText));
        foreach (string contract in new[] { "WmSysKeyDown", "WmSysCommand", "ScKeyMenu" })
        {
            if (!mainWindowSources.Contains(contract, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"native-child/system-menu routing contract '{contract}' is missing.");
            }
        }
    }

    private static void RequireLayoutRoundTrip()
    {
        MethodInfo setCleanView = typeof(MainWindow).GetMethod(
            "SetCleanPlay",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow.SetCleanPlay(bool) is missing.");
        PropertyInfo isCleanView = typeof(MainWindow).GetProperty(
            "IsCleanPlay",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow.IsCleanPlay is missing.");

        MainWindow window = CreateIsolatedWindow();
        try
        {
            Materialize(window);
            FrameworkElement mainMenu = RequireElement(window, "MainMenu");
            FrameworkElement toolbar = RequireElement(window, "ToolbarHost");
            FrameworkElement settings = RequireElement(window, "SettingsSidebarHost");
            FrameworkElement inspector = RequireElement(window, "EventInspectorHost");
            FrameworkElement status = RequireElement(window, "StatusBarHost");
            FrameworkElement diagnostics = RequireElement(window, "DiagnosticsPanel");
            FrameworkElement viewport = RequireElement(window, "NativeViewport");
            ColumnDefinition railColumn = RequireColumn(window, "SettingsRailColumn");
            ColumnDefinition settingsColumn = RequireColumn(window, "SettingsContentColumn");
            ColumnDefinition viewportColumn = RequireColumn(window, "EditorViewportColumn");
            ColumnDefinition inspectorColumn = RequireColumn(window, "EventInspectorColumn");

            diagnostics.Visibility = Visibility.Collapsed;
            Visibility[] before =
            [
                mainMenu.Visibility, toolbar.Visibility, settings.Visibility,
                inspector.Visibility, status.Visibility, diagnostics.Visibility
            ];
            GridLength[] widths =
            [
                railColumn.Width, settingsColumn.Width, viewportColumn.Width, inspectorColumn.Width
            ];

            object selection = typeof(MainWindow).GetField(
                    "_selection",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
                ?? throw new InvalidOperationException("MainWindow selection state is missing.");
            MethodInfo setSelection = selection.GetType().GetMethod("SetSelection")
                ?? throw new InvalidOperationException("EditorSelectionState.SetSelection is missing.");
            setSelection.Invoke(selection, [new[] { 0, 1 }, 1]);
            object audio = typeof(MainWindow).GetField(
                    "_audio",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
                ?? throw new InvalidOperationException("MainWindow audio transport is missing.");
            audio.GetType().GetMethod("Unload")?.Invoke(audio, null);
            LevelDocument level = (LevelDocument)(typeof(MainWindow).GetField(
                    "_level",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
                ?? throw new InvalidOperationException("MainWindow level is missing."));
            SetField(window, "_timingMap", TimingMapBuilder.Build(level));
            SetField(window, "_silentPlaybackActive", true);
            SetField(window, "_silentPlaybackPlaying", false);
            SetField(window, "_silentPlaybackChartTime", 0.375);

            setCleanView.Invoke(window, [true]);
            if (!Equals(isCleanView.GetValue(window), true))
                throw new InvalidOperationException("clean-view state did not enter.");
            foreach (FrameworkElement hidden in new[] { mainMenu, toolbar, settings, inspector, status, diagnostics })
            {
                if (hidden.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException($"{hidden.Name} remains visible in clean view.");
            }
            if (viewport.Visibility != Visibility.Visible ||
                railColumn.Width.Value != 0 || settingsColumn.Width.Value != 0 || inspectorColumn.Width.Value != 0)
            {
                throw new InvalidOperationException("native viewport does not occupy the clean-view client area.");
            }
            RequireSelection(selection, [0, 1], 1);
            RequireField(window, "_silentPlaybackActive", true, "Clean Play did not retain the current transport.");
            RequireField(window, "_silentPlaybackPlaying", true, "Clean Play did not start playback.");
            RequireNearField(window, "_silentPlaybackChartTime", 0.375, "Clean Play entry rewound the chart position.");

            // Entering while playback is already running must be idempotent rather than
            // using a play/pause toggle that would pause the transport.
            setCleanView.Invoke(window, [true]);
            RequireField(window, "_silentPlaybackPlaying", true, "Clean Play entry toggled active playback off.");

            double positionBeforeExit = GetDoubleProperty(window, "CurrentSilentChartTime");
            setCleanView.Invoke(window, [false]);
            if (!Equals(isCleanView.GetValue(window), false))
                throw new InvalidOperationException("clean-view state did not exit.");
            FrameworkElement[] restored = [mainMenu, toolbar, settings, inspector, status, diagnostics];
            for (int index = 0; index < restored.Length; index++)
            {
                if (restored[index].Visibility != before[index])
                    throw new InvalidOperationException($"{restored[index].Name} visibility was not restored.");
            }
            ColumnDefinition[] columns = [railColumn, settingsColumn, viewportColumn, inspectorColumn];
            for (int index = 0; index < columns.Length; index++)
            {
                if (columns[index].Width != widths[index])
                    throw new InvalidOperationException($"layout column {index} was not restored.");
            }
            RequireSelection(selection, [0, 1], 1);
            RequireField(window, "_silentPlaybackActive", true, "Clean Play exit discarded the retained transport position.");
            RequireField(window, "_silentPlaybackPlaying", false, "Clean Play exit did not stop playback progression.");
            RequireRetainedPosition(window, positionBeforeExit);
        }
        finally
        {
            window.Close();
        }
    }

    private static void RequireNonDestructiveTogglePath()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExtremeEditor.Wpf",
            "MainWindow",
            "MainWindow.CleanPlay.cs");
        if (!File.Exists(path))
            throw new InvalidOperationException("dedicated Clean Play implementation is missing.");

        string source = File.ReadAllText(path);
        foreach (string forbidden in new[]
                 {
                     "SetLevel(", "ShutdownRenderer(", "ReloadAssets(", "ClearSelection(", "SetSelection([]"
                 })
        {
            if (source.Contains(forbidden, StringComparison.Ordinal))
                throw new InvalidOperationException($"clean-view toggle contains destructive operation '{forbidden}'.");
        }
        if (!source.Contains("NativeViewport.SetCleanView", StringComparison.Ordinal))
            throw new InvalidOperationException("clean-view toggle is not forwarded to the existing native viewport.");
    }

    private static void SetField(MainWindow window, string name, object value)
    {
        FieldInfo field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"MainWindow field '{name}' is missing.");
        field.SetValue(window, value);
    }

    private static void RequireField(MainWindow window, string name, bool expected, string message)
    {
        FieldInfo field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"MainWindow field '{name}' is missing.");
        if (!Equals(field.GetValue(window), expected))
            throw new InvalidOperationException(message);
    }

    private static void RequireNearField(MainWindow window, string name, double expected, string message)
    {
        FieldInfo field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"MainWindow field '{name}' is missing.");
        double actual = (double)(field.GetValue(window) ?? double.NaN);
        if (Math.Abs(actual - expected) > 0.000001)
            throw new InvalidOperationException($"{message} expected={expected}, actual={actual}.");
    }

    private static double GetDoubleProperty(MainWindow window, string name)
    {
        PropertyInfo property = typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"MainWindow property '{name}' is missing.");
        return (double)(property.GetValue(window) ?? double.NaN);
    }

    private static void RequireRetainedPosition(MainWindow window, double positionBeforeExit)
    {
        FieldInfo field = typeof(MainWindow).GetField(
                "_silentPlaybackChartTime",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow silent playback position is missing.");
        double retained = (double)(field.GetValue(window) ?? double.NaN);
        if (!double.IsFinite(retained) || retained + 0.000001 < positionBeforeExit ||
            retained - positionBeforeExit > 0.05)
        {
            throw new InvalidOperationException(
                $"Clean Play exit did not retain its current chart position. before={positionBeforeExit}, retained={retained}.");
        }
    }

    private static void RequireAction(
        MethodInfo resolve,
        Key key,
        ModifierKeys modifiers,
        bool active,
        string expected)
    {
        object? action = resolve.Invoke(null, [key, modifiers, active]);
        if (!string.Equals(action?.ToString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{modifiers}+{key} with active={active} resolved to '{action}', expected '{expected}'.");
        }
    }

    private static void RequireSelection(object selection, int[] expectedFloors, int expectedPrimary)
    {
        object selected = selection.GetType().GetProperty("SelectedFloors")?.GetValue(selection)
            ?? throw new InvalidOperationException("SelectedFloors is missing.");
        int[] actualFloors = ((IEnumerable<int>)selected).OrderBy(value => value).ToArray();
        int actualPrimary = (int)(selection.GetType().GetProperty("PrimaryFloor")?.GetValue(selection) ?? -1);
        if (!actualFloors.SequenceEqual(expectedFloors) || actualPrimary != expectedPrimary)
            throw new InvalidOperationException("clean-view toggle changed the selection state.");
    }

    private static void RequireVersionLiteral(string path, string pattern, string owner)
    {
        string source = File.ReadAllText(path);
        if (!Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant))
            throw new InvalidOperationException($"{owner} is not synchronized to API version 22.");
    }

    private static void RequireMethod(
        Type type,
        string name,
        BindingFlags flags,
        params Type[] parameterTypes)
    {
        if (type.GetMethod(name, flags, binder: null, types: parameterTypes, modifiers: null) is null)
            throw new InvalidOperationException($"{type.Name}.{name} is missing.");
    }

    private static FrameworkElement RequireElement(MainWindow window, string name) =>
        window.FindName(name) as FrameworkElement
        ?? throw new InvalidOperationException($"MainWindow element '{name}' is missing.");

    private static ColumnDefinition RequireColumn(MainWindow window, string name) =>
        window.FindName(name) as ColumnDefinition
        ?? throw new InvalidOperationException($"MainWindow column '{name}' is missing.");

    private static void Materialize(MainWindow window)
    {
        window.ApplyTemplate();
        window.Measure(new Size(1500, 900));
        window.Arrange(new Rect(0, 0, 1500, 900));
        window.UpdateLayout();
    }

    private static MainWindow CreateIsolatedWindow()
    {
        string previousDirectory = Environment.CurrentDirectory;
        string isolatedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"extreme-editor-clean-play-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedDirectory);
        try
        {
            Environment.CurrentDirectory = isolatedDirectory;
            return new MainWindow();
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static void Probe(List<string> failures, string contract, Action action)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            failures.Add($"{contract}: {ex.InnerException.Message}");
        }
        catch (Exception ex)
        {
            failures.Add($"{contract}: {ex.Message}");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(current.FullName, "src", "ExtremeEditor.Wpf.Tests")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
