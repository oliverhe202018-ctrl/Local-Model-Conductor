using System.IO;
using System.Reflection;
using System.Windows;
using LocalLlmConsole.Localization;
using Xunit;

namespace LocalLlmConsole.UiTests;

public sealed class LocalizationScreenshotTests : WpfUiTestBase
{
    [Fact]
    public async Task CaptureChineseAndEnglishUiScreenshots()
    {
        await RunStaAsync(() =>
        {
            var targetDir = @"D:\project\本地模型计算\Phase4D_2_Localization\screenshots";
            Directory.CreateDirectory(targetDir);

            foreach (var (lang, filename) in new[] { ("zh", "screenshot_zh_CN.png"), ("en", "screenshot_en_US.png") })
            {
                Loc.LoadLanguage(lang);

                var window = new MainWindow
                {
                    Width = 1280,
                    Height = 850
                };
                DetachLoadedStartup(window);

                try
                {
                    var content = (FrameworkElement)window.Content;
                    void Layout()
                    {
                        content.Measure(new Size(1280, 850));
                        content.Arrange(new Rect(0, 0, 1280, 850));
                        content.UpdateLayout();
                    }

                    Layout();
                    var showGuardMethod = typeof(MainWindow).GetMethod("ShowGuardOperationsCenter", BindingFlags.Instance | BindingFlags.NonPublic);
                    showGuardMethod?.Invoke(window, new object[] { false, true });
                    Layout();

                    var savePath = Path.Combine(targetDir, filename);
                    GuardOperationsCenterController.SaveVisualPng(content, savePath);
                    Assert.True(File.Exists(savePath), $"Expected screenshot file at {savePath}");
                }
                finally
                {
                    typeof(MainWindow)
                        .GetMethod("DisposeTrayIcon", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, null);
                }
            }
            return Task.CompletedTask;
        });
    }
}
