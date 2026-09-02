using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;

namespace CloudLight.Presence.Tests;

internal static class WpfTestHost
{
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Ready = new();
    private static Application? _application;
    private static Dispatcher? _dispatcher;
    private static Exception? _startupException;
    private static bool _started;

    public static Task RunAsync(Action action)
    {
        EnsureStarted();
        return _dispatcher!.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    private static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
            var thread = new Thread(RunDispatcher) { IsBackground = true, Name = "CloudLight WPF UI test host" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        Ready.Wait();
        if (_startupException is not null) throw new InvalidOperationException("WPF 测试宿主初始化失败。", _startupException);
    }

    private static void RunDispatcher()
    {
        try
        {
            _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _application.Resources = LoadApplicationResources();
            _dispatcher = Dispatcher.CurrentDispatcher;
            Ready.Set();
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            _startupException = exception;
            Ready.Set();
        }
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var appXaml = FindAppXaml();
        var document = XDocument.Load(appXaml);
        var root = document.Root ?? throw new InvalidOperationException("App.xaml 根节点缺失。");
        var resources = root.Elements().Single(element => element.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal));
        var dictionary = new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"));
        foreach (var namespaceDeclaration in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            var value = namespaceDeclaration.Value;
            if (namespaceDeclaration.Name.LocalName == "behaviors" && !value.Contains("assembly=", StringComparison.Ordinal))
                value += ";assembly=CloudLight.XiaoMi";
            dictionary.Add(new XAttribute(namespaceDeclaration.Name, value));
        }
        dictionary.Add(resources.Nodes());
        var result = AssertResourceDictionary(XamlReader.Parse(dictionary.ToString(SaveOptions.DisableFormatting)));
        foreach (var type in new object[]
        {
            typeof(Window), typeof(Button), typeof(TextBox), typeof(PasswordBox), typeof(ComboBox),
            typeof(ComboBoxItem), typeof(CheckBox), typeof(RadioButton), typeof(ListBoxItem),
            typeof(ListBox), typeof(ScrollBar), typeof(Slider)
        })
            result.Remove(type);
        return result;
    }

    private static string FindAppXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "CloudLight.Presence.App", "App.xaml");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到生产 App.xaml。", "App.xaml");
    }

    private static ResourceDictionary AssertResourceDictionary(object value) =>
        value as ResourceDictionary ?? throw new InvalidOperationException("生产 App.xaml 未加载为 ResourceDictionary。");
}
