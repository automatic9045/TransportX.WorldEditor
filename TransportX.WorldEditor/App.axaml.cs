using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using TransportX.WorldEditor.Serialization.Import;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels;
using TransportX.WorldEditor.Views;
using TransportX.WorldEditor.Views.Dialogs;

namespace TransportX.WorldEditor;

/// <summary>
/// Application entry point configuring Avalonia styles, top-level dialogs, and the main workspace shell.
/// </summary>
public partial class App : Application
{
    private static bool SpinnerHandlerRegistered;

    public override void Initialize()
    {
        if (!SpinnerHandlerRegistered)
        {
            SpinnerHandlerRegistered = true;
            TemplatedControl.TemplateAppliedEvent.AddClassHandler<ButtonSpinner>(static (spinner, e) =>
            {
                if (e.NameScope.Find<RepeatButton>("PART_IncreaseButton") is RepeatButton inc)
                {
                    ConfigureCompactSpinnerButton(inc);
                }

                if (e.NameScope.Find<RepeatButton>("PART_DecreaseButton") is RepeatButton dec)
                {
                    ConfigureCompactSpinnerButton(dec);
                }
            });
        }

        AvaloniaXamlLoader.Load(this);
    }

    private static void ConfigureCompactSpinnerButton(RepeatButton button)
    {
        button.MinWidth = 16;
        button.Width = 16;
        button.Padding = new Thickness(1, 0);
        button.FontSize = 9;

        if (button.Content is PathIcon pathIcon)
        {
            pathIcon.Width = 7;
            pathIcon.Height = 7;
            pathIcon.Margin = new Thickness(0);
        }
        else if (button.Content is Path pathShape)
        {
            pathShape.Width = 7;
            pathShape.Height = 5;
            pathShape.Stretch = Stretch.Uniform;
            pathShape.Margin = new Thickness(0);
        }
        else if (button.Content is Control contentControl)
        {
            contentControl.MaxWidth = 8;
            contentControl.MaxHeight = 8;
            contentControl.Margin = new Thickness(0);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow? mainWindow = null;
            AvaloniaStorageDialogService dialogService = new AvaloniaStorageDialogService(() => mainWindow);

            async Task<NewWorldPromptResult?> PromptNewWorldAsync()
            {
                if (mainWindow is null)
                {
                    return null;
                }

                NewWorldDialogWindow dialog = new NewWorldDialogWindow();
                bool? confirmed = await dialog.ShowDialog<bool?>(mainWindow);
                if (confirmed == true && dialog.Confirmed)
                {
                    return new NewWorldPromptResult(
                        dialog.AuthorText,
                        dialog.WorldKeyText,
                        dialog.TitleText,
                        dialog.DescriptionText);
                }

                return null;
            }

            async Task<WorldImportOptions?> PromptImportWorldAsync()
            {
                if (mainWindow is null)
                {
                    return null;
                }

                ImportWorldDialogWindow dialog = new(dialogService);
                bool? confirmed = await dialog.ShowDialog<bool?>(mainWindow);
                if (confirmed == true && dialog.Confirmed)
                {
                    return dialog.ResultOptions;
                }

                return null;
            }

            MainWindowViewModel mainViewModel = new(
                dialogService,
                PromptNewWorldAsync,
                null,
                PromptImportWorldAsync);
            mainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };

            mainViewModel.CloseRequested += mainWindow.Close;
            desktop.MainWindow = mainWindow;
            desktop.Exit += (_, _) => mainViewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
