using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Attached behavior for <see cref="NumericUpDown"/> that commits deferred <c>UpdateSourceTrigger=LostFocus</c>
/// bindings on <see cref="Key.Enter"/> and spinner button clicks instead of every intermediate keystroke.
/// </summary>
public static class NumericUpDownCommitBehavior
{
    public static readonly AttachedProperty<bool> CommitOnLostFocusOrEnterProperty =
        AvaloniaProperty.RegisterAttached<NumericUpDown, bool>(
            "CommitOnLostFocusOrEnter",
            typeof(NumericUpDownCommitBehavior));

    static NumericUpDownCommitBehavior()
    {
        CommitOnLostFocusOrEnterProperty.Changed.AddClassHandler<NumericUpDown>(OnPropertyChanged);
    }

    public static bool GetCommitOnLostFocusOrEnter(NumericUpDown element)
    {
        return element.GetValue(CommitOnLostFocusOrEnterProperty);
    }

    public static void SetCommitOnLostFocusOrEnter(NumericUpDown element, bool value)
    {
        element.SetValue(CommitOnLostFocusOrEnterProperty, value);
    }

    private static void OnPropertyChanged(NumericUpDown numeric, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is bool enabled && enabled)
        {
            numeric.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
            numeric.Spinned += OnSpinned;
            numeric.LostFocus += OnLostFocus;
        }
        else
        {
            numeric.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            numeric.Spinned -= OnSpinned;
            numeric.LostFocus -= OnLostFocus;
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is NumericUpDown numeric && (e.Key == Key.Enter || e.Key == Key.Return))
        {
            Dispatcher.UIThread.Post(() => CommitSource(numeric), DispatcherPriority.Input);
        }
    }

    private static void OnSpinned(object? sender, SpinEventArgs e)
    {
        if (sender is NumericUpDown numeric)
        {
            Dispatcher.UIThread.Post(() => CommitSource(numeric), DispatcherPriority.Input);
        }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is NumericUpDown numeric)
        {
            CommitSource(numeric);
        }
    }

    public static void CommitSource(NumericUpDown numeric)
    {
        BindingExpressionBase? expr = BindingOperations.GetBindingExpressionBase(numeric, NumericUpDown.ValueProperty);
        expr?.UpdateSource();
    }
}
