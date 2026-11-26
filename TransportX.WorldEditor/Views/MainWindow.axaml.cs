using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using R3;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels;
using TransportX.WorldEditor.Views.Controls;
using TransportX.WorldEditor.Views.Dialogs;

namespace TransportX.WorldEditor.Views;

/// <summary>
/// Main editor window hosting the docking workspace and toolbars.
/// </summary>
public partial class MainWindow : Window
{
    private IDisposable? StartingCursorSubscription;
    private bool ForceCloseApproved;
    private bool IsExitPromptOpen;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);
    }

    private static CSharpCodeEditor? FindContainingCodeEditor(IInputElement? element)
    {
        return element is Visual visual ? visual.FindAncestorOfType<CSharpCodeEditor>() : null;
    }

    private static bool IsTextInputFocused(IInputElement? element)
    {
        return element is TextBox || FindContainingCodeEditor(element) is not null;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            IInputElement? focused = FocusManager?.GetFocusedElement();
            if (IsTextInputFocused(focused))
            {
                return;
            }

            if (vm.Workspace.CancelActiveInteraction())
            {
                e.Handled = true;
            }
        }
    }

    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        EditorGlobalShortcut? matched = null;
        if (ctrl && e.Key == Key.S)
        {
            matched = shift ? EditorGlobalShortcut.SaveAll : EditorGlobalShortcut.Save;
        }
        else if (ctrl && !shift && e.Key == Key.N)
        {
            matched = EditorGlobalShortcut.NewWorld;
        }
        else if (ctrl && !shift && e.Key == Key.O)
        {
            matched = EditorGlobalShortcut.OpenWorld;
        }
        else if (e.Key == Key.F5 && !shift)
        {
            matched = ctrl ? EditorGlobalShortcut.LaunchPlayer : EditorGlobalShortcut.Verify;
        }
        else if (ctrl && !shift && (e.Key == Key.Z || e.Key == Key.Y))
        {
            if (IsTextInputFocused(FocusManager?.GetFocusedElement()))
            {
                return;
            }

            matched = e.Key == Key.Z ? EditorGlobalShortcut.Undo : EditorGlobalShortcut.Redo;
        }

        if (!matched.HasValue)
        {
            return;
        }

        EditorGlobalShortcut shortcut = matched.Value;
        IInputElement? focused = FocusManager?.GetFocusedElement();
        CSharpCodeEditor? focusedCodeEditor = FindContainingCodeEditor(focused);
        bool flushFocusedTextInput = (focused is TextBox || focusedCodeEditor is not null)
            && (shortcut == EditorGlobalShortcut.Save
                || shortcut == EditorGlobalShortcut.SaveAll
                || shortcut == EditorGlobalShortcut.Verify
                || shortcut == EditorGlobalShortcut.LaunchPlayer);

        if (flushFocusedTextInput)
        {
            focusedCodeEditor?.CommitPendingText();
            Focus();
            vm.ExecuteGlobalShortcut(shortcut);
            focused?.Focus();
        }
        else
        {
            vm.ExecuteGlobalShortcut(shortcut);
        }

        e.Handled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!ForceCloseApproved
            && DataContext is MainWindowViewModel vm
            && vm.ShouldConfirmExit())
        {
            e.Cancel = true;
            if (!IsExitPromptOpen)
            {
                _ = PromptAndCloseAsync(vm);
            }

            return;
        }

        base.OnClosing(e);
    }

    private async Task PromptAndCloseAsync(MainWindowViewModel vm)
    {
        IsExitPromptOpen = true;
        try
        {
            bool allowClose = await vm.ResolveExitAsync(ShowUnsavedChangesDialogAsync);
            if (allowClose)
            {
                ForceCloseApproved = true;
                Close();
            }
        }
        finally
        {
            IsExitPromptOpen = false;
        }
    }

    private async Task<UnsavedChangesExitChoice> ShowUnsavedChangesDialogAsync(IReadOnlyList<string> dirtyFiles)
    {
        UnsavedChangesDialogWindow dialog = new();
        dialog.SetDirtyFiles(dirtyFiles);
        return await dialog.ShowDialog<UnsavedChangesExitChoice>(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        StartingCursorSubscription?.Dispose();
        StartingCursorSubscription = null;

        if (DataContext is MainWindowViewModel vm)
        {
            StartingCursorSubscription = vm.IsVerificationStarting.Subscribe(starting =>
            {
                Cursor = starting ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
            });
        }
        else
        {
            Cursor = Cursor.Default;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StartingCursorSubscription?.Dispose();
        StartingCursorSubscription = null;
        base.OnClosed(e);
    }
}
