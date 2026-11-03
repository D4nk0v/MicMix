using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MicMix.ViewModels;
using Border = System.Windows.Controls.Border;
using Page = MicMix.ViewModels.Page;

// Offscreen: no window is shown, no audio starts, no hotkeys are registered, the user's settings are not touched.
string outDir = args.Length > 0 ? args[0] : "docs/screenshots";
int height = args.Length > 1 ? int.Parse(args[1]) : 700;
const int Width = 960;
Directory.CreateDirectory(outDir);

var thread = new Thread(() =>
{
    // Relative pack:// URIs (/Assets/MicMix.ico) resolve against the entry assembly; point them at MicMix.
    typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
        .SetValue(null, typeof(MicMix.App).Assembly);
    var app = new MicMix.App();
    app.InitializeComponent();

    void Render(MainViewModel vm, string file)
    {
        var window = new MicMix.MainWindow(vm);
        var content = (UIElement)window.Content;
        window.Content = null;
        var root = new Border { Background = (Brush)app.Resources["WindowBrush"], Child = content, DataContext = vm, Width = Width, Height = height };
        TextElement.SetForeground(root, (Brush)app.Resources["TextBrush"]); // normally inherited from the Window
        TextElement.SetFontFamily(root, window.FontFamily);
        root.Measure(new Size(Width, height));
        root.Arrange(new Rect(0, 0, Width, height));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap(Width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bmp.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(Path.Combine(outDir, file))) png.Save(f);
        Console.WriteLine("saved " + file);
        vm.Dispose();
    }

    Render(MainViewModel.CreateDemo(Page.Mixer, setupWizard: true), "0-setup.png");
    foreach (var page in Enum.GetValues<Page>())
        Render(MainViewModel.CreateDemo(page), $"{(int)page + 1}-{page.ToString().ToLowerInvariant()}.png");
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
